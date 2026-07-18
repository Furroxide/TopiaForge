using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerSceneService : ISceneService
    {
        private readonly IModLifetime lifetime;
        private readonly UnitySceneBackend backend;
        private readonly IModLogger logger;

        public OwnerSceneService(IModLifetime lifetime, UnitySceneBackend backend, IModLogger logger)
        {
            this.lifetime = lifetime;
            this.backend = backend;
            this.logger = logger;
        }

        public bool TryGetActive(out SceneSnapshot? scene)
        {
            UnityMainThreadGuard.AssertCurrent();
            return backend.TryGetActive(out scene);
        }

        public IReadOnlyList<SceneSnapshot> GetLoadedScenes()
        {
            UnityMainThreadGuard.AssertCurrent();
            return backend.GetLoadedScenes();
        }

        public bool TryGetCheckpoint(out CheckpointSnapshot? checkpoint)
        {
            UnityMainThreadGuard.AssertCurrent();
            return backend.TryGetCheckpoint(out checkpoint);
        }

        public IDisposable SubscribeCheckpointChanged(Action<CheckpointSnapshot> handler)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return lifetime.Track(backend.SubscribeCheckpointChanged(checkpoint =>
            {
                try
                {
                    handler(checkpoint);
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "A checkpoint subscriber failed.");
                }
            }));
        }

        public Task<OperationResult<SceneSnapshot>> LoadAsync(
            SceneLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var result = backend.BeginLoad(request, lifetime.StoppingToken, cancellationToken);
            if (!result.TryGetValue(out var operation))
            {
                return Task.FromResult(OperationResult<SceneSnapshot>.Failure(
                    result.ErrorCode,
                    result.ErrorMessage));
            }

            // Unity cannot cancel a dispatched AsyncOperation. The backend owns it until native completion while
            // the result state independently honors caller and owner cancellation.
            return operation.Task;
        }
    }

    internal sealed partial class UnitySceneBackend : IDisposable
    {
        private readonly object sync = new object();
        private readonly List<Action<CheckpointSnapshot>> checkpointSubscribers =
            new List<Action<CheckpointSnapshot>>();
        private SceneLoadState? activeLoad;
        private CheckpointSnapshot? currentCheckpoint;
        private Type? checkpointManagerType;
        private bool checkpointTypeResolved;
        private bool disposed;

        public bool TryGetActive(out SceneSnapshot? snapshot)
        {
            UnityMainThreadGuard.AssertCurrent();
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrWhiteSpace(scene.name))
            {
                snapshot = null;
                return false;
            }

            snapshot = ToSnapshot(scene, true);
            return true;
        }

        public IReadOnlyList<SceneSnapshot> GetLoadedScenes()
        {
            UnityMainThreadGuard.AssertCurrent();
            var active = SceneManager.GetActiveScene();
            var scenes = new List<SceneSnapshot>(SceneManager.sceneCount);
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && !string.IsNullOrWhiteSpace(scene.name))
                {
                    scenes.Add(ToSnapshot(scene, scene == active));
                }
            }

            return scenes.AsReadOnly();
        }

        public bool TryGetCheckpoint(out CheckpointSnapshot? checkpoint)
        {
            UnityMainThreadGuard.AssertCurrent();
            lock (sync)
            {
                checkpoint = currentCheckpoint;
                return checkpoint != null;
            }
        }

        public IDisposable SubscribeCheckpointChanged(Action<CheckpointSnapshot> handler)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (sync)
            {
                if (disposed)
                {
                    return new CheckpointSubscription(null);
                }

                checkpointSubscribers.Add(handler);
            }

            return new CheckpointSubscription(() =>
            {
                lock (sync)
                {
                    checkpointSubscribers.Remove(handler);
                }
            });
        }

        public void SampleCheckpoint()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed)
            {
                return;
            }

            var sampled = ResolveCheckpoint();
            Action<CheckpointSnapshot>[] subscribers;
            lock (sync)
            {
                if (sampled == null
                    || (currentCheckpoint != null
                        && string.Equals(currentCheckpoint.Id, sampled.Id, StringComparison.Ordinal)
                        && string.Equals(currentCheckpoint.SceneName, sampled.SceneName, StringComparison.Ordinal)
                        && currentCheckpoint.Position == sampled.Position))
                {
                    currentCheckpoint = sampled;
                    return;
                }

                currentCheckpoint = sampled;
                subscribers = checkpointSubscribers.ToArray();
            }

            foreach (var subscriber in subscribers)
            {
                subscriber(sampled);
            }
        }

        public OperationResult<SceneLoadState> BeginLoad(
            SceneLoadRequest request,
            CancellationToken stoppingToken,
            CancellationToken callerToken)
        {
            UnityMainThreadGuard.AssertCurrent();
            lock (sync)
            {
                if (disposed)
                {
                    return OperationResult<SceneLoadState>.Failure(
                        ModErrorCode.InvalidState,
                        "The scene service is shutting down.");
                }

                if (activeLoad != null)
                {
                    return OperationResult<SceneLoadState>.Failure(
                        ModErrorCode.Conflict,
                        "Another TopiaForge scene load is already in progress.");
                }

                if (stoppingToken.IsCancellationRequested || callerToken.IsCancellationRequested)
                {
                    return OperationResult<SceneLoadState>.Failure(
                        ModErrorCode.Cancelled,
                        "The scene load was cancelled before it started.");
                }

                AsyncOperation? operation;
                try
                {
                    var mode = request.Mode == SceneLoadMode.Additive
                        ? LoadSceneMode.Additive
                        : LoadSceneMode.Single;
                    operation = SceneManager.LoadSceneAsync(request.SceneName, mode);
                }
                catch (Exception exception)
                {
                    return OperationResult<SceneLoadState>.Failure(
                        ModErrorCode.External,
                        "The game rejected scene '" + request.SceneName + "': " + exception.Message);
                }

                if (operation == null)
                {
                    return OperationResult<SceneLoadState>.Failure(
                        ModErrorCode.NotFound,
                        "Scene '" + request.SceneName + "' is not available in the game build.");
                }

                var state = new SceneLoadState(
                    this,
                    request.SceneName,
                    operation);

                // Publish before arming. Unity invokes AsyncOperation.completed synchronously when an
                // operation has already finished, so the callback must be able to observe this state.
                activeLoad = state;
                try
                {
                    state.Arm(stoppingToken, callerToken);
                }
                catch (Exception exception)
                {
                    if (ReferenceEquals(activeLoad, state))
                    {
                        activeLoad = null;
                    }

                    state.AbortArming();
                    return OperationResult<SceneLoadState>.Failure(
                        ModErrorCode.External,
                        "The native scene load started, but TopiaForge could not subscribe to its completion: "
                            + exception.Message);
                }

                return OperationResult<SceneLoadState>.Success(state);
            }
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            SceneLoadState? operation;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                operation = activeLoad;
                activeLoad = null;
                checkpointSubscribers.Clear();
                currentCheckpoint = null;
            }

            operation?.DisposeFromBackend();
        }

        private void Complete(SceneLoadState state, string sceneName)
        {
            UnityMainThreadGuard.AssertCurrent();
            lock (sync)
            {
                if (ReferenceEquals(activeLoad, state))
                {
                    activeLoad = null;
                }
            }

            var loaded = SceneManager.GetSceneByName(sceneName);
            if (!loaded.IsValid())
            {
                state.Complete(OperationResult<SceneSnapshot>.Failure(
                    ModErrorCode.External,
                    "The native scene operation completed but the loaded scene could not be resolved."));
                return;
            }

            state.Complete(OperationResult<SceneSnapshot>.Success(
                ToSnapshot(loaded, loaded == SceneManager.GetActiveScene())));
        }

        private static SceneSnapshot ToSnapshot(Scene scene, bool active)
        {
            return new SceneSnapshot(scene.name, scene.isLoaded, active);
        }

        internal sealed class SceneLoadState
        {
            private readonly UnitySceneBackend backend;
            private readonly string sceneName;
            private readonly AsyncOperation operation;
            private readonly TaskCompletionSource<OperationResult<SceneSnapshot>> completion =
                new TaskCompletionSource<OperationResult<SceneSnapshot>>();
            private CancellationTokenRegistration stoppingRegistration;
            private CancellationTokenRegistration callerRegistration;
            private int armStarted;
            private int resultFinished;
            private int nativeFinished;

            public SceneLoadState(
                UnitySceneBackend backend,
                string sceneName,
                AsyncOperation operation)
            {
                this.backend = backend;
                this.sceneName = sceneName;
                this.operation = operation;
            }

            public Task<OperationResult<SceneSnapshot>> Task => completion.Task;

            public void Arm(CancellationToken stoppingToken, CancellationToken callerToken)
            {
                if (Interlocked.Exchange(ref armStarted, 1) != 0)
                {
                    throw new InvalidOperationException("A scene-load state can only be armed once.");
                }

                var completionSubscriptionEstablished = false;
                try
                {
                    operation.completed += OnCompleted;
                    completionSubscriptionEstablished = true;
                    stoppingRegistration = stoppingToken.Register(Cancel);
                    callerRegistration = callerToken.CanBeCanceled ? callerToken.Register(Cancel) : default;
                }
                catch (Exception exception)
                {
                    if (!completionSubscriptionEstablished)
                    {
                        throw;
                    }

                    // The native operation is already dispatched and cannot be cancelled. Keep its completed
                    // handler/backend ownership intact, but settle the public result rather than leaking a task
                    // when cancellation registration cannot be established.
                    Complete(OperationResult<SceneSnapshot>.Failure(
                        ModErrorCode.External,
                        "The native scene load started, but its result could not be monitored safely: "
                            + exception.Message));
                }

                // Registration invokes synchronously when cancellation wins the narrow race after BeginLoad's
                // preflight check. Dispose registrations assigned after that callback before leaving Arm.
                if (Volatile.Read(ref resultFinished) != 0)
                {
                    DisposeResultRegistrations();
                }
            }

            public void AbortArming()
            {
                Complete(OperationResult<SceneSnapshot>.Failure(
                    ModErrorCode.External,
                    "The native scene load could not be monitored."));
                StopNativeTracking();
            }

            public void DisposeFromBackend()
            {
                Cancel();
                StopNativeTracking();
            }

            public void Complete(OperationResult<SceneSnapshot> result)
            {
                if (Interlocked.Exchange(ref resultFinished, 1) != 0)
                {
                    return;
                }

                try
                {
                    DisposeResultRegistrations();
                }
                finally
                {
                    completion.TrySetResult(result);
                }
            }

            private void Cancel()
            {
                if (Interlocked.Exchange(ref resultFinished, 1) != 0)
                {
                    return;
                }

                try
                {
                    DisposeResultRegistrations();
                }
                finally
                {
                    completion.TrySetResult(OperationResult<SceneSnapshot>.Failure(
                        ModErrorCode.Cancelled,
                        "The scene load was cancelled."));
                }
            }

            private void OnCompleted(AsyncOperation _)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (Interlocked.Exchange(ref nativeFinished, 1) != 0)
                {
                    return;
                }

                try
                {
                    operation.completed -= OnCompleted;
                }
                finally
                {
                    backend.Complete(this, sceneName);
                }
            }

            private void StopNativeTracking()
            {
                if (Interlocked.Exchange(ref nativeFinished, 1) != 0)
                {
                    return;
                }

                try
                {
                    operation.completed -= OnCompleted;
                }
                catch
                {
                    // Backend disposal and failed arming are terminal. Cleanup must not mask their result.
                }
            }

            private void DisposeResultRegistrations()
            {
                try
                {
                    stoppingRegistration.Dispose();
                }
                catch
                {
                    // Result completion must remain terminal even if the runtime rejects registration cleanup.
                }

                try
                {
                    callerRegistration.Dispose();
                }
                catch
                {
                    // The result-finished guard makes any surviving callback harmless.
                }
            }
        }
    }
}
