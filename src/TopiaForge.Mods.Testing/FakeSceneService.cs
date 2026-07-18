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

        /// <summary>Gets every successfully activated scene name in order.</summary>
        public IReadOnlyList<string> History => history.AsReadOnly();

        /// <summary>Gets the number of manually pending loads.</summary>
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

            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.StoppingToken);
            var operation = new ControlledOperation<SceneSnapshot>(linked.Token);
            pending.Enqueue(new PendingLoad(request, operation, linked));
            return operation.Task;
        }

        /// <summary>Changes the active scene synchronously and emits a load notification.</summary>
        public void Load(string sceneName, SceneLoadMode mode = SceneLoadMode.Single)
        {
            Apply(new SceneLoadRequest(sceneName, mode));
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
                return item.Operation.Succeed(Apply(item.Request));
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
                return item.Operation.Fail(errorCode, message);
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
            }
            else
            {
                for (var index = 0; index < loaded.Count; index++)
                {
                    var existing = loaded[index];
                    loaded[index] = new SceneSnapshot(existing.Name, existing.IsLoaded, false);
                }
            }

            var snapshot = new SceneSnapshot(request.SceneName, true, true);
            loaded.Add(snapshot);
            history.Add(request.SceneName);
            events.RaiseSceneLoaded(request.SceneName);
            legacyNotification?.Invoke(request.SceneName);
            return snapshot;
        }

        private bool TryTakePending(out PendingLoad item)
        {
            while (pending.Count != 0)
            {
                item = pending.Dequeue();
                if (!item.Operation.IsCompleted)
                {
                    return true;
                }

                item.DisposeRegistrations();
            }

            item = null!;
            return false;
        }

        private void CancelPendingLoads()
        {
            while (pending.Count != 0)
            {
                pending.Dequeue().DisposeRegistrations();
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
