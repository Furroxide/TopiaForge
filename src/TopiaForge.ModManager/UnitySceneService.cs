using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerSceneService : ISceneService
    {
        private readonly IModLifetime lifetime;
        private readonly UnitySceneBackend backend;
        private readonly IModLogger logger;
        private readonly IInternalSceneTransitionService sceneTransitions;

        public OwnerSceneService(
            IModLifetime lifetime,
            UnitySceneBackend backend,
            IModLogger logger,
            IInternalSceneTransitionService sceneTransitions)
        {
            this.lifetime = lifetime;
            this.backend = backend;
            this.logger = logger;
            this.sceneTransitions = sceneTransitions;
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
            return OwnerCheckpointSubscription.Subscribe(lifetime, handler, backend.SubscribeCheckpointChanged,
                exception => logger.Error(exception, "A checkpoint subscriber failed."));
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

            var result = sceneTransitions.TryDispatch(
                new NativeSceneRequest(request.SceneName, false, "core scene load", observeSceneArrival: false),
                new DelegateNativeSceneDispatch(completion => backend.DispatchLoad(request, completion)),
                cancellationToken);
            return result.TryGetValue(out var operation)
                ? operation.Completion
                : Task.FromResult(OperationResult<SceneSnapshot>.Failure(result.ErrorCode, result.ErrorMessage));
        }
    }

    internal sealed partial class UnitySceneBackend : IDisposable
    {
        private readonly object sync = new object();
        private readonly List<Action<CheckpointSnapshot>> checkpointSubscribers =
            new List<Action<CheckpointSnapshot>>();
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

        internal NativeSceneDispatchStatus DispatchLoad(SceneLoadRequest request, IInternalNativeSceneCompletion completion)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed || !Application.CanStreamedLevelBeLoaded(request.SceneName))
            {
                completion.FailCaller(disposed ? ModErrorCode.InvalidState : ModErrorCode.NotFound,
                    "The requested native scene is unavailable.");
                return NativeSceneDispatchStatus.NotDispatched;
            }
            AsyncOperation? operation = SceneManager.LoadSceneAsync(request.SceneName,
                request.Mode == SceneLoadMode.Additive ? LoadSceneMode.Additive : LoadSceneMode.Single);
            if (operation == null)
            {
                completion.FailCaller(ModErrorCode.External, "The native scene loader returned no operation.");
                return NativeSceneDispatchStatus.Indeterminate;
            }
            var pending = new NativeLoad(this, operation, request.SceneName, completion);
            nativeLoads.Add(pending); // Retain before subscribing; polling survives a subscription failure.
            try { pending.Arm(); }
            catch (Exception error) { completion.FailCaller(ModErrorCode.External, "Native completion subscription failed: " + error.Message); }
            return NativeSceneDispatchStatus.Dispatched;
        }

        private readonly List<NativeLoad> nativeLoads = new List<NativeLoad>();
        internal void PollNativeOperations()
        {
            UnityMainThreadGuard.AssertCurrent();
            for (var index = nativeLoads.Count - 1; index >= 0; index--)
                nativeLoads[index].Poll();
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            // A runtime facade never owns this backend. The process host may close only after drain.
            if (nativeLoads.Count != 0)
                throw new InvalidOperationException("Cannot dispose the native backend while engine work is outstanding.");
            disposed = true;
            lock (sync) { checkpointSubscribers.Clear(); currentCheckpoint = null; }
        }

        private static SceneSnapshot ToSnapshot(Scene scene, bool active) =>
            new SceneSnapshot(scene.name, scene.isLoaded, active);

        private sealed class NativeLoad
        {
            private readonly UnitySceneBackend backend;
            private readonly AsyncOperation operation;
            private readonly string sceneName;
            private readonly IInternalNativeSceneCompletion completion;
            private bool finished;
            public NativeLoad(UnitySceneBackend backend, AsyncOperation operation, string sceneName,
                IInternalNativeSceneCompletion completion)
            {
                this.backend = backend; this.operation = operation;
                this.sceneName = sceneName; this.completion = completion;
            }
            public void Arm() { operation.completed += OnCompleted; Poll(); }
            public void Poll() { if (!finished && operation.isDone) Finish(); }
            private void OnCompleted(AsyncOperation _) { Finish(); }
            private void Finish()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (finished) return;
                finished = true;
                try { operation.completed -= OnCompleted; } catch { }
                backend.nativeLoads.Remove(this);
                var scene = SceneManager.GetSceneByName(sceneName);
                completion.NativeCompleted(scene.IsValid()
                    ? OperationResult<SceneSnapshot>.Success(ToSnapshot(scene, scene == SceneManager.GetActiveScene()))
                    : OperationResult<SceneSnapshot>.Failure(ModErrorCode.External,
                        "Native work drained, but its resulting scene could not be resolved."));
            }
        }
    }
}
