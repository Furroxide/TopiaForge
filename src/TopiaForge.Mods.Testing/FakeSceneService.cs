using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic typed scene state with optional manual asynchronous completion.</summary>
    public sealed class FakeSceneService : ISceneService
    {
        private readonly FakeModEvents events;
        private readonly FakeModLifetime lifetime;
        private readonly Action<string>? legacyNotification;
        private readonly List<SceneSnapshot> loaded = new List<SceneSnapshot>();
        private readonly Dictionary<string, SceneLoadMode> loadedModes =
            new Dictionary<string, SceneLoadMode>(StringComparer.Ordinal);
        private readonly List<string> history = new List<string>();
        private readonly Queue<PendingLoad> pending = new Queue<PendingLoad>();
        private readonly List<Action<CheckpointSnapshot>> checkpointHandlers =
            new List<Action<CheckpointSnapshot>>();
        private CheckpointSnapshot? checkpoint;

        /// <summary>Creates fake scene state.</summary>
        public FakeSceneService(
            FakeModEvents events,
            FakeModLifetime lifetime,
            string initialScene = "TestCityStartMenu",
            Action<string>? legacyNotification = null)
        {
            this.events = events ?? throw new ArgumentNullException(nameof(events));
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            if (!string.IsNullOrWhiteSpace(initialScene))
            {
                loaded.Add(new SceneSnapshot(initialScene, true, true));
                loadedModes[initialScene] = SceneLoadMode.Single;
                history.Add(initialScene);
            }

            this.legacyNotification = legacyNotification;
            lifetime.Defer(CancelPendingLoads);
        }

        /// <summary>Gets whether scene loads complete immediately; default is <see langword="true"/>.</summary>
        public bool CompleteLoadsImmediately { get; set; } = true;

        /// <summary>Gets the active scene name, or an empty string when no scene is loaded.</summary>
        public string ActiveScene
        {
            get
            {
                return TryGetActive(out var scene) ? scene!.Name : string.Empty;
            }
        }

        /// <summary>Gets every successfully loaded scene name in order.</summary>
        public IReadOnlyList<string> History => history.AsReadOnly();

        /// <summary>Gets the number of manually pending consumer results.</summary>
        public int PendingLoadCount
        {
            get
            {
                var count = 0;
                foreach (var item in pending)
                {
                    if (!item.Operation.IsCompleted)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Gets the number of active checkpoint subscriptions.</summary>
        public int ActiveCheckpointSubscriptionCount => checkpointHandlers.Count;

        /// <inheritdoc/>
        public bool TryGetActive(out SceneSnapshot? scene)
        {
            foreach (var candidate in loaded)
            {
                if (candidate.IsActive)
                {
                    scene = candidate;
                    return true;
                }
            }

            scene = null;
            return false;
        }

        /// <inheritdoc/>
        public IReadOnlyList<SceneSnapshot> GetLoadedScenes() =>
            new List<SceneSnapshot>(loaded).AsReadOnly();

        /// <inheritdoc/>
        public bool TryGetCheckpoint(out CheckpointSnapshot? current)
        {
            current = checkpoint;
            return current != null;
        }

        /// <inheritdoc/>
        public IDisposable SubscribeCheckpointChanged(Action<CheckpointSnapshot> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            checkpointHandlers.Add(handler);
            return lifetime.Track(new CheckpointSubscription(() => checkpointHandlers.Remove(handler)));
        }

        /// <summary>Changes the current checkpoint and notifies subscribers in registration order.</summary>
        public void SetCheckpoint(CheckpointSnapshot current)
        {
            checkpoint = current ?? throw new ArgumentNullException(nameof(current));
            foreach (var handler in checkpointHandlers.ToArray())
            {
                try
                {
                    handler(current);
                }
                catch (Exception exception)
                {
                    events.Logger.Error(exception, "A fake checkpoint subscriber threw.");
                }
            }
        }

        /// <inheritdoc/>
        public Task<OperationResult<SceneSnapshot>> LoadAsync(
            SceneLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // Mirror UnitySceneService's single native-load slot. A cancelled consumer result does not release
            // that slot because the already-dispatched native load still owns it until completion is observed.
            // Check the slot before cancellation and completion policy exactly as production does: once A owns the
            // backend, even an already-cancelled B is a conflicting dispatch rather than an admitted operation.
            if (pending.Count != 0)
            {
                return Task.FromResult(OperationResult<SceneSnapshot>.Failure(
                    ModErrorCode.Conflict,
                    "Another fake scene load is already in progress."));
            }

            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<SceneSnapshot>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake scene load was cancelled."));
            }

            if (CompleteLoadsImmediately)
            {
                return Task.FromResult(OperationResult<SceneSnapshot>.Success(Apply(request)));
            }

            // Result cancellation and native completion are independent: cancellation suppresses the consumer's
            // result, while CompleteNextLoad still applies the already-dispatched native replacement.
            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.StoppingToken);
            var operation = new ControlledOperation<SceneSnapshot>(linked.Token);
            pending.Enqueue(new PendingLoad(request, operation, linked));
            return operation.Task;
        }

        /// <summary>
        /// Loads a scene synchronously and emits a notification. Like production, an additive load remains in the
        /// background until <see cref="Activate"/> is called.
        /// </summary>
        public void Load(string sceneName, SceneLoadMode mode = SceneLoadMode.Single)
        {
            Apply(new SceneLoadRequest(sceneName, mode));
        }

        /// <summary>Activates an already loaded scene and emits a detail-only authoritative transition.</summary>
        public bool Activate(string sceneName)
        {
            var found = false;
            for (var index = 0; index < loaded.Count; index++)
            {
                if (string.Equals(loaded[index].Name, sceneName, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found || !loadedModes.TryGetValue(sceneName, out var mode))
            {
                return false;
            }

            for (var index = 0; index < loaded.Count; index++)
            {
                var candidate = loaded[index];
                var active = string.Equals(candidate.Name, sceneName, StringComparison.Ordinal);
                loaded[index] = new SceneSnapshot(candidate.Name, candidate.IsLoaded, active);
            }

            events.RaiseSceneActivated(new SceneLoadEvent(sceneName, mode, isActive: true));
            return true;
        }

        /// <summary>Successfully completes the oldest manually pending scene load.</summary>
        public bool CompleteNextLoad()
        {
            if (!TryTakePending(out var item))
            {
                return false;
            }

            try
            {
                var snapshot = Apply(item.Request);
                item.Operation.Succeed(snapshot);
                return true;
            }
            finally
            {
                item.DisposeRegistrations();
            }
        }

        /// <summary>Fails the oldest manually pending scene load.</summary>
        public bool FailNextLoad(ModErrorCode errorCode, string message)
        {
            if (!TryTakePending(out var item))
            {
                return false;
            }

            try
            {
                item.Operation.Fail(errorCode, message);
                return true;
            }
            finally
            {
                item.DisposeRegistrations();
            }
        }

        private SceneSnapshot Apply(SceneLoadRequest request)
        {
            if (request.Mode == SceneLoadMode.Single)
            {
                loaded.Clear();
                loadedModes.Clear();
            }

            var isActive = request.Mode == SceneLoadMode.Single;
            var snapshot = new SceneSnapshot(request.SceneName, true, isActive);
            loaded.Add(snapshot);
            loadedModes[request.SceneName] = request.Mode;
            history.Add(request.SceneName);
            events.RaiseSceneLoaded(new SceneLoadEvent(request.SceneName, request.Mode, isActive));
            legacyNotification?.Invoke(request.SceneName);
            return snapshot;
        }

        private bool TryTakePending(out PendingLoad item)
        {
            if (pending.Count != 0)
            {
                item = pending.Dequeue();
                return true;
            }

            item = null!;
            return false;
        }

        private void CancelPendingLoads()
        {
            // Owner shutdown suppresses consumer results, but mirrors production by retaining already-dispatched
            // native replacements until the backend completion control consumes them.
            foreach (var item in pending)
            {
                item.DisposeRegistrations();
            }
        }

        private sealed class PendingLoad
        {
            public PendingLoad(
                SceneLoadRequest request,
                ControlledOperation<SceneSnapshot> operation,
                CancellationTokenSource linked)
            {
                Request = request;
                Operation = operation;
                Linked = linked;
            }

            public SceneLoadRequest Request { get; }
            public ControlledOperation<SceneSnapshot> Operation { get; }
            public CancellationTokenSource Linked { get; }
            public void DisposeRegistrations()
            {
                Linked.Dispose();
                Operation.Dispose();
            }
        }

        private sealed class CheckpointSubscription : IDisposable
        {
            private Action? unsubscribe;

            public CheckpointSubscription(Action unsubscribe)
            {
                this.unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                var action = unsubscribe;
                unsubscribe = null;
                action?.Invoke();
            }
        }
    }
}
