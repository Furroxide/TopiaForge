using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>Unity-backed manager-owned implementation of the safe V1 gameplay contracts.</summary>
    internal sealed class CoreGameplayServices : IRuntimeGameplayHost
    {
        private readonly UnityInputRegistry input = new UnityInputRegistry();
        private readonly UnityEntityRegistry entities = new UnityEntityRegistry();
        private readonly UnityGameTime time = new UnityGameTime();
        private readonly UnityScheduler scheduler = new UnityScheduler();
        private readonly UnityPlayerBackend player = new UnityPlayerBackend();
        private readonly UnitySceneBackend scenes;
        private readonly string runtimeOwnershipId;
        private readonly SceneCoordinator sceneCoordinator;
        private readonly UnityPhysicsBackend physics;
        private readonly GameObject loopObject;
        private bool disposed;

        public CoreGameplayServices(NativeTransitionHost nativeHost, string runtimeOwnershipId)
        {
            sceneCoordinator = nativeHost.Coordinator;
            scenes = nativeHost.Scenes;
            this.runtimeOwnershipId = runtimeOwnershipId;
            UnityMainThreadGuard.CaptureCurrentThread();
            physics = new UnityPhysicsBackend(entities);
            loopObject = new GameObject("TopiaForge.CoreGameplayLoop");
            UnityEngine.Object.DontDestroyOnLoad(loopObject);
            var loop = loopObject.AddComponent<CoreGameplayLoop>();
            loop.Owner = this;
        }

        public event Action<GameTimeSample>? FixedUpdate;
        public event Action<GameTimeSample>? LateUpdate;

        public GameplayContextServices Create(
            string ownerModId,
            string packagePath,
            string dataPath,
            IModLifetime lifetime,
            IModLogger logger,
            NativeTransitionAccessSlot? transitionAccess = null)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(CoreGameplayServices));
            }

            var sceneTransitions = new OwnerSceneTransitionService(ownerModId, sceneCoordinator,
                lifetime.StoppingToken, transitionAccess,
                runtimeOwnershipId + ":" + (transitionAccess?.OwnershipId ?? ownerModId));
            return new GameplayContextServices(
                new OwnerInputService(ownerModId, lifetime, input),
                new OwnerPlayerService(lifetime, player),
                new OwnerEntityService(lifetime, entities),
                physics,
                time,
                new OwnerScheduler(lifetime, scheduler, logger),
                new OwnerSceneService(lifetime, scenes, logger, sceneTransitions),
                new OwnerInteractionService(lifetime, entities, player, logger),
                new OwnerItemService(lifetime, entities, logger),
                new OwnerAssetService(packagePath, lifetime, entities),
                new OwnerAudioService(lifetime),
                new OwnerUiService(ownerModId, dataPath, lifetime, logger),
                new OwnerUnityInteropService(ownerModId, lifetime, entities),
                sceneTransitions);
        }

        public GameTimeSample BeginFrame(float deltaTime)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed)
            {
                return default;
            }

            var sample = time.Update(
                GameLoopPhase.Frame,
                deltaTime,
                UnityEngine.Time.unscaledDeltaTime,
                UnityEngine.Time.realtimeSinceStartupAsDouble,
                UnityEngine.Time.frameCount);
            input.Sample();
            scenes.SampleCheckpoint();
            scheduler.Tick(sample.ElapsedTime, sample.FrameIndex);
            return sample;
        }

        private void BeginFixedUpdate()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed)
            {
                return;
            }

            var sample = time.Update(
                GameLoopPhase.Fixed,
                UnityEngine.Time.fixedDeltaTime,
                UnityEngine.Time.fixedUnscaledDeltaTime,
                UnityEngine.Time.realtimeSinceStartupAsDouble,
                UnityEngine.Time.frameCount);
            FixedUpdate?.Invoke(sample);
        }

        private void BeginLateUpdate()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed)
            {
                return;
            }

            var sample = time.Update(
                GameLoopPhase.Late,
                UnityEngine.Time.deltaTime,
                UnityEngine.Time.unscaledDeltaTime,
                UnityEngine.Time.realtimeSinceStartupAsDouble,
                UnityEngine.Time.frameCount);
            LateUpdate?.Invoke(sample);
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed)
            {
                return;
            }

            disposed = true;
            input.Dispose();
            scheduler.Dispose();
            sceneCoordinator.RevokeOwnership(runtimeOwnershipId);
            entities.Dispose();
            player.Dispose();
            FixedUpdate = null;
            LateUpdate = null;
            if (loopObject != null)
            {
                UnityEngine.Object.Destroy(loopObject);
            }
        }

        private sealed class CoreGameplayLoop : MonoBehaviour
        {
            public CoreGameplayServices? Owner { get; set; }

            private void FixedUpdate()
            {
                Owner?.BeginFixedUpdate();
            }

            private void LateUpdate()
            {
                Owner?.BeginLateUpdate();
            }
        }
    }
}
