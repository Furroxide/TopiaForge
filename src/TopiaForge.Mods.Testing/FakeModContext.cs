using System;

namespace TopiaForge.Mods.Testing
{
    /// <summary>
    /// Complete runner-neutral SDK context with deterministic services and no filesystem or game dependency.
    /// </summary>
    public sealed class FakeModContext : IModContext, IDisposable
    {
        private bool disposed;

        /// <summary>Creates a context with stable V1 test defaults.</summary>
        /// <param name="identity">Optional manifest identity for the mod under test.</param>
        /// <param name="runtime">Optional mutable runtime metadata.</param>
        public FakeModContext(ModIdentity? identity = null, FakeRuntimeInfo? runtime = null)
        {
            Identity = identity ?? new ModIdentity(
                "example.test-mod",
                "Test Mod",
                SemanticVersion.Parse("1.0.0"));
            Runtime = runtime ?? new FakeRuntimeInfo();
            Logger = new CapturedModLogger();
            Lifetime = new FakeModLifetime();
            Events = new FakeModEvents(Lifetime, Logger);
            FileSystem = new InMemoryModFileSystem();
            Files = new InMemoryModFiles(FileSystem, Lifetime);
            Config = new InMemoryModConfigService();
            Storage = new InMemoryModStorageService();
            Input = new FakeInputService(Lifetime);
            Time = new DeterministicGameTime();
            Scheduler = new DeterministicModScheduler(Lifetime, Logger);
            Player = new FakePlayerService(Lifetime);
            Entities = new FakeEntityService(Lifetime);
            Physics = new FakePhysicsService();
            Interactions = new FakeInteractionService(Lifetime);
            Items = new FakeItemService(Entities);
            Assets = new FakeAssetService(Lifetime);
            Audio = new FakeAudioService(Lifetime);
            Ui = new FakeUiService(Lifetime);
            Localization = new FakeLocalizationService(Lifetime);
            Commands = new FakeCommandService(Identity, Lifetime);
            Diagnostics = new FakeDiagnosticsService();
            Extensions = new FakeExtensionService(Lifetime);
            Scenes = new FakeSceneService(Events, Lifetime);
        }

        /// <inheritdoc/>
        public ModIdentity Identity { get; }

        /// <summary>Gets mutable host-runtime metadata.</summary>
        public FakeRuntimeInfo Runtime { get; }

        /// <summary>Gets the deterministic resource lifetime.</summary>
        public FakeModLifetime Lifetime { get; }

        /// <summary>Gets the deterministic event source.</summary>
        public FakeModEvents Events { get; }

        /// <summary>Gets the shared in-memory byte store.</summary>
        public InMemoryModFileSystem FileSystem { get; }

        /// <summary>Gets in-memory package and persistent data content operations.</summary>
        public InMemoryModFiles Files { get; }

        /// <summary>Gets typed in-memory configuration.</summary>
        public InMemoryModConfigService Config { get; }

        /// <summary>Gets scoped in-memory save storage.</summary>
        public InMemoryModStorageService Storage { get; }

        /// <summary>Gets deterministic named input.</summary>
        public FakeInputService Input { get; }

        /// <summary>Gets mutable player state.</summary>
        public FakePlayerService Player { get; }

        /// <summary>Gets deterministic entities and motion leases.</summary>
        public FakeEntityService Entities { get; }

        /// <summary>Gets explicitly configured physics queries.</summary>
        public FakePhysicsService Physics { get; }

        /// <summary>Gets deterministic interactable registration and focus state.</summary>
        public FakeInteractionService Interactions { get; }

        /// <summary>Gets in-memory held-item and grant state.</summary>
        public FakeItemService Items { get; }

        /// <summary>Gets in-memory asset handles and prefab spawning.</summary>
        public FakeAssetService Assets { get; }

        /// <summary>Gets captured audio playbacks.</summary>
        public FakeAudioService Audio { get; }

        /// <summary>Gets captured TopiaForgeUi surfaces, modals, and toasts.</summary>
        public FakeUiService Ui { get; }

        /// <summary>Gets in-memory localization catalogs.</summary>
        public FakeLocalizationService Localization { get; }

        /// <summary>Gets the deterministic owner-scoped command registry.</summary>
        public FakeCommandService Commands { get; }

        /// <summary>Gets bounded structured diagnostics.</summary>
        public FakeDiagnosticsService Diagnostics { get; }

        /// <summary>Gets typed extension-provider registrations.</summary>
        public FakeExtensionService Extensions { get; }

        /// <summary>Gets explicitly advanced game-loop samples.</summary>
        public DeterministicGameTime Time { get; }

        /// <summary>Gets the virtual-time scheduler.</summary>
        public DeterministicModScheduler Scheduler { get; }

        /// <summary>Gets deterministic scene state.</summary>
        public FakeSceneService Scenes { get; }

        /// <summary>Gets captured mod log messages.</summary>
        public CapturedModLogger Logger { get; }

        IRuntimeInfo IModContext.Runtime => Runtime;
        IModLifetime IModContext.Lifetime => Lifetime;
        IModEvents IModContext.Events => Events;
        IModFiles IModContext.Files => Files;
        IModConfigService IModContext.Config => Config;
        IModStorageService IModContext.Storage => Storage;
        IInputService IModContext.Input => Input;
        IPlayerService IModContext.Player => Player;
        ISceneService IModContext.Scenes => Scenes;
        IEntityService IModContext.Entities => Entities;
        IPhysicsService IModContext.Physics => Physics;
        IInteractionService IModContext.Interactions => Interactions;
        IItemService IModContext.Items => Items;
        IAssetService IModContext.Assets => Assets;
        IAudioService IModContext.Audio => Audio;
        IUiService IModContext.Ui => Ui;
        ILocalizationService IModContext.Localization => Localization;
        ICommandService IModContext.Commands => Commands;
        IDiagnosticsService IModContext.Diagnostics => Diagnostics;
        IExtensionService IModContext.Extensions => Extensions;
        IGameTime IModContext.Time => Time;
        IModScheduler IModContext.Scheduler => Scheduler;
        IModLogger IModContext.Logger => Logger;

        /// <summary>
        /// Advances a complete rendered frame: time and scheduler first, then frame and late events, then clears
        /// transient input edges.
        /// </summary>
        public void AdvanceFrame(TimeSpan duration)
        {
            ThrowIfDisposed();
            var seconds = ToFiniteSeconds(duration, allowZero: true);
            var frame = Time.AdvanceFrame(seconds);
            Scheduler.AdvanceFrame(duration);
            Events.RaiseUpdate(frame.DeltaTime);
            Events.RaiseLateUpdate(Time.StepLate());
            Input.FinishFrame();
        }

        /// <summary>Raises one fixed-physics callback at the current rendered-frame time.</summary>
        public void StepFixed(TimeSpan duration)
        {
            ThrowIfDisposed();
            var seconds = ToFiniteSeconds(duration, allowZero: false);
            Events.RaiseFixedUpdate(Time.StepFixed(seconds));
        }

        /// <summary>Asserts that shutdown released every service resource owned by this context.</summary>
        public void AssertNoLeaks() => ModLeakAssertions.AssertNoLeaks(this);

        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Lifetime.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(FakeModContext));
            }
        }

        private static float ToFiniteSeconds(TimeSpan duration, bool allowZero)
        {
            var seconds = duration.TotalSeconds;
            if (seconds < 0d || (!allowZero && seconds == 0d) || seconds > float.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            return (float)seconds;
        }
    }
}
