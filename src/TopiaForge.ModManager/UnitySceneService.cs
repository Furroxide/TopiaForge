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

            lifetime.Track(operation);
            return operation.Task;
        }
    }

    internal sealed class UnitySceneBackend : IDisposable
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

                activeLoad = new SceneLoadState(this, request.SceneName, operation, stoppingToken, callerToken);
                return OperationResult<SceneLoadState>.Success(activeLoad);
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

        private CheckpointSnapshot? ResolveCheckpoint()
        {
            try
            {
                var type = ResolveCheckpointManagerType();
                if (type == null)
                {
                    return null;
                }

                var manager = ReadMember(null, type, "Instance", "instance", "Current")
                    ?? Resources.FindObjectsOfTypeAll(type).FirstOrDefault();
                var checkpoint = ReadMember(manager, type, "CurrentCheckpoint", "currentCheckpoint", "Checkpoint")
                    ?? ReadMember(null, type, "CurrentCheckpoint", "currentCheckpoint", "Checkpoint");
                if (checkpoint == null)
                {
                    return null;
                }

                var checkpointType = checkpoint.GetType();
                var id = ReadString(checkpoint, checkpointType, "CheckpointId", "checkpointId", "Id", "id");
                if (string.IsNullOrWhiteSpace(id) && checkpoint is UnityEngine.Object native)
                {
                    id = native.name;
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    return null;
                }

                var sceneName = ReadString(checkpoint, checkpointType, "SceneName", "sceneName");
                if (string.IsNullOrWhiteSpace(sceneName))
                {
                    sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
                }

                var position = checkpoint is Component component
                    ? component.transform.position
                    : ReadVector(checkpoint, checkpointType, "Position", "position");
                return new CheckpointSnapshot(id, sceneName, UnityPhysicsBackend.FromUnity(position));
            }
            catch
            {
                return null;
            }
        }

        private Type? ResolveCheckpointManagerType()
        {
            if (checkpointTypeResolved)
            {
                return checkpointManagerType;
            }

            checkpointTypeResolved = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    checkpointManagerType = assembly.GetTypes().FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, "CheckpointManager", StringComparison.Ordinal));
                    if (checkpointManagerType != null)
                    {
                        break;
                    }
                }
                catch (ReflectionTypeLoadException exception)
                {
                    checkpointManagerType = exception.Types.FirstOrDefault(candidate => candidate != null
                        && string.Equals(candidate.Name, "CheckpointManager", StringComparison.Ordinal));
                    if (checkpointManagerType != null)
                    {
                        break;
                    }
                }
            }

            return checkpointManagerType;
        }

        private static object? ReadMember(object? instance, Type type, params string[] names)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic
                | (instance == null ? BindingFlags.Static : BindingFlags.Instance);
            foreach (var name in names)
            {
                var value = type.GetProperty(name, flags)?.GetValue(instance, null)
                    ?? type.GetField(name, flags)?.GetValue(instance);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static string ReadString(object instance, Type type, params string[] names) =>
            ReadMember(instance, type, names) as string ?? string.Empty;

        private static Vector3 ReadVector(object instance, Type type, params string[] names) =>
            ReadMember(instance, type, names) is Vector3 value ? value : Vector3.zero;

        private sealed class CheckpointSubscription : IDisposable
        {
            private Action? unsubscribe;

            public CheckpointSubscription(Action? unsubscribe)
            {
                this.unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref unsubscribe, null)?.Invoke();
            }
        }

        internal sealed class SceneLoadState : IDisposable
        {
            private readonly UnitySceneBackend backend;
            private readonly string sceneName;
            private readonly AsyncOperation operation;
            private readonly TaskCompletionSource<OperationResult<SceneSnapshot>> completion =
                new TaskCompletionSource<OperationResult<SceneSnapshot>>();
            private readonly CancellationTokenRegistration stoppingRegistration;
            private readonly CancellationTokenRegistration callerRegistration;
            private int resultFinished;
            private int nativeFinished;

            public SceneLoadState(
                UnitySceneBackend backend,
                string sceneName,
                AsyncOperation operation,
                CancellationToken stoppingToken,
                CancellationToken callerToken)
            {
                this.backend = backend;
                this.sceneName = sceneName;
                this.operation = operation;
                stoppingRegistration = stoppingToken.Register(Cancel);
                callerRegistration = callerToken.CanBeCanceled ? callerToken.Register(Cancel) : default;
                operation.completed += OnCompleted;
            }

            public Task<OperationResult<SceneSnapshot>> Task => completion.Task;

            public void Dispose()
            {
                Cancel();
            }

            public void DisposeFromBackend()
            {
                Cancel();
                if (Interlocked.Exchange(ref nativeFinished, 1) == 0)
                {
                    operation.completed -= OnCompleted;
                }
            }

            public void Complete(OperationResult<SceneSnapshot> result)
            {
                if (Interlocked.Exchange(ref resultFinished, 1) != 0)
                {
                    return;
                }

                stoppingRegistration.Dispose();
                callerRegistration.Dispose();
                completion.TrySetResult(result);
            }

            private void Cancel()
            {
                if (Interlocked.Exchange(ref resultFinished, 1) != 0)
                {
                    return;
                }

                stoppingRegistration.Dispose();
                callerRegistration.Dispose();
                completion.TrySetResult(OperationResult<SceneSnapshot>.Failure(
                    ModErrorCode.Cancelled,
                    "The scene load was cancelled."));
            }

            private void OnCompleted(AsyncOperation _)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (Interlocked.Exchange(ref nativeFinished, 1) != 0)
                {
                    return;
                }

                operation.completed -= OnCompleted;
                backend.Complete(this, sceneName);
            }
        }
    }
}
