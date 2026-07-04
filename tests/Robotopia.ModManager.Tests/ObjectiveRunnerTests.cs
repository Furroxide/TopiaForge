using System;
using System.Collections.Generic;
using Robotopia.Mods;
using Robotopia.RobotKit;

namespace Robotopia.ModManager.Tests
{
    // Unit tests for the Unity-free objective layer: the per-robot state machine (ObjectiveRunner) and the service
    // that owns runners + the named-target registry (RobotObjectiveService), driven against a fake robot agent and a
    // fake clock. No UnityEngine — these compile straight into the net8.0 test assembly via the csproj Compile
    // includes, exactly like the conversation tests.
    internal static class ObjectiveRunnerTests
    {
        public static void Run()
        {
            TestIdleStopsOnce();
            TestGoToPointArrives();
            TestGoToNamedTargetReissuesWhenItMoves();
            TestMissingTargetParksAndRetries();
            TestFollowLiveObjectChasesOnce();
            TestFollowPositionOnlyReissuesMoves();
            TestPatrolAdvancesDwellsAndLoops();
            TestPatrolToMaterialisesRouteFromStart();
            TestSetObjectiveReplacesCleanly();
            TestDeadAgentRunnerIsDropped();
            TestSceneChangeClearsEverything();
            TestTargetNamesAreNormalisedAndSorted();
            TestTargetKindsCarryThroughMetadata();
            Console.WriteLine("All objective runner tests passed.");
        }

        private static void TestIdleStopsOnce()
        {
            var (service, clock) = NewService();
            var agent = new FakeRobotAgent();

            var handle = service.SetObjective(agent, RobotObjective.Idle());
            service.Tick(0.016f);
            service.Tick(0.016f);

            Assert(handle.State == RobotObjectiveState.Idle, "an idle objective reports Idle");
            Assert(agent.StopCalls == 1, "idle stops the agent exactly once");
            Assert(agent.MoveToCalls.Count == 0 && agent.ChaseCalls.Count == 0, "idle never moves");
            _ = clock;
        }

        private static void TestGoToPointArrives()
        {
            var (service, _) = NewService();
            var agent = new FakeRobotAgent();

            var goal = new Vec3(10f, 0f, 0f);
            var handle = service.SetObjective(agent, RobotObjective.GoTo(goal));
            service.Tick(0.016f);

            Assert(handle.State == RobotObjectiveState.Seeking, "a fresh go-to seeks");
            Assert(agent.MoveToCalls.Count == 1 && agent.MoveToCalls[0].X == 10f, "go-to issues one walk to the point");
            Assert(Math.Abs(agent.StopDistance - handle.Objective.ArriveDistance) < 1e-6f, "arrive distance applies as stop distance");

            agent.HasReachedTarget = true;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Arrived, "reaching the point latches Arrived");
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 1, "an arrived go-to does not re-walk to a fixed point");
        }

        private static void TestGoToNamedTargetReissuesWhenItMoves()
        {
            var (service, clock) = NewService();
            var agent = new FakeRobotAgent();
            var targetPosition = new Vec3(10f, 0f, 0f);
            service.RegisterTarget("CRATE", () => new RobotTargetSnapshot(targetPosition));

            var handle = service.SetObjective(agent, RobotObjective.GoTo("CRATE"));
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 1, "named go-to walks to the resolved position");

            agent.HasReachedTarget = true;
            clock.Now += 1.5f; // past the 1s re-resolve window
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Arrived, "reaching the target latches Arrived");

            // The crate is carried far away — the robot re-walks to it.
            targetPosition = new Vec3(30f, 0f, 0f);
            agent.HasReachedTarget = false;
            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count >= 2, "a moved named target re-issues the walk");
            Assert(agent.MoveToCalls[agent.MoveToCalls.Count - 1].X == 30f, "the re-issued walk goes to the new position");
        }

        private static void TestMissingTargetParksAndRetries()
        {
            var (service, clock) = NewService();
            var agent = new FakeRobotAgent();
            RobotTargetSnapshot? snapshot = null;
            service.RegisterTarget("CRATE", () => snapshot);

            var handle = service.SetObjective(agent, RobotObjective.GoTo("CRATE"));
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.TargetMissing, "an unresolvable target parks the objective");
            Assert(agent.StopCalls == 1, "a missing target stops the robot");
            Assert(agent.MoveToCalls.Count == 0, "no walk is issued while the target is missing");

            // The target comes back after the retry window — the objective resumes on its own.
            snapshot = new RobotTargetSnapshot(new Vec3(5f, 0f, 0f));
            clock.Now += 2.5f; // past the 2s missing-retry window
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Seeking, "a recovered target resumes seeking");
            Assert(agent.MoveToCalls.Count == 1, "the walk is issued once the target resolves");
        }

        private static void TestFollowLiveObjectChasesOnce()
        {
            var (service, clock) = NewService();
            var agent = new FakeRobotAgent();
            var player = new object();
            service.RegisterTarget("PLAYER", () => new RobotTargetSnapshot(new Vec3(3f, 0f, 0f), player));

            var handle = service.SetObjective(agent, RobotObjective.Follow("PLAYER"));
            service.Tick(0.016f);
            clock.Now += 1.5f;
            service.Tick(0.016f);
            clock.Now += 1.5f;
            service.Tick(0.016f);

            Assert(agent.ChaseCalls.Count == 1 && ReferenceEquals(agent.ChaseCalls[0], player),
                "a live target is chased natively with exactly one Chase call");
            Assert(agent.MoveToCalls.Count == 0, "a live target never falls back to point walks");
            Assert(handle.State == RobotObjectiveState.Seeking, "an out-of-range follower seeks");

            agent.HasReachedTarget = true;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Arrived, "an in-range follower reads Arrived but keeps following");
        }

        private static void TestFollowPositionOnlyReissuesMoves()
        {
            var (service, clock) = NewService();
            var agent = new FakeRobotAgent();
            var position = new Vec3(5f, 0f, 0f);
            service.RegisterTarget("BEACON", () => new RobotTargetSnapshot(position));

            var handle = service.SetObjective(agent, RobotObjective.Follow("BEACON"));
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 1, "a position-only follow walks to the position");

            agent.HasReachedTarget = true;
            position = new Vec3(25f, 0f, 0f);
            agent.HasReachedTarget = false;
            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count >= 2 && agent.MoveToCalls[agent.MoveToCalls.Count - 1].X == 25f,
                "a moved position-only target re-issues the walk");
            Assert(handle.State == RobotObjectiveState.Seeking, "the follower keeps seeking the moved target");
        }

        private static void TestPatrolAdvancesDwellsAndLoops()
        {
            var (service, clock) = NewService();
            var agent = new FakeRobotAgent();
            var a = new Vec3(0f, 0f, 0f);
            var b = new Vec3(10f, 0f, 0f);

            var handle = service.SetObjective(agent, RobotObjective.Patrol(new[] { a, b }));
            service.Tick(0.016f);
            Assert(handle.WaypointIndex == 0 && agent.MoveToCalls.Count == 1, "patrol walks to the first waypoint");

            agent.HasReachedTarget = true;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Dwelling, "reaching a waypoint dwells");

            // Still dwelling before the dwell time lapses.
            clock.Now += 0.5f;
            service.Tick(0.016f);
            Assert(handle.WaypointIndex == 0, "the patrol holds through the dwell");

            clock.Now += 1.0f; // past DwellSeconds (default 1.0)
            agent.HasReachedTarget = false;
            service.Tick(0.016f);
            Assert(handle.WaypointIndex == 1, "the dwell lapsing advances to the next waypoint");
            Assert(agent.MoveToCalls.Count == 2 && agent.MoveToCalls[1].X == 10f, "the patrol walks to the second waypoint");

            // Reaching the last waypoint loops back to the first.
            agent.HasReachedTarget = true;
            service.Tick(0.016f);
            clock.Now += 1.5f;
            agent.HasReachedTarget = false;
            service.Tick(0.016f);
            Assert(handle.WaypointIndex == 0, "the patrol loops back to the first waypoint");
            Assert(agent.MoveToCalls.Count == 3, "the loop issues the next walk");
        }

        private static void TestPatrolToMaterialisesRouteFromStart()
        {
            var (service, _) = NewService();
            var agent = new FakeRobotAgent { Position = new Vec3(2f, 0f, 2f) };
            service.RegisterTarget("RED MARKER", () => new RobotTargetSnapshot(new Vec3(20f, 0f, 2f)));

            var handle = service.SetObjective(agent, RobotObjective.PatrolTo("RED MARKER"));
            service.Tick(0.016f);

            Assert(handle.State == RobotObjectiveState.Seeking, "the patrol starts seeking");
            Assert(agent.MoveToCalls.Count == 1 && agent.MoveToCalls[0].X == 2f,
                "the here<->target route starts at the robot's own position");
        }

        private static void TestSetObjectiveReplacesCleanly()
        {
            var (service, _) = NewService();
            var agent = new FakeRobotAgent();

            var first = service.SetObjective(agent, RobotObjective.GoTo(new Vec3(10f, 0f, 0f)));
            service.Tick(0.016f);
            var second = service.SetObjective(agent, RobotObjective.Idle());
            service.Tick(0.016f);

            Assert(first.State == RobotObjectiveState.Cancelled, "the replaced objective is cancelled");
            Assert(second.State == RobotObjectiveState.Idle, "the replacement runs");
            Assert(ReferenceEquals(service.GetObjective(agent), second), "GetObjective returns the live handle");

            service.ClearObjective(agent);
            Assert(second.State == RobotObjectiveState.Cancelled, "clearing cancels the objective");
            Assert(service.GetObjective(agent) == null, "a cleared agent has no objective");
        }

        private static void TestDeadAgentRunnerIsDropped()
        {
            var (service, _) = NewService();
            var agent = new FakeRobotAgent();
            service.SetObjective(agent, RobotObjective.GoTo(new Vec3(10f, 0f, 0f)));
            service.Tick(0.016f);

            agent.IsAlive = false;
            service.Tick(0.016f);
            Assert(service.GetObjective(agent) == null, "a dead agent's runner is dropped on the next tick");
        }

        private static void TestSceneChangeClearsEverything()
        {
            var (service, _) = NewService();
            var agent = new FakeRobotAgent();
            service.RegisterTarget("CRATE", () => new RobotTargetSnapshot(default));
            var handle = service.SetObjective(agent, RobotObjective.GoTo("CRATE"));

            service.OnSceneChanged();
            Assert(handle.State == RobotObjectiveState.Cancelled, "a scene change cancels objectives");
            Assert(service.GetObjective(agent) == null, "a scene change drops runners");
            Assert(service.TargetNames.Count == 0, "a scene change clears the target vocabulary");
        }

        private static void TestTargetNamesAreNormalisedAndSorted()
        {
            var (service, _) = NewService();
            service.RegisterTarget("  red marker ", () => new RobotTargetSnapshot(default));
            service.RegisterTarget("Player", () => new RobotTargetSnapshot(default));

            Assert(service.TargetNames.Count == 2, "both targets register");
            Assert(service.TargetNames[0] == "PLAYER" && service.TargetNames[1] == "RED MARKER",
                "names are upper-cased, trimmed, and sorted");
            Assert(service.TryResolveTarget("red MARKER", out _), "resolution is case-insensitive");

            service.UnregisterTarget("PLAYER");
            Assert(service.TargetNames.Count == 1, "unregistering removes the target");
            Assert(!service.TryResolveTarget("PLAYER", out _), "an unregistered target no longer resolves");
        }

        private static void TestTargetKindsCarryThroughMetadata()
        {
            var (service, _) = NewService();
            service.RegisterTarget("Player", RobotTargetKind.Player, () => new RobotTargetSnapshot(default));
            service.RegisterTarget("robot 2", RobotTargetKind.Robot, () => new RobotTargetSnapshot(default));
            service.RegisterTarget("CRATE", () => new RobotTargetSnapshot(default)); // legacy overload

            Assert(service.Targets.Count == 3, "every registration appears in Targets");
            Assert(service.Targets[0].Name == "CRATE" && service.Targets[1].Name == "PLAYER"
                && service.Targets[2].Name == "ROBOT 2", "Targets is sorted like TargetNames");

            Assert(service.TryGetTargetInfo("player", out var player) && player.Kind == RobotTargetKind.Player,
                "kind metadata resolves case-insensitively");
            Assert(service.TryGetTargetInfo("ROBOT 2", out var robot) && robot.Kind == RobotTargetKind.Robot,
                "the kinded overload stores its kind");
            Assert(service.TryGetTargetInfo("crate", out var crate) && crate.Kind == RobotTargetKind.Custom,
                "the legacy overload registers as Custom");

            service.OnSceneChanged();
            Assert(service.Targets.Count == 0, "a scene change clears target metadata");
            Assert(!service.TryGetTargetInfo("PLAYER", out _), "cleared metadata no longer resolves");
        }

        private static (RobotObjectiveService Service, FakeClock Clock) NewService()
        {
            var clock = new FakeClock();
            return (new RobotObjectiveService(new NullLogger(), () => clock.Now), clock);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class FakeClock
        {
            public float Now;
        }

        // Records the movement intents the runner issues; reached/alive state is scripted by each test.
        private sealed class FakeRobotAgent : IRobotAgent
        {
            public List<Vec3> MoveToCalls { get; } = new List<Vec3>();
            public List<object> ChaseCalls { get; } = new List<object>();
            public int StopCalls { get; private set; }

            public string Id { get; } = Guid.NewGuid().ToString("N");
            public object GameObject { get; } = new object();
            public bool IsAlive { get; set; } = true;
            public Vec3 Position { get; set; }
            public Vec3 HeadPosition => Position;
            public RobotBrainMode BrainMode { get; private set; } = RobotBrainMode.Dormant;
            public bool IsMoving => false;
            public bool HasReachedTarget { get; set; }
            public float MoveSpeed { get; set; }
            public float TurnSpeed { get; set; }
            public float StopDistance { get; set; }
            public RobotGait Gait { get; set; }

            public void MoveTo(Vec3 position)
            {
                MoveToCalls.Add(position);
            }

            public void Chase(object targetGameObject)
            {
                ChaseCalls.Add(targetGameObject);
            }

            public void Stop()
            {
                StopCalls++;
            }

            public void SetBrainMode(RobotBrainMode mode)
            {
                BrainMode = mode;
            }

            public void SetTint(RobotColor color) { }

            public void SetEmote(string emojiShortcode) { }

            public void SetName(string name) { }

            public void SetScale(float scale) { }

            public void SetInteraction(RobotInteractionOptions options) { }

            public bool ApplyDamage(float amount, RobotDamageType type, string source) => false;

            public void Kill(RobotDamageType type, string source) { }

            public void Ragdoll() { }

            public void Knockback(Vec3 impulse) { }

            public void Despawn()
            {
                IsAlive = false;
            }
        }

        private sealed class NullLogger : IModLogger
        {
            public void Debug(string message) { }

            public void Info(string message) { }

            public void Warn(string message) { }

            public void Error(string message) { }

            public void Error(Exception exception, string message) { }
        }
    }
}
