using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
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
            TestNullAgentReturnsCancelledHandle();
            TestInvalidAgentIdentityReturnsCancelledHandle();
            TestFaultyAgentDoesNotStarveOtherObjectives();
            TestCancelIsSafeWhenAgentCleanupThrows();
            TestDeadAgentRunnerIsDropped();
            TestSceneChangeClearsEverything();
            TestTargetNamesAreNormalisedAndSorted();
            TestTargetKindsCarryThroughMetadata();
            TestWanderPicksLegAndDwells();
            TestWanderAnchorsToHomeNotCurrentPosition();
            TestWanderNamedHomeTracksTarget();
            TestWanderLegTimeoutRepicks();
            TestFleeMovesDirectlyAway();
            TestFleeArrivesWhenSafe();
            TestFleeReEvaluatesAsThreatMoves();
            TestFleeMissingThreatParksAndRecovers();
            TestFleeOnTopOfThreatUsesRandomDirection();
            TestReprogramDeliversPayloadAndRaisesEvent();
            TestReprogramTargetMissingMidJourney();
            TestReprogramUnmappableTargetStaysArrived();
            TestReprogramReplacesRecipientsExistingObjective();
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
            service.RegisterTarget("CRATE", RobotTargetKind.Prop, () => new RobotTargetSnapshot(targetPosition));

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
            service.RegisterTarget("CRATE", RobotTargetKind.Prop, () => snapshot);

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
            var player = new FakeEntity("player");
            service.RegisterTarget("PLAYER", RobotTargetKind.Player,
                () => new RobotTargetSnapshot(new Vec3(3f, 0f, 0f), player));

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
            service.RegisterTarget("BEACON", RobotTargetKind.Marker, () => new RobotTargetSnapshot(position));

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
            service.RegisterTarget("RED MARKER", RobotTargetKind.Marker,
                () => new RobotTargetSnapshot(new Vec3(20f, 0f, 2f)));

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

        private static void TestNullAgentReturnsCancelledHandle()
        {
            var (service, _) = NewService();
            var objective = RobotObjective.GoTo(new Vec3(1f, 0f, 0f));
            var handle = service.SetObjective(null!, objective);

            Assert(handle.State == RobotObjectiveState.Cancelled, "a null agent should return an inert cancelled handle");
            Assert(ReferenceEquals(handle.Objective, objective), "the inert handle should preserve the requested objective");
            handle.Dispose();
        }

        private static void TestInvalidAgentIdentityReturnsCancelledHandle()
        {
            var (service, _) = NewService();
            var agent = new FakeRobotAgent { ThrowOnId = true };

            var handle = service.SetObjective(agent, RobotObjective.Idle());

            Assert(handle.State == RobotObjectiveState.Cancelled,
                "an agent whose identity getter fails returns an inert cancelled handle");
        }

        private static void TestFaultyAgentDoesNotStarveOtherObjectives()
        {
            var (service, _) = NewService();
            var faulty = new FakeRobotAgent { ThrowOnMoveTo = true };
            var healthy = new FakeRobotAgent();
            var failed = service.SetObjective(faulty, RobotObjective.GoTo(new Vec3(1f, 0f, 0f)));
            service.SetObjective(healthy, RobotObjective.GoTo(new Vec3(2f, 0f, 0f)));

            service.Tick(0.016f);

            Assert(failed.State == RobotObjectiveState.Cancelled,
                "an objective whose agent throws is cancelled and isolated");
            Assert(healthy.MoveToCalls.Count == 1,
                "a faulty agent objective must not starve later objectives in the same tick");
        }

        private static void TestCancelIsSafeWhenAgentCleanupThrows()
        {
            var (service, _) = NewService();
            var agent = new FakeRobotAgent();
            var handle = service.SetObjective(agent, RobotObjective.Idle());
            agent.ThrowOnIsAlive = true;

            handle.Dispose();

            Assert(handle.State == RobotObjectiveState.Cancelled,
                "objective cancellation remains terminal when agent cleanup throws");
        }

        private static void TestSceneChangeClearsEverything()
        {
            var (service, _) = NewService();
            var agent = new FakeRobotAgent();
            service.RegisterTarget("CRATE", RobotTargetKind.Prop, () => new RobotTargetSnapshot(default));
            var handle = service.SetObjective(agent, RobotObjective.GoTo("CRATE"));

            service.OnSceneChanged();
            Assert(handle.State == RobotObjectiveState.Cancelled, "a scene change cancels objectives");
            Assert(service.GetObjective(agent) == null, "a scene change drops runners");
            Assert(service.TargetNames.Count == 0, "a scene change clears the target vocabulary");
        }

        private static void TestTargetNamesAreNormalisedAndSorted()
        {
            var (service, _) = NewService();
            service.RegisterTarget("  red marker ", RobotTargetKind.Marker, () => new RobotTargetSnapshot(default));
            service.RegisterTarget("Player", RobotTargetKind.Player, () => new RobotTargetSnapshot(default));

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
            service.RegisterTarget("CRATE", RobotTargetKind.Prop, () => new RobotTargetSnapshot(default));

            Assert(service.Targets.Count == 3, "every registration appears in Targets");
            Assert(service.Targets[0].Name == "CRATE" && service.Targets[1].Name == "PLAYER"
                && service.Targets[2].Name == "ROBOT 2", "Targets is sorted like TargetNames");

            Assert(service.TryGetTargetInfo("player", out var player) && player.Kind == RobotTargetKind.Player,
                "kind metadata resolves case-insensitively");
            Assert(service.TryGetTargetInfo("ROBOT 2", out var robot) && robot.Kind == RobotTargetKind.Robot,
                "the kinded overload stores its kind");
            Assert(service.TryGetTargetInfo("crate", out var crate) && crate.Kind == RobotTargetKind.Prop,
                "the target registry preserves prop metadata");

            service.OnSceneChanged();
            Assert(service.Targets.Count == 0, "a scene change clears target metadata");
            Assert(!service.TryGetTargetInfo("PLAYER", out _), "cleared metadata no longer resolves");
        }

        // --- Wander -------------------------------------------------------------------------------------------

        private static void TestWanderPicksLegAndDwells()
        {
            var (service, clock, random) = NewRunnerService();
            var agent = new FakeRobotAgent();

            // angle = 0.25 * 2pi (due +X), distance = 8 * (0.35 + 0.65 * 0.5) = 5.4.
            random.Enqueue(0.25f, 0.5f);
            var handle = service.SetObjective(agent, RobotObjective.Wander());
            service.Tick(0.016f);

            Assert(handle.State == RobotObjectiveState.Seeking, "a fresh wander seeks its first leg");
            Assert(agent.MoveToCalls.Count == 1, "wander issues one leg walk");
            Assert(Math.Abs(agent.MoveToCalls[0].X - 5.4f) < 1e-3f && Math.Abs(agent.MoveToCalls[0].Z) < 1e-3f,
                "the leg lands where the scripted randoms point");
            Assert(PlanarDistance(agent.MoveToCalls[0], default) <= handle.Objective.WanderRadius + 1e-3f,
                "the leg stays within the wander radius of home");

            agent.HasReachedTarget = true;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Dwelling, "reaching a leg dwells");

            clock.Now += 1.5f; // past DwellSeconds (default 1.0)
            agent.HasReachedTarget = false;
            random.Enqueue(0.75f, 0.5f); // due -X this time
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 2, "the dwell lapsing picks a fresh leg");
            Assert(Math.Abs(agent.MoveToCalls[1].X + 5.4f) < 1e-3f, "the fresh leg uses the next scripted direction");
            Assert(handle.State == RobotObjectiveState.Seeking, "the wanderer seeks again");
        }

        private static void TestWanderAnchorsToHomeNotCurrentPosition()
        {
            var (service, clock, random) = NewRunnerService();
            var agent = new FakeRobotAgent();

            random.Enqueue(0.25f, 0.5f);
            service.SetObjective(agent, RobotObjective.Wander());
            service.Tick(0.016f);

            // The robot drifts far off (carried, knocked back) — the next leg still orbits the ORIGINAL home.
            agent.Position = new Vec3(100f, 0f, 100f);
            agent.HasReachedTarget = true;
            service.Tick(0.016f);
            clock.Now += 1.5f;
            agent.HasReachedTarget = false;
            random.Enqueue(0.25f, 0.5f);
            service.Tick(0.016f);

            Assert(agent.MoveToCalls.Count == 2, "the wanderer keeps picking legs");
            Assert(Math.Abs(agent.MoveToCalls[1].X - 5.4f) < 1e-3f && Math.Abs(agent.MoveToCalls[1].Z) < 1e-3f,
                "legs anchor to the set-time home, not wherever the robot ended up");
        }

        private static void TestWanderNamedHomeTracksTarget()
        {
            var (service, clock, random) = NewRunnerService();
            var agent = new FakeRobotAgent();
            RobotTargetSnapshot? pad = new RobotTargetSnapshot(new Vec3(20f, 0f, 0f));
            service.RegisterTarget("PAD", RobotTargetKind.Marker, () => pad);

            random.Enqueue(0.25f, 0.5f);
            var handle = service.SetObjective(agent, RobotObjective.Wander("PAD"));
            service.Tick(0.016f);
            Assert(Math.Abs(agent.MoveToCalls[0].X - 25.4f) < 1e-3f, "legs orbit the named home");

            // The pad moves; past the re-resolve window the next leg orbits its new spot.
            pad = new RobotTargetSnapshot(new Vec3(40f, 0f, 0f));
            agent.HasReachedTarget = true;
            service.Tick(0.016f);
            clock.Now += 1.5f;
            agent.HasReachedTarget = false;
            random.Enqueue(0.25f, 0.5f);
            service.Tick(0.016f);
            Assert(Math.Abs(agent.MoveToCalls[1].X - 45.4f) < 1e-3f, "a moved named home drifts the orbit with it");

            // The pad disappears — the wanderer parks; it comes back — the wanderer resumes.
            pad = null;
            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.TargetMissing, "a missing named home parks the wanderer");
            Assert(agent.StopCalls >= 1, "parking stops the robot");

            pad = new RobotTargetSnapshot(new Vec3(40f, 0f, 0f));
            clock.Now += 2.5f;
            random.Enqueue(0.25f, 0.5f);
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Seeking, "a recovered home resumes wandering");
            Assert(agent.MoveToCalls.Count == 3, "the resume picks a fresh leg");
        }

        private static void TestWanderLegTimeoutRepicks()
        {
            var (service, clock, random) = NewRunnerService();
            var agent = new FakeRobotAgent();

            random.Enqueue(0.25f, 0.5f);
            service.SetObjective(agent, RobotObjective.Wander());
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 1, "the first leg is issued");

            // The pick was unreachable (never arrives). Before the timeout: nothing changes.
            clock.Now += 5f;
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 1, "a running leg is left to run before the timeout");

            // Past the 12s leg timeout the pick is written off and a fresh one chosen.
            clock.Now += 8f;
            service.Tick(0.016f); // marks the leg stale
            random.Enqueue(0.75f, 0.5f);
            service.Tick(0.016f); // picks the replacement
            Assert(agent.MoveToCalls.Count == 2, "a timed-out leg is re-picked");
            Assert(Math.Abs(agent.MoveToCalls[1].X + 5.4f) < 1e-3f, "the replacement uses fresh randomness");
        }

        // --- Flee ---------------------------------------------------------------------------------------------

        private static void TestFleeMovesDirectlyAway()
        {
            var (service, _, _) = NewRunnerService();
            var agent = new FakeRobotAgent { Position = new Vec3(3f, 0f, 0f) };
            service.RegisterTarget("THREAT", RobotTargetKind.Custom, () => new RobotTargetSnapshot(default));

            var handle = service.SetObjective(agent, RobotObjective.Flee("THREAT"));
            service.Tick(0.016f);

            // Distance 3 <= FleeDistance 8: hop max(4, 8*0.5) = 4 m straight away along +X.
            Assert(handle.State == RobotObjectiveState.Seeking, "a threatened flee seeks");
            Assert(agent.MoveToCalls.Count == 1, "the flee issues one hop");
            Assert(Math.Abs(agent.MoveToCalls[0].X - 7f) < 1e-3f && Math.Abs(agent.MoveToCalls[0].Z) < 1e-3f,
                "the hop points directly away from the threat");
        }

        private static void TestFleeArrivesWhenSafe()
        {
            var (service, clock, _) = NewRunnerService();
            var agent = new FakeRobotAgent { Position = new Vec3(3f, 0f, 0f) };
            service.RegisterTarget("THREAT", RobotTargetKind.Custom, () => new RobotTargetSnapshot(default));

            var handle = service.SetObjective(agent, RobotObjective.Flee("THREAT"));
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 1, "the threatened robot hops away");

            // Beyond FleeDistance + slack: safe. The hop is dropped mid-stride and the robot stands watchful.
            agent.Position = new Vec3(11f, 0f, 0f);
            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Arrived, "a safe distance reads Arrived");
            Assert(agent.StopCalls == 1, "reaching safety stops the current hop");

            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 1 && agent.StopCalls == 1,
                "a safe robot issues nothing new while the threat keeps its distance");
        }

        private static void TestFleeReEvaluatesAsThreatMoves()
        {
            var (service, clock, _) = NewRunnerService();
            var agent = new FakeRobotAgent();
            var threat = new Vec3(5f, 0f, 0f);
            service.RegisterTarget("THREAT", RobotTargetKind.Custom, () => new RobotTargetSnapshot(threat));

            service.SetObjective(agent, RobotObjective.Flee("THREAT"));
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 1 && agent.MoveToCalls[0].X < 0f,
                "the first hop runs away from the east-side threat");

            // The threat circles round to the west — the next evaluation re-aims from live positions.
            threat = new Vec3(-5f, 0f, 0f);
            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(agent.MoveToCalls.Count == 2 && agent.MoveToCalls[1].X > 0f,
                "each evaluation re-aims the hop away from the threat's new position");
        }

        private static void TestFleeMissingThreatParksAndRecovers()
        {
            var (service, clock, _) = NewRunnerService();
            var agent = new FakeRobotAgent();
            RobotTargetSnapshot? threat = null;
            service.RegisterTarget("THREAT", RobotTargetKind.Custom, () => threat);

            var handle = service.SetObjective(agent, RobotObjective.Flee("THREAT"));
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.TargetMissing, "a missing threat parks the flee");
            Assert(agent.StopCalls == 1 && agent.MoveToCalls.Count == 0, "the robot stands down with nothing to flee");

            threat = new RobotTargetSnapshot(new Vec3(2f, 0f, 0f));
            clock.Now += 2.5f;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Seeking, "a reappeared threat resumes the flee");
            Assert(agent.MoveToCalls.Count == 1, "the resume hops away");
        }

        private static void TestFleeOnTopOfThreatUsesRandomDirection()
        {
            var (service, _, random) = NewRunnerService();
            var agent = new FakeRobotAgent();
            service.RegisterTarget("THREAT", RobotTargetKind.Custom, () => new RobotTargetSnapshot(default));

            random.Enqueue(0.25f); // escape angle: due +X
            service.SetObjective(agent, RobotObjective.Flee("THREAT"));
            service.Tick(0.016f);

            Assert(agent.MoveToCalls.Count == 1, "standing on the threat still hops");
            Assert(Math.Abs(agent.MoveToCalls[0].X - 4f) < 1e-3f && Math.Abs(agent.MoveToCalls[0].Z) < 1e-3f,
                "the degenerate zero-distance case escapes along the scripted random direction");
        }

        // --- Reprogram (courier) ------------------------------------------------------------------------------

        private static void TestReprogramDeliversPayloadAndRaisesEvent()
        {
            var recipient = new FakeRobotAgent { Position = new Vec3(10f, 0f, 0f) };
            var (service, clock, _) = NewRunnerService(entity => ReferenceEquals(entity, recipient) ? recipient : null);
            var messenger = new FakeRobotAgent();
            var player = new FakeEntity("player");
            service.RegisterTarget("ROBOT 2", RobotTargetKind.Robot,
                () => new RobotTargetSnapshot(recipient.Position, recipient));
            service.RegisterTarget("PLAYER", RobotTargetKind.Player,
                () => new RobotTargetSnapshot(new Vec3(5f, 0f, 0f), player));

            var deliveries = new List<RobotProgramDelivery>();
            service.ProgramDelivered += delivery => deliveries.Add(delivery);

            var payload = RobotObjective.Follow("PLAYER");
            var handle = service.SetObjective(messenger, RobotObjective.Reprogram("ROBOT 2", payload));
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Seeking, "the courier sets out");
            Assert(messenger.MoveToCalls.Count == 1 && messenger.MoveToCalls[0].X == 10f,
                "the courier walks to the recipient");

            messenger.HasReachedTarget = true;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Delivered, "arrival hands the payload over");
            Assert(messenger.StopCalls == 1, "the courier stops on delivery");
            Assert(deliveries.Count == 1, "the delivery event fires exactly once");
            Assert(ReferenceEquals(deliveries[0].Sender, messenger) && ReferenceEquals(deliveries[0].Recipient, recipient)
                && ReferenceEquals(deliveries[0].Payload, payload), "the event carries sender, recipient, and payload");
            Assert(ReferenceEquals(service.GetObjective(recipient)!.Objective, payload),
                "the recipient now runs the payload");

            // The recipient's new runner steps on the following ticks: a live PLAYER target chases natively.
            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(recipient.ChaseCalls.Count == 1 && ReferenceEquals(recipient.ChaseCalls[0], player),
                "the delivered follow program actually runs");

            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(deliveries.Count == 1 && messenger.MoveToCalls.Count == 1,
                "a delivered courier holds position and never re-delivers");
        }

        private static void TestReprogramTargetMissingMidJourney()
        {
            var recipient = new FakeRobotAgent { Position = new Vec3(10f, 0f, 0f) };
            var (service, clock, _) = NewRunnerService(entity => ReferenceEquals(entity, recipient) ? recipient : null);
            var messenger = new FakeRobotAgent();
            RobotTargetSnapshot? snapshot = new RobotTargetSnapshot(recipient.Position, recipient);
            service.RegisterTarget("ROBOT 2", RobotTargetKind.Robot, () => snapshot);

            var deliveries = new List<RobotProgramDelivery>();
            service.ProgramDelivered += delivery => deliveries.Add(delivery);

            var handle = service.SetObjective(messenger, RobotObjective.Reprogram("ROBOT 2", RobotObjective.Idle()));
            service.Tick(0.016f);
            Assert(messenger.MoveToCalls.Count == 1, "the courier sets out");

            snapshot = null;
            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.TargetMissing, "a vanished recipient parks the courier");

            snapshot = new RobotTargetSnapshot(recipient.Position, recipient);
            clock.Now += 2.5f;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Seeking, "the courier resumes when the recipient returns");

            messenger.HasReachedTarget = true;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Delivered && deliveries.Count == 1,
                "the resumed courier still delivers");
        }

        private static void TestReprogramUnmappableTargetStaysArrived()
        {
            var (service, clock, _) = NewRunnerService(_ => null); // nothing maps back to an agent
            var messenger = new FakeRobotAgent();
            service.RegisterTarget("CRATE", RobotTargetKind.Prop, () =>
                new RobotTargetSnapshot(new Vec3(6f, 0f, 0f), new FakeEntity("crate")));

            var deliveries = new List<RobotProgramDelivery>();
            service.ProgramDelivered += delivery => deliveries.Add(delivery);

            var handle = service.SetObjective(messenger, RobotObjective.Reprogram("CRATE", RobotObjective.Idle()));
            service.Tick(0.016f);
            messenger.HasReachedTarget = true;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Arrived, "an unmappable recipient leaves the courier Arrived");

            clock.Now += 1.5f;
            service.Tick(0.016f);
            clock.Now += 1.5f;
            service.Tick(0.016f);
            Assert(handle.State == RobotObjectiveState.Arrived && deliveries.Count == 0,
                "the courier keeps standing (and retrying) without ever delivering to a non-robot");
        }

        private static void TestReprogramReplacesRecipientsExistingObjective()
        {
            var recipient = new FakeRobotAgent { Position = new Vec3(10f, 0f, 0f) };
            var (service, _, _) = NewRunnerService(entity => ReferenceEquals(entity, recipient) ? recipient : null);
            var messenger = new FakeRobotAgent();
            service.RegisterTarget("ROBOT 2", RobotTargetKind.Robot,
                () => new RobotTargetSnapshot(recipient.Position, recipient));

            var previous = service.SetObjective(recipient, RobotObjective.GoTo(new Vec3(50f, 0f, 0f)));
            var payload = RobotObjective.Idle();
            var handle = service.SetObjective(messenger, RobotObjective.Reprogram("ROBOT 2", payload));

            service.Tick(0.016f);
            messenger.HasReachedTarget = true;
            service.Tick(0.016f); // delivery mutates the runner map mid-tick — must not throw

            Assert(handle.State == RobotObjectiveState.Delivered, "the courier delivers");
            Assert(previous.State == RobotObjectiveState.Cancelled, "the recipient's old program is cancelled clean-slate");
            Assert(ReferenceEquals(service.GetObjective(recipient)!.Objective, payload),
                "the payload replaces the recipient's old program");
        }

        private static float PlanarDistance(Vec3 a, Vec3 b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        private static (RobotObjectiveService Service, FakeClock Clock) NewService()
        {
            var clock = new FakeClock();
            return (new RobotObjectiveService(new NullLogger(), () => clock.Now), clock);
        }

        // The wander/flee/reprogram fixture: a scripted random source and (optionally) the live-object -> agent
        // mapping a Reprogram courier needs to hand its payload over.
        private static (RobotObjectiveService Service, FakeClock Clock, FakeRandom Random) NewRunnerService(
            Func<IEntity, IRobotAgent?>? resolveAgent = null)
        {
            var clock = new FakeClock();
            var random = new FakeRandom();
            return (new RobotObjectiveService(new NullLogger(), () => clock.Now, resolveAgent, random.Next), clock, random);
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

        // Scripted [0,1) source for wander legs / flee escape angles; falls back to 0.5 when the queue runs dry.
        private sealed class FakeRandom
        {
            private readonly Queue<float> queued = new Queue<float>();

            public void Enqueue(params float[] values)
            {
                foreach (var value in values)
                {
                    queued.Enqueue(value);
                }
            }

            public float Next()
            {
                return queued.Count > 0 ? queued.Dequeue() : 0.5f;
            }
        }

        // Records the movement intents the runner issues; reached/alive state is scripted by each test.
        private sealed class FakeRobotAgent : IRobotAgent
        {
            private readonly string id = Guid.NewGuid().ToString("N");
            private bool isAlive = true;

            public List<Vec3> MoveToCalls { get; } = new List<Vec3>();
            public List<IEntity> ChaseCalls { get; } = new List<IEntity>();
            public int StopCalls { get; private set; }
            public bool ThrowOnId { get; set; }
            public bool ThrowOnIsAlive { get; set; }
            public bool ThrowOnMoveTo { get; set; }

            public string Id => ThrowOnId ? throw new InvalidOperationException("id failed") : id;
            public string Name => "Fake robot";
            public bool IsAlive
            {
                get => ThrowOnIsAlive ? throw new InvalidOperationException("liveness failed") : isAlive;
                set => isAlive = value;
            }
            public Vec3 Position { get; set; }
            public Vec3 HeadPosition => Position;
            public RobotBrainMode BrainMode { get; private set; } = RobotBrainMode.Dormant;
            public bool IsMoving => false;
            public bool HasReachedTarget { get; set; }
            public float MoveSpeed { get; set; }
            public float TurnSpeed { get; set; }
            public float StopDistance { get; set; }
            public RobotGait Gait { get; set; }

            public OperationResult<bool> MoveTo(Vec3 position)
            {
                if (ThrowOnMoveTo)
                {
                    throw new InvalidOperationException("movement failed");
                }

                MoveToCalls.Add(position);
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> Chase(IEntity target)
            {
                ChaseCalls.Add(target);
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> Stop()
            {
                StopCalls++;
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> SetBrainMode(RobotBrainMode mode)
            {
                BrainMode = mode;
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> ConfigureMovement(RobotMovementSettings settings)
            {
                Gait = settings.Gait;
                MoveSpeed = settings.MoveSpeed;
                TurnSpeed = settings.TurnSpeed;
                StopDistance = settings.StopDistance;
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> SetTint(RobotColor color) => OperationResult<bool>.Success(true);

            public OperationResult<bool> SetEmote(string emojiShortcode) => OperationResult<bool>.Success(true);

            public OperationResult<bool> SetName(string name) => OperationResult<bool>.Success(true);

            public OperationResult<bool> SetScale(float scale) => OperationResult<bool>.Success(true);

            public OperationResult<bool> SetInteraction(RobotInteractionOptions options) =>
                OperationResult<bool>.Success(true);

            public OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source) =>
                OperationResult<bool>.Success(false);

            public OperationResult<bool> Kill(RobotDamageType type, string source)
            {
                IsAlive = false;
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> Ragdoll() => OperationResult<bool>.Success(true);

            public OperationResult<bool> Knockback(Vec3 impulse) => OperationResult<bool>.Success(true);

            public OperationResult<bool> Despawn()
            {
                IsAlive = false;
                return OperationResult<bool>.Success(true);
            }

            public void Dispose() => IsAlive = false;
        }

        private sealed class FakeEntity : IEntity
        {
            public FakeEntity(string id)
            {
                Id = id;
            }

            public string Id { get; }
            public string Name => Id;
            public bool IsAlive => true;
            public Vec3 Position => Vec3.Zero;
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
