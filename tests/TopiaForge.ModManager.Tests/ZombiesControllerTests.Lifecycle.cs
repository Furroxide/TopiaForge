using System;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        private static void CancelledFactoryStartupCleansAllocatedResources()
        {
            using var context = new FakeModContext();
            using var cancellation = new CancellationTokenSource();
            var sessions = new FakeWorldSessionService();
            var robots = new FakeRobotKit(context.Lifetime);
            var pause = new FakeWorldPauseMenuService(context.Lifetime);
            context.Extensions.Register<IWorldSessionService>(sessions);
            context.Extensions.Register<IRobotAgentService>(robots.Agents);
            context.Extensions.Register<IWorldPauseMenuService>(new CancellingPause(pause, cancellation));
            var session = NewSession(context, "cancelled-start");
            sessions.Publish(WorldSessionPhase.StartingMode, session, session.World);
            var baseline = context.Lifetime.TrackedResourceCount;
            var result = new ZombiesGamemode().StartAsync(session, cancellation.Token).GetAwaiter().GetResult();
            Assert(result.ErrorCode == ModErrorCode.Cancelled && cancellation.IsCancellationRequested
                && pause.ActiveActionCount == 0 && context.Ui.Surfaces.Count == 0
                && context.Input.ActiveActionCount == 0 && context.Events.ActiveSubscriptionCount == 0
                && !context.Extensions.TryGet<ZombiesSessionCommands>(out _)
                && context.Lifetime.TrackedResourceCount == baseline,
                "cancellation after controller and pause allocation cleans everything without publishing a command target");
        }

        private static void SuccessfulModLifecycleReusesOneContextWithoutSessionLeaks()
        {
            var context = new FakeModContext(new ModIdentity("io.github.furroxide.topiaforge.zombies", "Zombies", SemanticVersion.Parse("1.0.0")));
            context.Config.Seed(2, FastConfig());
            context.Scenes.Load("ZombiesArena");
            context.LocalPlayer.Snapshot = new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
            var sessions = new FakeWorldSessionService();
            var robots = new FakeRobotKit(context.Lifetime);
            robots.Agents.PlayerEntity = new FakeEntity("zombies-player", "Player", Vec3.Zero);
            var pause = new FakeWorldPauseMenuService(context.Lifetime);
            context.Extensions.Register<IWorldSessionService>(sessions);
            context.Extensions.Register<IRobotAgentService>(robots.Agents);
            context.Extensions.Register<IWorldPauseMenuService>(pause);
            using var runner = ModLifecycleRunner.Create<ZombiesMod>(context);
            runner.Load();
            Assert(context.Commands.ActiveCommandCount == 3 && context.Ui.Surfaces.Count == 0,
                "package load retains command names without creating gameplay");
            var baseline = context.Lifetime.TrackedResourceCount;
            for (var cycle = 0; cycle < 10; cycle++)
            {
                Assert(context.Commands.TryExecute("zombies-status", Array.Empty<string>(), out var inactive)
                    && inactive!.ErrorCode == ModErrorCode.InvalidState, "inactive command names remain discoverable");
                var session = NewSession(context, "cycle-" + cycle);
                sessions.Publish(WorldSessionPhase.StartingMode, session, session.World);
                var result = new ZombiesGamemode().StartAsync(session, default).GetAwaiter().GetResult();
                Assert(result.Succeeded && pause.ActiveActionCount == 1 && context.Ui.Surfaces.Count == 1,
                    "factory creates the controller and one scoped pause action");
                Assert(context.Commands.TryExecute("zombies-status", Array.Empty<string>(), out var starting)
                    && starting!.ErrorCode == ModErrorCode.InvalidState, "commands reject before committed Running");
                sessions.Publish(WorldSessionPhase.Running, session, session.World);
                Assert(context.Commands.TryExecute("zombies-status", Array.Empty<string>(), out var running) && running!.Succeeded,
                    "matching running session accepts retained package commands");
                Assert(pause.Invoke("zombies-restart") && session.RestartRequests == 0,
                    "restart run retains its wave-reset behavior instead of requesting a world reload");
                var successor = NewSession(context, "different-" + cycle);
                sessions.Publish(WorldSessionPhase.Running, successor, successor.World);
                Assert(context.Commands.TryExecute("zombies-restart", Array.Empty<string>(), out var stale)
                    && stale!.ErrorCode == ModErrorCode.InvalidState, "stale targets cannot control another committed session");
                result.Value!.Dispose();
                sessions.Publish(WorldSessionPhase.Idle);
                Assert(pause.ActiveActionCount == 0 && context.Ui.Surfaces.Count == 0
                    && context.Input.ActiveActionCount == 0 && context.Events.ActiveSubscriptionCount == 0
                    && robots.Agents.ActiveAgents.Count == 0 && context.Lifetime.TrackedResourceCount == baseline,
                    "controller cleanup returns the loaded package to its exact resource baseline");
            }
            runner.Unload();
            context.AssertNoLeaks();
        }

        private static FakeGamemodeSession NewSession(FakeModContext context, string id) => new FakeGamemodeSession(context,
            new WorldReadiness(new WorldSceneIdentity(1, "ZombiesArena"), TransformState.Identity), id,
            "io.github.furroxide.topiaforge.zombies.menu", ZombiesMod.GamemodeId);

        private sealed class CancellingPause : IWorldPauseMenuService
        {
            private readonly FakeWorldPauseMenuService inner;
            private readonly CancellationTokenSource cancellation;
            internal CancellingPause(FakeWorldPauseMenuService inner, CancellationTokenSource cancellation)
            { this.inner = inner; this.cancellation = cancellation; }
            public bool IsAvailable => inner.IsAvailable;
            public OperationResult<IDisposable> RegisterAction(WorldPauseAction action)
            { var result = inner.RegisterAction(action); cancellation.Cancel(); return result; }
            public OperationResult<IDisposable> InterceptExit(Func<WorldPauseExitContext, WorldPauseExitDecision> interceptor) =>
                inner.InterceptExit(interceptor);
        }
        private static void SceneReadinessRequiresTheSessionScene()
        {
            var config = FastConfig();
            using var harness = new Harness(config, activeScene: "WrongArena", sessionScene: "ZombiesArena");

            for (var index = 0; index < 8; index++)
            {
                harness.Advance(0.25f);
            }

            Assert(harness.Controller.TestingPhase == ZombiesPhase.WaitingForWorld
                && harness.Controller.TestingWave == 0
                && harness.Robots.Agents.ActiveAgents.Count == 0,
                "Zombies must not begin from the early Worlds session event while another scene is active");

            harness.Context.Scenes.Load("ZombiesArena");
            harness.Advance(0.01f);
            harness.Advance(0.01f);
            Assert(harness.Controller.TestingPhase == ZombiesPhase.Wave
                && harness.Controller.TestingWave == 1,
                "Zombies begins only after the current session scene and safe local-player entity are ready");
        }



        private static void RepeatedLifecycleReturnsToLeakBaseline()
        {
            for (var cycle = 0; cycle < 10; cycle++)
            {
                var harness = new Harness(FastConfig(), withChronos: true);
                harness.Advance(0.01f);
                harness.Controller.TestingSetWavePhase();
                Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 4f)),
                    "each lifecycle cycle should own one agent");
                harness.Controller.TestingDamagePlayer(harness.Config.PlayerIntegrity);
                var gameOver = FindSurface(harness.Context, "zombies-game-over");
                Assert(gameOver.ActivateButton("zombies-game-over-return").Succeeded,
                    "each lifecycle cycle should open a retained confirmation modal");
                harness.Context.Ui.Modals[0].Close();
                Assert(harness.Controller.Restart().Succeeded,
                    "each lifecycle cycle should release run state before unload");
                harness.Dispose();
            }
        }
    }
}
