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
        internal static void RunRobotAuthority()
        {
            TestRobotAuthorityScopes();
            TestRobotAuthorityClient();
            TestRobotAuthorityUi();
            Console.WriteLine("Testing kit robot authority passed.");
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
                // Observe without mutating fake collections concurrently with the owner thread.
                Assert(Task.Run(() => robot.SetBrainMode(RobotBrainMode.Autonomous).Succeeded).GetAwaiter().GetResult(),
                    "workers do not observe owner-thread forwarded origin, matching the production scope");
                robot.SetBrainMode(RobotBrainMode.Dormant);
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
