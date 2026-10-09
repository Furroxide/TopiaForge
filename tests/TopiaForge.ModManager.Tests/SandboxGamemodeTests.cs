using System;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Sandbox;

namespace TopiaForge.ModManager.Tests
{
    internal static class SandboxGamemodeTests
    {
        internal static void Run()
        {
            HostRejectsBeforeRunning();
            FailedHostRegistrationReleasesController();
            RepeatedSessionsRetainOnlyPackageCommands();
        }

        internal static void HostRejectsBeforeRunning()
        {
            using var fixture = new Fixture();
            var session = fixture.Session("starting");
            fixture.Sessions.Publish(WorldSessionPhase.StartingMode, session, session.World);
            var result = new SandboxGamemode().StartAsync(session, default).GetAwaiter().GetResult();
            Assert(result.Succeeded, "Sandbox factory starts against the scoped services");
            using var controller = result.Value!;
            Assert(fixture.Router.Host != null && !fixture.Router.Host.CanOpen(new CreatorToolOpenContext("Arena")),
                "F5 host must reject before the orchestrator commits Running");
            fixture.Sessions.Publish(WorldSessionPhase.Running, session, session.World);
            Assert(fixture.Router.Host!.CanOpen(new CreatorToolOpenContext("Arena")), "F5 host admits the running session");
            fixture.Sessions.Publish(WorldSessionPhase.Stopping, session, session.World);
            Assert(!fixture.Router.Host.CanOpen(new CreatorToolOpenContext("Arena")), "F5 host rejects while stopping");
        }

        private static void FailedHostRegistrationReleasesController()
        {
            using var fixture = new Fixture();
            fixture.Router.FailRegistration = true;
            var baseline = fixture.Context.Lifetime.TrackedResourceCount;
            var result = new SandboxGamemode().StartAsync(fixture.Session("failed"), default).GetAwaiter().GetResult();
            Assert(result.ErrorCode == ModErrorCode.Conflict && fixture.Context.Events.ActiveSubscriptionCount == 0
                && fixture.Context.Lifetime.TrackedResourceCount == baseline
                && !fixture.Context.Extensions.TryGet<SandboxSessionCommands>(out _),
                "a failed host registration cleans the allocated controller before returning failure");
        }

        private static void RepeatedSessionsRetainOnlyPackageCommands()
        {
            using var fixture = new Fixture();
            fixture.Context.Config.Seed(1, new SandboxConfig { SpawnMenuKey = "Q" });
            using var runner = ModLifecycleRunner.Create<SandboxMod>(fixture.Context);
            runner.Load();
            Assert(SandboxMod.LoadConfiguration(fixture.Context).SpawnMenuKey == "F5", "schema-one config migration is preserved");
            Assert(fixture.Context.Commands.ActiveCommandCount == 5 && fixture.Context.Events.ActiveSubscriptionCount == 0,
                "loading the package registers five command names without constructing a controller");
            var baseline = fixture.Context.Lifetime.TrackedResourceCount;
            for (var cycle = 0; cycle < 5; cycle++)
            {
                Assert(Command(fixture, "sandbox-status").ErrorCode == ModErrorCode.InvalidState, "inactive command keeps its guidance");
                var session = fixture.Session("cycle-" + cycle);
                fixture.Sessions.Publish(WorldSessionPhase.StartingMode, session, session.World);
                var result = new SandboxGamemode().StartAsync(session, default).GetAwaiter().GetResult();
                Assert(result.Succeeded && fixture.Router.RegistrationCount == 1 && fixture.Pause.ActiveActionCount == 1,
                    "factory owns one creator host and pause action");
                Assert(Command(fixture, "sandbox-status").ErrorCode == ModErrorCode.InvalidState, "commands reject StartingMode");
                fixture.Sessions.Publish(WorldSessionPhase.Running, session, session.World);
                Assert(Command(fixture, "sandbox-status").Succeeded && Command(fixture, "sandbox-end").Succeeded
                    && session.StopRequests == 0, "workbench end preserves gamemode ownership");
                var other = fixture.Session("other-" + cycle);
                fixture.Sessions.Publish(WorldSessionPhase.Running, other, other.World);
                Assert(Command(fixture, "sandbox-clear").ErrorCode == ModErrorCode.InvalidState, "stale commands reject the next session");
                result.Value!.Dispose();
                fixture.Sessions.Publish(WorldSessionPhase.Idle);
                Assert(fixture.Router.RegistrationCount == 0 && fixture.Pause.ActiveActionCount == 0
                    && fixture.Context.Events.ActiveSubscriptionCount == 0
                    && fixture.Context.Lifetime.TrackedResourceCount == baseline,
                    "session cleanup removes its host, pause and target while package commands remain");
            }
            runner.Unload();
            fixture.Context.AssertNoLeaks();
        }

        private static OperationResult<string> Command(Fixture fixture, string name)
        {
            Assert(fixture.Context.Commands.TryExecute(name, Array.Empty<string>(), out var result), "package command exists");
            return result!;
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Assertion failed: " + message); }

        private sealed class Fixture : IDisposable
        {
            internal Fixture()
            {
                Context = new FakeModContext(new ModIdentity("io.github.furroxide.topiaforge.sandbox", "Sandbox", SemanticVersion.Parse("1.0.0")));
                Content = new FakeCreatorContentService(Context.Lifetime);
                Router = new HostRouter(Context.Lifetime, Context.Identity.Id);
                Pause = new FakeWorldPauseMenuService(Context.Lifetime);
                Context.Extensions.Register<IRobotAgentService>(new FakeRobotKit(Context.Lifetime).Agents);
                Context.Extensions.Register<ICreatorContentService>(Content);
                Context.Extensions.Register<ICreatorToolHostService>(Router);
                Context.Extensions.Register<IWorldSessionService>(Sessions);
                Context.Extensions.Register<IWorldPauseMenuService>(Pause);
            }
            internal FakeModContext Context { get; }
            internal FakeCreatorContentService Content { get; }
            internal HostRouter Router { get; }
            internal FakeWorldPauseMenuService Pause { get; }
            internal FakeWorldSessionService Sessions { get; } = new FakeWorldSessionService();
            internal FakeGamemodeSession Session(string id) => new FakeGamemodeSession(Context,
                new WorldReadiness(new WorldSceneIdentity(1, "Arena"), TransformState.Identity), id,
                "io.github.furroxide.topiaforge.sandbox.creator.menu", SandboxMod.GamemodeId);
            public void Dispose() { Content.Dispose(); Context.Dispose(); }
        }

        private sealed class HostRouter : ICreatorToolHostService
        {
            private readonly FakeModLifetime lifetime;
            private readonly string owner;
            internal HostRouter(FakeModLifetime lifetime, string owner) { this.lifetime = lifetime; this.owner = owner; }
            internal ICreatorToolHost? Host { get; private set; }
            internal bool FailRegistration { get; set; }
            internal int RegistrationCount => Host == null ? 0 : 1;
            public CreatorToolHostDescriptor? ActiveHost => null;
            public OperationResult<ICreatorToolHostRegistration> RegisterHost(CreatorToolHostRegistrationRequest request)
            {
                if (FailRegistration || Host != null)
                    return OperationResult<ICreatorToolHostRegistration>.Failure(ModErrorCode.Conflict, "Injected host conflict.");
                Host = request.Host;
                var registration = new Registration(new CreatorToolHostDescriptor(owner + ":" + request.LocalId,
                    owner, request.LocalId, request.DisplayName, request.Priority), () => Host = null);
                registration.Lease = lifetime.Track(registration);
                return OperationResult<ICreatorToolHostRegistration>.Success(registration);
            }
            public OperationResult<bool> Toggle() => OperationResult<bool>.Success(false);
            public OperationResult<bool> CloseActive(CreatorToolCloseReason reason = CreatorToolCloseReason.Requested) => OperationResult<bool>.Success(false);
            private sealed class Registration : ICreatorToolHostRegistration
            {
                private Action? remove;
                internal Registration(CreatorToolHostDescriptor descriptor, Action remove) { Descriptor = descriptor; this.remove = remove; }
                internal IDisposable? Lease;
                public CreatorToolHostDescriptor Descriptor { get; }
                public bool IsAlive => remove != null;
                public void Dispose()
                {
                    var callback = Interlocked.Exchange(ref remove, null);
                    try { callback?.Invoke(); } finally { Interlocked.Exchange(ref Lease, null)?.Dispose(); }
                }
            }
        }
    }
}
