using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModManager
{
    internal interface IGameplayContextFactory
    {
        GameplayContextServices Create(
            string ownerModId,
            string packagePath,
            string dataPath,
            IModLifetime lifetime,
            IModLogger logger);
    }

    internal sealed class GameplayContextServices
    {
        public GameplayContextServices(
            IInputService input,
            IPlayerService player,
            IEntityService entities,
            IPhysicsService physics,
            IGameTime time,
            IModScheduler scheduler,
            ISceneService scenes,
            IInteractionService interactions,
            IItemService items,
            IAssetService assets,
            IAudioService audio,
            IUiService ui,
            IUnityInteropService? unityInterop)
        {
            Input = input;
            Player = player;
            Entities = entities;
            Physics = physics;
            Time = time;
            Scheduler = scheduler;
            Scenes = scenes;
            Interactions = interactions;
            Items = items;
            Assets = assets;
            Audio = audio;
            Ui = ui;
            UnityInterop = unityInterop;
        }

        public IInputService Input { get; }
        public IPlayerService Player { get; }
        public IEntityService Entities { get; }
        public IPhysicsService Physics { get; }
        public IGameTime Time { get; }
        public IModScheduler Scheduler { get; }
        public ISceneService Scenes { get; }
        public IInteractionService Interactions { get; }
        public IItemService Items { get; }
        public IAssetService Assets { get; }
        public IAudioService Audio { get; }
        public IUiService Ui { get; }
        public IUnityInteropService? UnityInterop { get; }

        public static GameplayContextServices Unavailable(IModLifetime lifetime)
        {
            var unavailable = new UnavailableGameplayService(lifetime);
            return new GameplayContextServices(
                unavailable,
                unavailable,
                unavailable,
                unavailable,
                unavailable,
                unavailable,
                unavailable,
                unavailable,
                unavailable,
                unavailable,
                unavailable,
                unavailable,
                null);
        }

        private sealed class UnavailableGameplayService :
            IInputService,
            IPlayerService,
            IEntityService,
            IPhysicsService,
            IGameTime,
            IModScheduler,
            ISceneService,
            IInteractionService,
            IItemService,
            IAssetService,
            IAudioService,
            IUiService
        {
            private readonly IModLifetime lifetime;

            public UnavailableGameplayService(IModLifetime lifetime)
            {
                this.lifetime = lifetime;
            }

            public bool IsUiFocused => false;
            public GameTimeSample Frame => default;
            public GameTimeSample Fixed => default;
            public GameTimeSample Late => default;
            public UiAccessibilityPreferences Accessibility => UiAccessibilityPreferences.Default;

            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences)
            {
                if (preferences == null) throw new ArgumentNullException(nameof(preferences));
                return OperationResult<UiAccessibilityPreferences>.Failure(
                    ModErrorCode.Unavailable,
                    "In-game UI is unavailable in this host.");
            }

            public OperationResult<IInputAction> RegisterAction(InputActionDefinition definition)
            {
                if (definition == null) throw new ArgumentNullException(nameof(definition));
                return OperationResult<IInputAction>.Failure(
                    ModErrorCode.Unavailable,
                    "The gameplay input service is unavailable in this host.");
            }

            public System.Collections.Generic.IReadOnlyList<InputConflict> GetConflicts()
            {
                return Array.Empty<InputConflict>();
            }

            public bool TryGetSnapshot(out PlayerSnapshot? snapshot)
            {
                snapshot = null;
                return false;
            }

            public bool TryGetHealth(out PlayerHealthSnapshot? health)
            {
                health = null;
                return false;
            }

            public OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request)
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                return OperationResult<PlayerHealthSnapshot>.Failure(
                    ModErrorCode.Unavailable,
                    "Player health is unavailable in this host.");
            }

            public OperationResult<PlayerHealthSnapshot> Heal(float amount, string source)
            {
                return OperationResult<PlayerHealthSnapshot>.Failure(
                    ModErrorCode.Unavailable,
                    "Player health is unavailable in this host.");
            }

            public OperationResult<IPlayerControlLease> AcquireControl(string reason)
            {
                return OperationResult<IPlayerControlLease>.Failure(ModErrorCode.Unavailable, "Player controls are unavailable in this host.");
            }

            public OperationResult<IEntityMotion> AcquireMotion(IEntity entity)
            {
                return OperationResult<IEntityMotion>.Failure(ModErrorCode.Unavailable, "World entities are unavailable in this host.");
            }

            public bool TryGetTransform(IEntity entity, out TransformState transform)
            {
                transform = TransformState.Identity;
                return false;
            }

            public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform)
            {
                return OperationResult<TransformState>.Failure(
                    ModErrorCode.Unavailable,
                    "World entities are unavailable in this host.");
            }

            public System.Collections.Generic.IReadOnlyList<IEntity> Query(EntityQuery query)
            {
                return Array.Empty<IEntity>();
            }

            public OperationResult<bool> Destroy(IEntity entity)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.Unavailable,
                    "World entities are unavailable in this host.");
            }

            public bool TryRaycast(Ray ray, float maximumDistance, out PhysicsHit? hit)
            {
                hit = null;
                return false;
            }

            public bool TrySphereCast(
                Ray ray,
                float radius,
                float maximumDistance,
                out PhysicsHit? hit)
            {
                hit = null;
                return false;
            }

            public System.Collections.Generic.IReadOnlyList<IEntity> Overlap(Bounds bounds, int maximumResults = 64)
            {
                return Array.Empty<IEntity>();
            }

            public bool TryGetActive(out SceneSnapshot? scene)
            {
                scene = null;
                return false;
            }

            public System.Collections.Generic.IReadOnlyList<SceneSnapshot> GetLoadedScenes()
            {
                return Array.Empty<SceneSnapshot>();
            }

            public bool TryGetCheckpoint(out CheckpointSnapshot? checkpoint)
            {
                checkpoint = null;
                return false;
            }

            public IDisposable SubscribeCheckpointChanged(Action<CheckpointSnapshot> handler)
            {
                if (handler == null) throw new ArgumentNullException(nameof(handler));
                return lifetime.Defer(() => { });
            }

            public Task<OperationResult<SceneSnapshot>> LoadAsync(
                SceneLoadRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(OperationResult<SceneSnapshot>.Failure(
                    ModErrorCode.Unavailable,
                    "Scene loading is unavailable in this host."));
            }

            public OperationResult<IInteractableRegistration> Register(
                IEntity entity,
                InteractableDefinition definition,
                Action<InteractionEvent> handler)
            {
                return OperationResult<IInteractableRegistration>.Failure(
                    ModErrorCode.Unavailable,
                    "Game interaction bindings are unavailable in this host.");
            }

            public bool TryGetFocused(out IInteractableRegistration? interaction)
            {
                interaction = null;
                return false;
            }

            public bool TryGetHeld(out HeldItemSnapshot? item)
            {
                item = null;
                return false;
            }

            public Task<OperationResult<HeldItemSnapshot>> GiveAsync(
                ItemGrantRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(OperationResult<HeldItemSnapshot>.Failure(
                    ModErrorCode.Unavailable,
                    "Game item bindings are unavailable in this host."));
            }

            public Task<OperationResult<IEntity>> DropHeldAsync(
                Vec3 velocity,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(OperationResult<IEntity>.Failure(
                    ModErrorCode.Unavailable,
                    "Game item bindings are unavailable in this host."));
            }

            public Task<OperationResult<IAssetBundle>> LoadBundleAsync(
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(OperationResult<IAssetBundle>.Failure(
                    ModErrorCode.Unavailable,
                    "Asset loading is unavailable in this host."));
            }

            public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(
                IAssetBundle bundle,
                string assetName,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(OperationResult<IPrefabAsset>.Failure(
                    ModErrorCode.Unavailable,
                    "Asset loading is unavailable in this host."));
            }

            public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
            {
                return OperationResult<ISpawnedEntity>.Failure(
                    ModErrorCode.Unavailable,
                    "Asset spawning is unavailable in this host.");
            }

            public OperationResult<IAudioPlayback> Play(AudioPlayRequest request)
            {
                return OperationResult<IAudioPlayback>.Failure(
                    ModErrorCode.Unavailable,
                    "Audio cue bindings are unavailable in this host.");
            }

            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request)
            {
                return OperationResult<IUiSurface>.Failure(
                    ModErrorCode.Unavailable,
                    "In-game UI is unavailable in this host.");
            }

            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed)
            {
                return OperationResult<IUiModal>.Failure(
                    ModErrorCode.Unavailable,
                    "In-game UI is unavailable in this host.");
            }

            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.Unavailable,
                    "In-game UI is unavailable in this host.");
            }

            public OperationResult<IDisposable> NextFrame(Action action)
            {
                return After(TimeSpan.Zero, action);
            }

            public OperationResult<IDisposable> After(TimeSpan delay, Action action)
            {
                if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
                if (action == null) throw new ArgumentNullException(nameof(action));
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Unavailable,
                    "The gameplay scheduler is unavailable in this host.");
            }

            public OperationResult<IDisposable> Every(TimeSpan interval, Action action)
            {
                if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
                if (action == null) throw new ArgumentNullException(nameof(action));
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Unavailable,
                    "The gameplay scheduler is unavailable in this host.");
            }

            public Task<OperationResult<bool>> DelayAsync(
                TimeSpan delay,
                CancellationToken cancellationToken = default)
            {
                if (delay < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(delay));
                }

                if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
                {
                    return Task.FromResult(OperationResult<bool>.Failure(
                        ModErrorCode.Cancelled,
                        "The scheduled delay was cancelled."));
                }

                return Task.FromResult(OperationResult<bool>.Failure(
                    ModErrorCode.Unavailable,
                    "The gameplay scheduler is unavailable in this host."));
            }
        }
    }
}
