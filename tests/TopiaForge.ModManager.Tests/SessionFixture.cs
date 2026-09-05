using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
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

        internal SessionFixture(string root, bool endOnScene = false)
        {
            Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new SessionTestWorld()));
            Start = (session, _) =>
            {
                ModeSession = session;
                return Task.FromResult(OperationResult<IGamemodeController>.Success(new SessionTestController()));
            };
            ProviderConstructor = () => { };
            FactoryConstructor = () => { };
            ModeSession = null; ChildLifetime = null; Slot = null;
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.FindRepoRoot(),
                "tests", "fixtures", "gamemode-v6", "resolution", "auto-transition-prefers-scene-replacement.json")));
            var manifest = ModManifestJson.Deserialize(fixture.RootElement.GetProperty("profile").GetProperty("packages")[0]
                .GetProperty("manifest").GetRawText());
            manifest.Contributions!.Gamemodes[0].SceneChangePolicy = endOnScene
                ? ModGamemodeDeclaration.EndSessionPolicy : ModGamemodeDeclaration.KeepControllerPolicy;
            Profile = new EffectiveProfile("session-fixture", 1, new[] { new ResolvedPackage(manifest.Id, manifest.Version, manifest) });
            Plan = LaunchResolver.Resolve(Profile, new LaunchRequest(manifest.Contributions.LaunchTargets[0].Id)).Plan!;
            var paths = new ManagerPaths(root); paths.EnsureCreated();
            Parent = new ModContext(manifest, paths, Path.Combine(root, "package"), new OwnerFacadeStoppingTests.Logger(),
                new ModServiceRegistry(), null, this);
            Native = new SceneCoordinator(dispatcher: Host);
            Dispatch = new DelayedSessionDispatcher(Host);
            Hosted = new GamemodeSessionOrchestrator(Dispatch, Native, this, "runtime-fixture");
            Hosted.Outcome += Outcomes.Add;
        }

        public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath,
            IModLifetime lifetime, IModLogger logger, NativeTransitionAccessSlot? transitionAccess = null)
        {
            if (transitionAccess != null) { ChildLifetime = lifetime; Slot = transitionAccess; ScopeFactory?.Invoke(lifetime); }
            return GameplayContextServices.Unavailable(lifetime);
        }
        public RuntimeSessionSnapshot Capture()
        {
            var bindings = new RuntimeBindingSnapshot(Profile.ProfileId, Profile.Revision, Plan.Digest,
                InvalidBindings ? Array.Empty<string>() : new[] { Plan.WorldId },
                InvalidBindings ? Array.Empty<string>() : new[] { Plan.GamemodeId });
            return new RuntimeSessionSnapshot(Profile, bindings, new Dictionary<string, ModContext> { [Parent.Identity.Id] = Parent },
                new[] { new SessionImplementation<IGamemodeFactory>(Plan.Packages[0], Plan.GamemodeId, typeof(SessionTestFactory)) },
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
        private Action? held;
        internal DelayedSessionDispatcher(HostDispatcher host) { this.host = host; }
        public bool IsCurrent => host.IsCurrent;
        public void Post(Action action)
        {
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
