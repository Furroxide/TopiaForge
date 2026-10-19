using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Intent tracking (WS3.4): its Core rules and scope, the runtime's entry points, the decorators that feed it and the kit's half.</summary>
    internal static class MultiplayerIntentTests
    {
        internal static void Run(string root)
        {
            Directory.CreateDirectory(root);
            MultiplayerIntentRulesTests.Run();
            MultiplayerIntentScopeTests.Run();
            MultiplayerIntentRuntimeTests.Run();
            MultiplayerIntentCoarsenessTests.Run();
            MultiplayerIntentServicesTests.Run(root);
            UiKitIntentConventionTests.Run();
            Console.WriteLine("Multiplayer intent tracking tests passed.");
        }

        /// <summary>A composition root online in <paramref name="role"/>, as of its last pump.</summary>
        internal static (MultiplayerRuntime Runtime, FakeMultiplayerControl Control) Online(InternalSessionRole role)
        {
            var (runtime, control, _) = Runtime(() => SessionPhase.Idle);
            runtime.Connect();
            control.Role = role;
            Frame(runtime);
            return (runtime, control);
        }

        /// <summary>What a mod's code observes now, by its mod id: the question authority routing asks.</summary>
        internal static string Seen(MultiplayerIntents intents, string modId) => Describe(intents.VisibleTo(modId));

        internal static string Describe(IntentRecord? record) =>
            record == null ? "none" : record.Source + (record.IsLocalPlayer ? "/local" : "/" + record.ParticipantId);
    }

    /// <summary>
    /// Mod contexts over one composition root and one gameplay host, with a frame run as ModRuntime runs it: the host's
    /// sample and timers, FixedUpdate, the pump, every context's Update, the flush, then every context's LateUpdate.
    /// </summary>
    internal sealed class IntentWorld : IDisposable
    {
        private readonly List<ModContext> live = new List<ModContext>();

        internal IntentWorld(string root, InternalSessionRole role, params string[] modIds)
        {
            (Runtime, Control) = MultiplayerIntentTests.Online(role);
            Services = new MultiplayerReplication(Runtime);
            Host = new IntentGameplayFactory(Runtime.Intents);
            Contexts = modIds.Select(id =>
            {
                var paths = new ManagerPaths(Path.Combine(root, id));
                paths.EnsureCreated();
                var context = new ModContext(Manifest(id), paths, Path.Combine(root, id, "package"), new OwnerFacadeStoppingTests.Logger(),
                    Registry, null, Host, null, Services);
                live.Add(context);
                return context;
            }).ToArray();
        }

        internal MultiplayerRuntime Runtime { get; }

        internal FakeMultiplayerControl Control { get; }

        internal MultiplayerReplication Services { get; }

        internal MultiplayerIntents Intents => Runtime.Intents;

        internal IntentGameplayFactory Host { get; }

        internal ModServiceRegistry Registry { get; } = new ModServiceRegistry();

        internal ModContext[] Contexts { get; }

        internal void Frame(List<string> trace, string label)
        {
            trace.Add("frame:" + label);
            Host.BeginFrame();
            var sample = new GameTimeSample(GameLoopPhase.Fixed, 0.02f, 0.02f, 0d, 0);
            foreach (var context in live) context.RaiseFixedUpdate(sample);
            Tick(sampleInput: false);
        }

        /// <summary>One frame without the host's timers, whose scheduler allocates as it ticks; a test that measures runs these.</summary>
        internal void Tick(bool sampleInput = true)
        {
            if (sampleInput) Host.SampleInput();
            Runtime.BeforeModsUpdate(0f);
            for (var index = 0; index < live.Count; index++) live[index].RaiseUpdate(0f);
            Runtime.AfterModsUpdate(0f);
            var late = new GameTimeSample(GameLoopPhase.Late, 0f, 0f, 0d, 0);
            Runtime.BeforeModsLateUpdate(late);
            for (var index = 0; index < live.Count; index++) live[index].RaiseLateUpdate(late);
        }

        internal void GoOffline()
        {
            Control.Role = InternalSessionRole.Solo;
            Seam.Frame(Runtime);
        }

        internal ModContextScope Scope(ModContext parent, string session, HostDispatcher dispatcher, ModExecutionContext context)
        {
            var creation = parent.CreateChildScopeAsync(session, CancellationToken.None, () => { },
                new NativeTransitionAccessSlot(session + ":" + parent.Identity.Id, session, () => !parent.Lifetime.IsStopping), dispatcher, context);
            HostDispatcherTests.Pump(dispatcher, creation);
            return creation.GetAwaiter().GetResult();
        }

        internal void Release(ModContext context)
        {
            if (live.Remove(context)) context.DisposeLifetime();
        }

        public void Dispose()
        {
            foreach (var context in live.ToArray()) Release(context);
        }
    }

    /// <summary>A gameplay host whose services a test drives as the player would: input, interactions, a real scheduler and spawns.</summary>
    internal sealed class IntentGameplayFactory : IGameplayContextFactory
    {
        private readonly UnityScheduler scheduler = new UnityScheduler();
        private double elapsed;
        private long frame;

        internal IntentGameplayFactory(MultiplayerIntents intents)
        {
            Intents = intents;
        }

        internal MultiplayerIntents Intents { get; }

        internal List<IntentInputAction> Actions { get; } = new List<IntentInputAction>();

        internal List<(string Owner, Action<InteractionEvent> Handler)> Interactions { get; } = new List<(string, Action<InteractionEvent>)>();

        /// <summary>Gets each spawn, by its prefab's name, with what its mod observed as it spawned.</summary>
        internal List<(string Owner, string Name, string Intent)> Spawns { get; } = new List<(string, string, string)>();

        public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath, IModLifetime lifetime, IModLogger logger,
            ModNetProfile profile, NativeTransitionAccessSlot? transitionAccess = null)
        {
            var other = GameplayContextServices.Unavailable(lifetime);
            return new GameplayContextServices(new FakeInputService(this, ownerModId), other.LocalPlayer, other.Entities, other.Physics, other.Time,
                new OwnerScheduler(lifetime, scheduler, logger), other.Scenes, new CapturingInteractions(this, ownerModId), other.Items,
                new RecordingAssets(this, ownerModId, lifetime), other.Audio, other.Ui, other.UnityInterop, other.SceneTransitions);
        }

        /// <summary>The start of a frame, as the game's host runs it: input is sampled and due timers run, before the mods' Update.</summary>
        internal void BeginFrame(double seconds = 0.1)
        {
            elapsed += seconds;
            frame++;
            SampleInput();
            scheduler.Tick(elapsed, frame);
        }

        /// <summary>Samples every action, as the game's input sample does at the start of a frame.</summary>
        internal void SampleInput()
        {
            for (var index = 0; index < Actions.Count; index++) Actions[index].Sample();
        }

        internal IntentInputAction Action(string owner, string name) => Actions.Find(action => action.Owner == owner && action.Name == name)
            ?? throw new InvalidOperationException("No action " + owner + ":" + name + ".");

        internal void Interact(string owner)
        {
            var entity = new SeamEntity();
            foreach (var (registered, handler) in Interactions)
            {
                if (registered == owner) handler(new InteractionEvent(entity, new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0, 0, 1)))));
            }
        }

        private sealed class FakeInputService : IInputService
        {
            private readonly IntentGameplayFactory host;
            private readonly string owner;

            internal FakeInputService(IntentGameplayFactory host, string owner)
            {
                this.host = host;
                this.owner = owner;
            }

            public bool IsUiFocused => false;

            public IReadOnlyList<InputConflict> GetConflicts() => Array.Empty<InputConflict>();

            public OperationResult<IInputAction> RegisterAction(InputActionDefinition definition)
            {
                var action = new IntentInputAction(owner, definition.Name);
                host.Actions.Add(action);
                return OperationResult<IInputAction>.Success(action);
            }
        }

        private sealed class CapturingInteractions : IInteractionService
        {
            private readonly IntentGameplayFactory host;
            private readonly string owner;

            internal CapturingInteractions(IntentGameplayFactory host, string owner)
            {
                this.host = host;
                this.owner = owner;
            }

            public OperationResult<IInteractableRegistration> Register(IEntity entity, InteractableDefinition definition, Action<InteractionEvent> handler)
            {
                if (handler == null) throw new ArgumentNullException(nameof(handler));
                host.Interactions.Add((owner, handler));
                return OperationResult<IInteractableRegistration>.Failure(ModErrorCode.Unavailable, "The fake host keeps no registration.");
            }

            public bool TryGetFocused(out IInteractableRegistration? interaction)
            {
                interaction = null;
                return false;
            }
        }

        private sealed class RecordingAssets : IAssetService
        {
            private readonly IntentGameplayFactory host;
            private readonly string owner;
            private readonly IModLifetime lifetime;

            internal RecordingAssets(IntentGameplayFactory host, string owner, IModLifetime lifetime)
            {
                this.host = host;
                this.owner = owner;
                this.lifetime = lifetime;
            }

            public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken cancellationToken = default) =>
                Task.FromResult(OperationResult<IAssetBundle>.Failure(ModErrorCode.Unavailable, "no bundles"));

            public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string assetName, CancellationToken cancellationToken = default) =>
                Task.FromResult(OperationResult<IPrefabAsset>.Failure(ModErrorCode.Unavailable, "no prefabs"));

            public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
            {
                host.Spawns.Add((owner, request.Prefab.Name, MultiplayerIntentTests.Seen(host.Intents, owner)));
                var spawned = new SeamEntity();
                lifetime.Track(spawned);
                return OperationResult<ISpawnedEntity>.Success(spawned);
            }
        }
    }

    /// <summary>An input action the test holds and lets go of; the host's sample turns that into edges, as the game's does.</summary>
    internal sealed class IntentInputAction : IInputAction
    {
        private bool down;

        internal IntentInputAction(string owner, string name)
        {
            Owner = owner;
            Name = name;
        }

        internal string Owner { get; }

        internal bool Disposed { get; private set; }

        internal bool ThrowOnRead { get; set; }

        public string Name { get; }

        public IReadOnlyList<InputBinding> Bindings { get; private set; } = new[] { InputBinding.Key("Space") };

        public float Value => IsHeld ? 1f : 0f;

        public bool IsHeld => ThrowOnRead ? throw new InvalidOperationException("unreadable action") : held;

        public bool WasPressed { get; private set; }

        public bool WasReleased { get; private set; }

        private bool held;

        internal void Hold(bool value) => down = value;

        internal void Sample()
        {
            var previous = held;
            held = down && !Disposed;
            WasPressed = held && !previous;
            WasReleased = !held && previous;
        }

        public OperationResult<bool> Rebind(IEnumerable<InputBinding> bindings)
        {
            Bindings = new List<InputBinding>(bindings).AsReadOnly();
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> ResetBindings() => OperationResult<bool>.Success(true);

        public void Dispose()
        {
            Disposed = true;
            held = false;
            WasPressed = false;
            WasReleased = false;
        }
    }

    /// <summary>A prefab a test spawns by name, so a spawn says what made it.</summary>
    internal sealed class IntentPrefab : IPrefabAsset
    {
        internal IntentPrefab(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public bool IsAlive => true;

        public void Dispose()
        {
        }
    }
}
