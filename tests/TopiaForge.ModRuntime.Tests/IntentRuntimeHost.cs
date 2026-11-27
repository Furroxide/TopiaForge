using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    /// <summary>
    /// A gameplay host for intent tracking over the real runtime (WS3.4). A test drives it as the player would: it holds
    /// input, confirms modals through the UI kit's hook, and interacts; it runs timers and posted work at the start of each
    /// frame, as the game's player loop does; and it records each spawn with the intent its mod observed as it spawned.
    /// </summary>
    internal sealed class IntentRuntimeHost : IRuntimeGameplayHost, IMultiplayerPlatform
    {
        private readonly UnityScheduler scheduler = new UnityScheduler();
        private double elapsed;
        private long frame;

        public event Action<GameTimeSample>? FixedUpdate;

        public event Action<GameTimeSample>? LateUpdate;

        public bool RunInBackground { get; set; } = true;

        /// <summary>Gets or sets the runtime's intent tracking, which the recorded spawns read.</summary>
        internal MultiplayerIntents? Intents { get; set; }

        /// <summary>Gets or sets the main thread's posted work, run after the timers at the start of each frame.</summary>
        internal MainThreadPump? Pump { get; set; }

        /// <summary>Gets or sets whether frames run timers; the scheduler allocates as it ticks, so a measured frame runs none.</summary>
        internal bool TickTimers { get; set; } = true;

        internal List<FakeAction> Actions { get; } = new List<FakeAction>();

        internal List<(string Owner, Action<bool> Completed)> Modals { get; } = new List<(string, Action<bool>)>();

        internal List<(string Owner, Action<InteractionEvent> Handler)> Interactions { get; } = new List<(string, Action<InteractionEvent>)>();

        internal List<(string Owner, string Name, string Intent)> Spawns { get; } = new List<(string, string, string)>();

        public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath, IModLifetime lifetime, IModLogger logger,
            ModNetProfile profile, NativeTransitionAccessSlot? transitionAccess = null)
        {
            var other = GameplayContextServices.Unavailable(lifetime);
            var services = new Services(this, ownerModId, lifetime);
            return new GameplayContextServices(services, other.LocalPlayer, other.Entities, other.Physics, other.Time,
                new OwnerScheduler(lifetime, scheduler, logger), other.Scenes, services, other.Items, services, other.Audio, services,
                other.UnityInterop, other.SceneTransitions);
        }

        public GameTimeSample BeginFrame(float deltaTime)
        {
            elapsed += 0.25;
            frame++;
            for (var index = 0; index < Actions.Count; index++) Actions[index].Sample();
            if (TickTimers) scheduler.Tick(elapsed, frame);
            Pump?.RunPending();
            return new GameTimeSample(GameLoopPhase.Frame, deltaTime, deltaTime, elapsed, frame);
        }

        internal void RaiseFixedUpdate() => FixedUpdate?.Invoke(new GameTimeSample(GameLoopPhase.Fixed, 0.02f, 0.02f, elapsed, frame));

        internal void RaiseLateUpdate() => LateUpdate?.Invoke(new GameTimeSample(GameLoopPhase.Late, 0f, 0f, elapsed, frame));

        internal FakeAction Action(string owner, string name) =>
            Actions.Find(action => action.Owner == owner && action.Name == name) ?? throw new InvalidOperationException("No action " + owner + ":" + name + ".");

        /// <summary>The player interacts with what <paramref name="owner"/> registered, as the game's interaction system calls it.</summary>
        internal void Interact(string owner)
        {
            foreach (var (registered, handler) in Interactions)
            {
                if (registered == owner) handler(new InteractionEvent(new Anchor(), new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0, 0, 1)))));
            }
        }

        public void Dispose()
        {
            scheduler.Dispose();
        }

        /// <summary>An input action the test holds and lets go of; each frame's sample turns that into edges, as the game's does.</summary>
        internal sealed class FakeAction : IInputAction
        {
            private bool down;

            internal FakeAction(string owner, string name)
            {
                Owner = owner;
                Name = name;
            }

            internal string Owner { get; }

            public string Name { get; }

            public IReadOnlyList<InputBinding> Bindings { get; } = new[] { InputBinding.Key("Space") };

            public float Value => IsHeld ? 1f : 0f;

            public bool IsHeld { get; private set; }

            public bool WasPressed { get; private set; }

            public bool WasReleased { get; private set; }

            internal void Hold(bool value) => down = value;

            internal void Sample()
            {
                var previous = IsHeld;
                IsHeld = down;
                WasPressed = IsHeld && !previous;
                WasReleased = !IsHeld && previous;
            }

            public OperationResult<bool> Rebind(IEnumerable<InputBinding> bindings) => OperationResult<bool>.Success(true);

            public OperationResult<bool> ResetBindings() => OperationResult<bool>.Success(true);

            public void Dispose() => down = false;
        }

        private sealed class Services : IInputService, IInteractionService, IAssetService, IUiService
        {
            private readonly IntentRuntimeHost host;
            private readonly string owner;
            private readonly IModLifetime lifetime;

            internal Services(IntentRuntimeHost host, string owner, IModLifetime lifetime)
            {
                this.host = host;
                this.owner = owner;
                this.lifetime = lifetime;
            }

            public bool IsUiFocused => false;

            public UiAccessibilityPreferences Accessibility => UiAccessibilityPreferences.Default;

            public IReadOnlyList<InputConflict> GetConflicts() => Array.Empty<InputConflict>();

            public OperationResult<IInputAction> RegisterAction(InputActionDefinition definition)
            {
                var action = new FakeAction(owner, definition.Name);
                host.Actions.Add(action);
                return OperationResult<IInputAction>.Success(action);
            }

            public OperationResult<IInteractableRegistration> Register(IEntity entity, InteractableDefinition definition, Action<InteractionEvent> handler)
            {
                host.Interactions.Add((owner, handler ?? throw new ArgumentNullException(nameof(handler))));
                return OperationResult<IInteractableRegistration>.Failure(ModErrorCode.Unavailable, "The test host keeps no registration.");
            }

            public bool TryGetFocused(out IInteractableRegistration? interaction)
            {
                interaction = null;
                return false;
            }

            public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken cancellationToken = default) =>
                Task.FromResult(OperationResult<IAssetBundle>.Failure(ModErrorCode.Unavailable, "no bundles"));

            public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string assetName, CancellationToken cancellationToken = default) =>
                Task.FromResult(OperationResult<IPrefabAsset>.Failure(ModErrorCode.Unavailable, "no prefabs"));

            public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
            {
                // The question authority routing asks of every spawn: does it carry intent, and whose?
                var seen = host.Intents?.VisibleTo(owner);
                host.Spawns.Add((owner, request.Prefab.Name, seen == null ? "none" : seen.Source + (seen.IsLocalPlayer ? "/local" : "/" + seen.ParticipantId)));
                var spawned = new Anchor();
                lifetime.Track(spawned);
                return OperationResult<ISpawnedEntity>.Success(spawned);
            }

            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) =>
                OperationResult<UiAccessibilityPreferences>.Success(preferences);

            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) =>
                OperationResult<IUiSurface>.Failure(ModErrorCode.Unavailable, "no surfaces");

            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed)
            {
                host.Modals.Add((owner, completed ?? throw new ArgumentNullException(nameof(completed))));
                return OperationResult<IUiModal>.Failure(ModErrorCode.Unavailable, "The test host keeps no modal.");
            }

            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => OperationResult<bool>.Success(true);
        }

        private sealed class Anchor : ISpawnedEntity
        {
            private static int next;

            internal Anchor()
            {
                Id = "intent-anchor-" + Interlocked.Increment(ref next);
            }

            public string Id { get; }

            public string Name => "anchor";

            public bool IsAlive { get; private set; } = true;

            public Vec3 Position => Vec3.Zero;

            public TransformState InitialTransform => TransformState.Identity;

            public void Dispose() => IsAlive = false;
        }
    }
}
