using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        // The focused selector reports this group by name; the full testing-kit run reports one summary line.
        internal static void RunRobotCallOriginTests()
        {
            TestRobotCallOrigin();
            Console.WriteLine("Testing kit robot authority passed.");
        }

        private static void TestRobotCallOrigin()
        {
            TestRobotAuthorityScopes();
            TestRobotAuthorityClient();
            TestRobotAuthorityUi();
            TestRobotCallOriginOwnerThread();
            TestRobotCallOriginScheduler();
        }

        private static RobotAgentSpawnRequest AutonomousRobot() =>
            new RobotAgentSpawnRequest(Vec3.Zero, brainMode: RobotBrainMode.Autonomous);

        private static void TestRobotAuthorityScopes()
        {
            using var context = new FakeModContext();
            var service = new FakeRobotKit(context.Lifetime).Agents;
            var robot = service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            var clean = ExecutionContext.Capture();
            ExecutionContext? captured = null;
            void Restricted()
            {
                var count = service.ActiveAgents.Count;
                var tracked = context.Lifetime.TrackedResourceCount;
                var result = service.Spawn(AutonomousRobot());
                Assert(result.ErrorCode == ModErrorCode.NotAuthoritative && result.Value == null &&
                    service.ActiveAgents.Count == count && context.Lifetime.TrackedResourceCount == tracked,
                    "forwarded Autonomous refusal creates no fake body or lifetime lease");
                Assert(robot.SetBrainMode(RobotBrainMode.Autonomous).ErrorCode == ModErrorCode.NotAuthoritative &&
                    robot.SetInteraction(RobotInteractionOptions.NativeTalk()).ErrorCode == ModErrorCode.NotAuthoritative,
                    "retained host handles check the current callback's origin");
                Assert(robot.BrainMode == RobotBrainMode.Dormant && robot.SetBrainMode(RobotBrainMode.Dormant).Succeeded &&
                    robot.SetInteraction(RobotInteractionOptions.DisableNativeTalk()).Succeeded &&
                    robot.SetInteraction(RobotInteractionOptions.Custom(new RobotCustomInteraction("Inspect"))).Succeeded &&
                    robot.MoveTo(new Vec3(1, 0, 0)).Succeeded,
                    "refusal leaves the brain dormant and non-native controls remain available");
            }

            service.RunAsGuest(() =>
            {
                Restricted();
                captured = ExecutionContext.Capture();
                service.RunAsGuest(Restricted);
                ExecutionContext.Run(clean!, _ => Restricted(), null);
                var defaultSpawn = service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero));
                Assert(defaultSpawn.Succeeded && defaultSpawn.Value!.BrainMode == RobotBrainMode.Dormant,
                    "forwarded default Dormant request still succeeds");
                Assert(service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero,
                    interaction: RobotInteractionOptions.Custom(new RobotCustomInteraction("Inspect")))).Succeeded,
                    "host guest-origin Dormant spawn retains custom interaction support");
                Assert(service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero, brainMode: (RobotBrainMode)2)).ErrorCode ==
                    ModErrorCode.InvalidArgument, "invalid original brain is not normalized into success");
                // Each refusal throws before the fake mutates anything, so the owner thread is never raced.
                var agents = service.ActiveAgents.Count;
                AssertThrows<InvalidOperationException>(() => Task.Run(() =>
                    robot.SetBrainMode(RobotBrainMode.Autonomous)).GetAwaiter().GetResult(),
                    "a worker that inherits guest origin fails instead of waking the robot as the host");
                AssertThrows<InvalidOperationException>(() => Task.Run(() =>
                    service.Spawn(AutonomousRobot())).GetAwaiter().GetResult(),
                    "a worker that inherits guest origin fails instead of spawning as the host");
                Assert(robot.BrainMode == RobotBrainMode.Dormant && service.ActiveAgents.Count == agents,
                    "off-thread guest calls change nothing");
            });

            Assert(captured != null, "forwarded context captured");
            ExecutionContext.Run(captured!, _ => Restricted(), null);
            Assert(robot.SetBrainMode(RobotBrainMode.Autonomous).Succeeded &&
                robot.SetInteraction(RobotInteractionOptions.NativeTalk()).Succeeded,
                "captured same-thread continuation restores trusted caller after returning");
            robot.SetBrainMode(RobotBrainMode.Dormant);
            AssertThrows<ApplicationException>(() => service.RunAsGuest(() =>
            {
                Restricted();
                throw new ApplicationException("callback failure");
            }), "throwing callback propagates its exception");
            Assert(service.Spawn(AutonomousRobot()).Value!.BrainMode == RobotBrainMode.Autonomous,
                "exception cleanup restores trusted host spawning");
            context.Dispose();
            service.RunAsGuest(() => Assert(!robot.SetBrainMode(RobotBrainMode.Autonomous).Succeeded && !robot.IsAlive,
                "guest helper cannot revive an ended robot lifetime"));
        }

        private static void TestRobotCallOriginOwnerThread()
        {
            using var context = new FakeModContext();
            using var players = new FakePlayerDirectory();
            var service = new FakeRobotKit(context.Lifetime).Agents;
            var invoked = false;
            var thrown = Task.Run(() =>
            {
                try { service.RunAsGuest(() => invoked = true); return null; }
                catch (InvalidOperationException exception) { return exception; }
            }).GetAwaiter().GetResult();
            Assert(thrown != null && thrown.Message.Contains("thread that created it", StringComparison.Ordinal) && !invoked,
                "RunAsGuest on another thread names the owning-thread rule and runs nothing");

            var remotePlayer = players.Add(new ParticipantId("guest"), "Guest");
            var remote = (FakeUiSurface)remotePlayer.Ui.CreateSurface(new UiSurfaceRequest("remote", "Robot", "",
                content: new UiColumn(new UiButton("wake", "Wake", () => service.Spawn(AutonomousRobot()))))).Value!;
            var pressed = Task.Run(() => remote.ActivateButton("wake")).GetAwaiter().GetResult();
            Assert(!pressed.Succeeded && service.ActiveAgents.Count == 0 && remote.CallbackErrors.Count == 1 &&
                remote.CallbackErrors[0].Contains("thread that created it", StringComparison.Ordinal),
                "a remote player action on another thread fails its callback instead of spawning as the host");
            Assert(remote.ActivateButton("wake").Succeeded && service.ActiveAgents.Count == 0,
                "the same action on the owning thread is refused as a guest callback");

            var hosted = Task.Run(() => service.Spawn(AutonomousRobot())).GetAwaiter().GetResult();
            Assert(hosted.Succeeded && hosted.Value!.BrainMode == RobotBrainMode.Autonomous,
                "host calls outside any guest scope keep working on other threads");
        }

        private static void TestRobotCallOriginScheduler()
        {
            using var context = new FakeModContext();
            var service = new FakeRobotKit(context.Lifetime).Agents;
            var scheduler = context.Scheduler;
            var interval = TimeSpan.FromSeconds(0.1);
            OperationResult<IRobotAgent>? nextFrame = null, after = null, guestTimer = null, hostTimer = null;
            var hostTicks = 0;
            service.RunAsGuest(() =>
            {
                Assert(scheduler.NextFrame(() => nextFrame = service.Spawn(AutonomousRobot())).Succeeded &&
                    scheduler.After(interval, () => after = service.Spawn(AutonomousRobot())).Succeeded &&
                    scheduler.Every(interval, () => guestTimer ??= service.Spawn(AutonomousRobot())).Succeeded,
                    "a guest callback can schedule work");
            });
            // Registered after the guest's work, so it runs after that work in every frame and would see a leak.
            Assert(scheduler.Every(interval, () => { hostTicks++; hostTimer = service.Spawn(AutonomousRobot()); }).Succeeded,
                "host code registers its own timer");
            scheduler.AdvanceFrame(interval);
            Assert(nextFrame?.ErrorCode == ModErrorCode.NotAuthoritative && after?.ErrorCode == ModErrorCode.NotAuthoritative &&
                guestTimer?.ErrorCode == ModErrorCode.NotAuthoritative,
                "NextFrame, After and Every scheduled from a guest callback keep the guest's origin");
            Assert(hostTicks == 1 && hostTimer?.Succeeded == true && hostTimer.Value!.BrainMode == RobotBrainMode.Autonomous,
                "a timer registered outside every guest callback runs as the host after guest-scheduled work");
            scheduler.AdvanceFrame(interval);
            Assert(hostTicks == 2 && hostTimer?.Succeeded == true && service.Spawn(AutonomousRobot()).Succeeded,
                "guest-scheduled work leaves no guest scope behind");

            Exception? continuation = null;
            Task? pending = null;
            service.RunAsGuest(() => pending = Resume());
            async Task Resume()
            {
                await scheduler.DelayAsync(TimeSpan.Zero);
                try { service.Spawn(AutonomousRobot()); }
                catch (InvalidOperationException exception) { continuation = exception; }
            }
            scheduler.AdvanceFrame(TimeSpan.Zero);
            pending!.GetAwaiter().GetResult();
            Assert(continuation != null && continuation.Message.Contains("Scheduler.NextFrame", StringComparison.Ordinal),
                "a guest continuation that resumes on a pool thread names the scheduler remedy");
        }

        private static void TestRobotAuthorityClient()
        {
            using var context = new FakeModContext();
            var service = new FakeRobotKit(context.Lifetime).Agents;
            service.IsClient = true;
            var result = service.Spawn(AutonomousRobot());
            Assert(result.Succeeded && result.Value!.BrainMode == RobotBrainMode.Dormant,
                "client spawn retains defensive Dormant normalization");
            var robot = result.Value!;
            Assert(robot.SetBrainMode(RobotBrainMode.Autonomous).ErrorCode == ModErrorCode.NotAuthoritative &&
                robot.SetInteraction(RobotInteractionOptions.NativeTalk()).ErrorCode == ModErrorCode.NotAuthoritative &&
                robot.SetBrainMode(RobotBrainMode.Dormant).Succeeded,
                "client cannot wake its replica but can request Dormant");
            // Inspect the fake's stored policy without adding a public API just for this regression.
            var interactionField = typeof(FakeRobotAgent).GetField("interaction",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var originalInteraction = (RobotInteractionOptions)interactionField.GetValue(robot)!;
            var custom = RobotInteractionOptions.Custom(new RobotCustomInteraction("Inspect", _ =>
                throw new InvalidOperationException("Refused custom callback must not be installed.")));
            Assert(robot.SetInteraction(custom).ErrorCode == ModErrorCode.NotAuthoritative &&
                ReferenceEquals(originalInteraction, interactionField.GetValue(robot)) &&
                originalInteraction.CustomInteraction == null,
                "retained client handle refuses custom interaction without installing its callback");
            Assert(robot.SetInteraction(RobotInteractionOptions.DisableNativeTalk()).Succeeded,
                "client can still apply an ordinary disabled native-talk policy");
            AssertThrows<ArgumentNullException>(() => robot.SetInteraction(null!),
                "client custom-interaction guard preserves null validation");
            Assert(service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero, interaction: custom)).ErrorCode ==
                ModErrorCode.NotAuthoritative, "clients cannot spawn custom host interactions");
            Assert(service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero, brainMode: (RobotBrainMode)2)).ErrorCode ==
                ModErrorCode.InvalidArgument, "client original brain validates before normalization");
            robot.Dispose();
            Assert(robot.SetInteraction(custom).ErrorCode == ModErrorCode.InvalidState,
                "ended fake agent lifetime takes precedence over custom-interaction authority");
        }

        private static void TestRobotAuthorityUi()
        {
            using var context = new FakeModContext();
            using var players = new FakePlayerDirectory();
            var service = new FakeRobotKit(context.Lifetime).Agents;
            var robot = service.Spawn(new RobotAgentSpawnRequest(Vec3.Zero)).Value!;
            var observed = new List<ModErrorCode>();
            void Observe()
            {
                var count = service.ActiveAgents.Count;
                var spawn = service.Spawn(AutonomousRobot());
                var brain = robot.SetBrainMode(RobotBrainMode.Autonomous);
                var talk = robot.SetInteraction(RobotInteractionOptions.NativeTalk());
                Assert(spawn.ErrorCode == brain.ErrorCode && spawn.ErrorCode == talk.ErrorCode &&
                    service.ActiveAgents.Count == count + (spawn.Succeeded ? 1 : 0),
                    "fake callback spawning and retained controls agree before any body is admitted");
                observed.Add(spawn.ErrorCode);
            }

            var local = (FakeUiSurface)players.Local!.Ui.CreateSurface(new UiSurfaceRequest("local", "Robot", "",
                content: new UiColumn(new UiButton("wake", "Wake", Observe)))).Value!;
            var remotePlayer = players.Add(new ParticipantId("guest"), "Guest");
            var remote = (FakeUiSurface)remotePlayer.Ui.CreateSurface(new UiSurfaceRequest("remote", "Robot", "",
                content: new UiColumn(new UiButton("wake", "Wake", () =>
                {
                    Observe();
                    local.ActivateButton("wake");
                }), new UiToggle("enabled", "Enabled", false, _ => Observe())))).Value!;
            remote.Dismissed += Observe;

            Assert(remote.ActivateButton("wake").Succeeded && observed.Count == 2 &&
                observed.TrueForAll(code => code == ModErrorCode.NotAuthoritative),
                "remote activation and a nested local callback both retain guest origin automatically");
            observed.Clear();
            Assert(remote.ChangeToggle("enabled", true).Succeeded && observed[0] == ModErrorCode.NotAuthoritative,
                "value callbacks also carry remote origin");
            observed.Clear();
            remote.DismissFromPlayer();
            Assert(observed.Count == 1 && observed[0] == ModErrorCode.NotAuthoritative,
                "remote player dismissal carries guest origin");
            observed.Clear();
            remote.Show();
            remote.Hide();
            local.ActivateButton("wake");
            Assert(observed.Count == 2 && observed.TrueForAll(code => code == ModErrorCode.None),
                "trusted server Hide and local player activation retain host authority");
            observed.Clear();
            local.Dismissed += Observe;
            local.DismissFromPlayer();
            Assert(observed.Count == 1 && observed[0] == ModErrorCode.None,
                "local player dismissal remains trusted");
            remote.Dispose();
            observed.Clear();
            Assert(!remote.ActivateButton("wake").Succeeded && observed.Count == 0,
                "retired remote surface cannot invoke a callback");
            AssertThrows<ObjectDisposedException>(() => remote.DismissFromPlayer(),
                "retired dismissal keeps the surface lifetime contract");
            Assert(service.Spawn(AutonomousRobot()).Succeeded, "failed dismissal restores caller authority");
        }
    }
}
