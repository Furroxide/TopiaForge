using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    // Foundation fixture using real scoped contexts, reflection constructors and the native executor.
    // Production verified-package binding is intentionally not supplied by this fixture.
    internal sealed class SessionFixture : IDisposable, IRuntimeSessionEnvironment, IGameplayContextFactory
    {
        internal static Func<IWorldLoadContext, CancellationToken, Task<OperationResult<IWorldInstance>>> Load = null!;
        internal static Func<IGamemodeSession, CancellationToken, Task<OperationResult<IGamemodeController>>> Start = null!;
        internal static Func<IGamemodeParticipantSession, CancellationToken, Task<OperationResult<IGamemodeParticipantController>>> StartParticipant = null!;
        internal static IGamemodeParticipantSession? ParticipantSession;
        internal static Action ProviderConstructor = null!;
        internal static Action FactoryConstructor = null!;
        internal static IGamemodeSession? ModeSession;
        internal static IModLifetime? ChildLifetime;
        internal static NativeTransitionAccessSlot? Slot;
        internal readonly HostDispatcher Host = new HostDispatcher();
        internal readonly SceneCoordinator Native;
        internal readonly DelayedSessionDispatcher Dispatch;
        internal readonly GamemodeSessionOrchestrator Hosted;
        internal readonly ModContext Parent;
        internal readonly LaunchPlan Plan;
        internal readonly EffectiveProfile Profile;
        internal readonly List<LaunchOutcome> Outcomes = new List<LaunchOutcome>();
        internal bool InvalidBindings;
        internal Action<IModLifetime>? ScopeFactory;
        internal int MenuLoads;
        internal Func<CancellationToken, Task<OperationResult<bool>>>? Menu;
        /// <summary>The gamemode's implementation type; <see cref="SessionTestParticipantFactory"/> adds a participant.</summary>
        internal Type FactoryType = typeof(SessionTestFactory);

        /// <param name="replication">The process's replication services, whose realm the session starts in; single player when null.</param>
        /// <param name="configure">Changes the package's manifest before its context is made, such as a dependency to resolve.</param>
        /// <param name="registry">The service registry the package context resolves extensions through.</param>
        /// <param name="available">The other manifests the package's dependencies may name.</param>
        internal SessionFixture(string root, bool endOnScene = false, Func<INativeTransitionExecutor, INativeTransitionExecutor>? decorateNative = null,
            MultiplayerReplication? replication = null, Action<ModManifest>? configure = null, ModServiceRegistry? registry = null,
            IEnumerable<ModManifest>? available = null)
        {
            Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
            Start = (session, _) =>
            {
                ModeSession = session;
                return Task.FromResult(OperationResult<IGamemodeController>.Success(new SessionTestController()));
            };
            StartParticipant = (participant, _) =>
            {
                ParticipantSession = participant;
                return Task.FromResult(OperationResult<IGamemodeParticipantController>.Success(new SessionTestParticipantController()));
            };
            ParticipantSession = null;
            ProviderConstructor = () => { };
            FactoryConstructor = () => { };
            ModeSession = null; ChildLifetime = null; Slot = null;
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.FindRepoRoot(),
                "tests", "fixtures", "gamemode-v6", "resolution", "auto-transition-prefers-scene-replacement.json")));
            var manifest = ModManifestJson.Deserialize(fixture.RootElement.GetProperty("profile").GetProperty("packages")[0]
                .GetProperty("manifest").GetRawText());
            manifest.Contributions!.Gamemodes[0].SceneChangePolicy = endOnScene
                ? ModGamemodeDeclaration.EndSessionPolicy : ModGamemodeDeclaration.KeepControllerPolicy;
            configure?.Invoke(manifest);
            Profile = new EffectiveProfile("session-fixture", 1, new[] { new ResolvedPackage(manifest.Id, manifest.Version, manifest) });
            Plan = LaunchResolver.Resolve(Profile, new LaunchRequest(manifest.Contributions.LaunchTargets[0].Id)).Plan!;
            var paths = new ManagerPaths(root); paths.EnsureCreated();
            Parent = new ModContext(manifest, paths, Path.Combine(root, "package"), new OwnerFacadeStoppingTests.Logger(),
                registry ?? new ModServiceRegistry(), null, this, available, replication);
            Native = new SceneCoordinator(dispatcher: Host);
            Dispatch = new DelayedSessionDispatcher(Host);
            Hosted = new GamemodeSessionOrchestrator(Dispatch, decorateNative?.Invoke(Native) ?? Native, this, "runtime-fixture");
            Hosted.Outcome += Outcomes.Add;
        }

        public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath,
            IModLifetime lifetime, IModLogger logger, ModNetProfile profile, NativeTransitionAccessSlot? transitionAccess = null)
        {
            if (transitionAccess != null) { ChildLifetime = lifetime; Slot = transitionAccess; ScopeProfiles.Add(profile); ScopeFactory?.Invoke(lifetime); }
            return GameplayContextServices.Unavailable(lifetime);
        }

        /// <summary>Gets the network profile each session scope's gameplay services were made for, in order.</summary>
        internal List<ModNetProfile> ScopeProfiles { get; } = new List<ModNetProfile>();
        public RuntimeSessionSnapshot Capture()
        {
            var bindings = new RuntimeBindingSnapshot(Profile.ProfileId, Profile.Revision, Plan.Digest,
                InvalidBindings ? Array.Empty<string>() : new[] { Plan.WorldId },
                InvalidBindings ? Array.Empty<string>() : new[] { Plan.GamemodeId });
            return new RuntimeSessionSnapshot(Profile, bindings, new Dictionary<string, ModContext> { [Parent.Identity.Id] = Parent },
                new[] { new SessionImplementation<IGamemodeFactory>(Plan.Packages[0], Plan.GamemodeId, FactoryType) },
                new[] { new SessionImplementation<IWorldContentProvider>(Plan.Packages[0], Plan.WorldId, typeof(SessionTestProvider)) });
        }
        public Task<OperationResult<bool>> LoadMainMenuAsync(IInternalSceneTransitionService transitions, CancellationToken cancellationToken)
        { MenuLoads++; return Menu?.Invoke(cancellationToken) ?? Task.FromResult(OperationResult<bool>.Success(true)); }
        internal void Launch(string request = "request")
        {
            var task = Hosted.StartAsync(Plan.Descriptor, request);
            Wait(task);
            GamemodeSessionOrchestratorTests.Assert(task.Result.Succeeded, "expected successful launch: " + task.Result.ErrorMessage);
        }
        internal void Wait(Task task) => HostDispatcherTests.Pump(Host, task);
        internal void Until(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!ready() && DateTime.UtcNow < deadline) { Host.Drain(); Thread.Sleep(1); }
            GamemodeSessionOrchestratorTests.Assert(ready(), "timed out waiting for session state");
            Host.Drain();
        }
        public void Dispose()
        {
            Wait(Hosted.ShutdownAsync());
            Parent.BeginStopping();
            Parent.DisposeLifetime();
            Until(() => !Host.HasPendingWork);
            Host.Dispose();
        }
    }

    internal sealed class DelayedSessionDispatcher : IHostDispatcher
    {
        private readonly HostDispatcher host;
        internal bool HoldNextPost;
        internal int PostedCount;
        private Action? held;
        internal DelayedSessionDispatcher(HostDispatcher host) { this.host = host; }
        public bool IsCurrent => host.IsCurrent;
        public void Post(Action action)
        {
            Interlocked.Increment(ref PostedCount);
            if (HoldNextPost) { HoldNextPost = false; held = action; }
            else host.Post(action);
        }
        internal void Release() { var action = held!; held = null; host.Post(action); }
        public Task InvokeAsync(Action action) => host.InvokeAsync(action);
        public Task<T> InvokeAsync<T>(Func<T> action) => host.InvokeAsync(action);
        public Task<T> InvokeCallbackAsync<T>(Func<Task<T>> callback) => host.InvokeCallbackAsync(callback);
    }

    public sealed class SessionTestProvider : IWorldContentProvider, IDisposable
    {
        public SessionTestProvider() => SessionFixture.ProviderConstructor();
        public Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken token) => SessionFixture.Load(context, token);
        public void Dispose() { }
    }
    public sealed class SessionTestFactory : IGamemodeFactory, IDisposable
    {
        public SessionTestFactory() => SessionFixture.FactoryConstructor();
        public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken token) => SessionFixture.Start(session, token);
        public void Dispose() { }
    }
    /// <summary>A gamemode that gives the local player a participant controller too.</summary>
    public sealed class SessionTestParticipantFactory : IGamemodeParticipantFactory, IDisposable
    {
        public SessionTestParticipantFactory() => SessionFixture.FactoryConstructor();
        public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken token) => SessionFixture.Start(session, token);
        public Task<OperationResult<IGamemodeParticipantController>> StartParticipantAsync(IGamemodeParticipantSession participant, CancellationToken token) =>
            SessionFixture.StartParticipant(participant, token);
        public void Dispose() { }
    }
    public sealed class SessionTestParticipantController : IGamemodeParticipantController
    {
        private readonly Action? dispose;
        public SessionTestParticipantController(Action? dispose = null) { this.dispose = dispose; }
        public void Dispose() => dispose?.Invoke();
    }
    public sealed class SessionTestWorld : IWorldInstance
    {
        private readonly Action? dispose;
        public SessionTestWorld(Action? dispose = null) { this.dispose = dispose; }
        public WorldReadiness Readiness { get; } = new WorldReadiness(new WorldSceneIdentity(-12, "World"), TransformState.Identity);
        public void Dispose() => dispose?.Invoke();
    }
    public sealed class SessionTestController : IGamemodeController
    {
        private readonly Action? dispose;
        public SessionTestController(Action? dispose = null) { this.dispose = dispose; }
        public void Dispose() => dispose?.Invoke();
    }
}
