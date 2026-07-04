using System;
using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.RobotKit
{
    // The per-robot objective state machine: turns a persistent RobotObjective into the agent's frame-to-frame
    // movement intents (MoveTo/Chase/Stop). Pure — it only touches the SDK contracts and an injected clock — so it
    // unit-tests on net8.0 with a fake agent. Stepped by RobotObjectiveService on the service tick; never throws.
    internal sealed class ObjectiveRunner : IRobotObjectiveHandle
    {
        // How often a named target is re-resolved while seeking/following (its object may move or despawn).
        private const float ReResolveSeconds = 1f;

        // How often a missing named target is re-tried before the robot gives up waiting (it never gives up).
        private const float MissingRetrySeconds = 2f;

        // How far (metres) beyond ArriveDistance a reached target must move before the robot re-walks to it.
        private const float ReChaseSlack = 1f;

        private readonly IRobotAgent agent;
        private readonly Func<string, RobotTargetSnapshot?> resolveTarget;
        private readonly Func<float> now;

        private RobotObjectiveState state;
        private int waypointIndex;
        private IReadOnlyList<Vec3>? route;      // materialised patrol route (PatrolTo resolves lazily)
        private Vec3 lastIssuedGoal;
        private bool goalIssued;
        private object? chasing;                 // the live object a Follow is natively tracking
        private float nextResolveAt;
        private float dwellUntil;

        public ObjectiveRunner(
            IRobotAgent agent,
            RobotObjective objective,
            Func<string, RobotTargetSnapshot?> resolveTarget,
            Func<float> now)
        {
            this.agent = agent;
            Objective = objective;
            this.resolveTarget = resolveTarget;
            this.now = now;
            state = objective.Kind == RobotObjectiveKind.Idle ? RobotObjectiveState.Idle : RobotObjectiveState.Seeking;
        }

        public RobotObjective Objective { get; }

        public RobotObjectiveState State => state;

        public int WaypointIndex => waypointIndex;

        public bool IsCancelled => state == RobotObjectiveState.Cancelled;

        public bool AgentAlive => agent.IsAlive;

        public void Cancel()
        {
            if (state == RobotObjectiveState.Cancelled)
            {
                return;
            }

            state = RobotObjectiveState.Cancelled;
            if (agent.IsAlive)
            {
                agent.Stop();
            }
        }

        // Advance the objective one tick. Cheap; issues a movement intent only when something changed.
        public void Step()
        {
            if (state == RobotObjectiveState.Cancelled || !agent.IsAlive)
            {
                return;
            }

            switch (Objective.Kind)
            {
                case RobotObjectiveKind.Idle:
                    StepIdle();
                    break;
                case RobotObjectiveKind.GoTo:
                    StepGoTo();
                    break;
                case RobotObjectiveKind.Follow:
                    StepFollow();
                    break;
                case RobotObjectiveKind.Patrol:
                    StepPatrol();
                    break;
            }
        }

        private void StepIdle()
        {
            if (!goalIssued)
            {
                goalIssued = true;
                agent.Stop();
                state = RobotObjectiveState.Idle;
            }
        }

        private void StepGoTo()
        {
            if (!TryCurrentTargetPosition(out var goal, out _))
            {
                return; // TargetMissing handled inside
            }

            if (!goalIssued || MovedBeyondSlack(goal, lastIssuedGoal))
            {
                IssueMoveTo(goal);
                return;
            }

            if (state == RobotObjectiveState.Seeking && agent.HasReachedTarget)
            {
                state = RobotObjectiveState.Arrived;
            }
        }

        private void StepFollow()
        {
            if (!TryCurrentTargetPosition(out var goal, out var liveObject))
            {
                chasing = null;
                return;
            }

            if (liveObject != null)
            {
                // A live object is tracked natively; one Chase call keeps re-pathing as it moves.
                if (!ReferenceEquals(chasing, liveObject))
                {
                    chasing = liveObject;
                    goalIssued = true;
                    ApplyGait();
                    agent.Chase(liveObject);
                    state = RobotObjectiveState.Seeking;
                    return;
                }
            }
            else
            {
                chasing = null;
                if (!goalIssued || MovedBeyondSlack(goal, lastIssuedGoal))
                {
                    IssueMoveTo(goal);
                    return;
                }
            }

            // A follower never completes; Arrived just means "currently in range".
            state = agent.HasReachedTarget ? RobotObjectiveState.Arrived : RobotObjectiveState.Seeking;
        }

        private void StepPatrol()
        {
            var waypoints = ResolveRoute();
            if (waypoints == null)
            {
                return; // TargetMissing (PatrolTo target not resolvable yet)
            }

            if (state == RobotObjectiveState.Dwelling)
            {
                if (now() < dwellUntil)
                {
                    return;
                }

                waypointIndex = (waypointIndex + 1) % waypoints.Count;
                goalIssued = false;
            }

            var goal = waypoints[waypointIndex];
            if (!goalIssued)
            {
                IssueMoveTo(goal);
                return;
            }

            if (state == RobotObjectiveState.Seeking && agent.HasReachedTarget)
            {
                state = RobotObjectiveState.Dwelling;
                dwellUntil = now() + Math.Max(0f, Objective.DwellSeconds);
            }
        }

        // The patrol route: explicit waypoints as-is; a PatrolTo materialises [start-position, target] once the
        // target first resolves. Returns null (and parks in TargetMissing) until then.
        private IReadOnlyList<Vec3>? ResolveRoute()
        {
            if (route != null)
            {
                return route;
            }

            if (Objective.Waypoints != null && Objective.Waypoints.Count >= 2)
            {
                route = Objective.Waypoints;
                return route;
            }

            if (Objective.TargetName != null)
            {
                var snapshot = resolveTarget(Objective.TargetName);
                if (snapshot == null)
                {
                    MarkTargetMissing();
                    return null;
                }

                route = new[] { agent.Position, snapshot.Value.Position };
                state = RobotObjectiveState.Seeking;
                return route;
            }

            // A patrol with fewer than two points has nowhere to go; treat as idle.
            state = RobotObjectiveState.Idle;
            if (!goalIssued)
            {
                goalIssued = true;
                agent.Stop();
            }

            return null;
        }

        // The objective's current goal position: a fixed point immediately, a named target via the registry with
        // periodic re-resolution. False parks the robot in TargetMissing (and Stops it once) until a retry succeeds.
        private bool TryCurrentTargetPosition(out Vec3 position, out object? liveObject)
        {
            liveObject = null;
            if (Objective.TargetPoint != null)
            {
                position = Objective.TargetPoint.Value;
                return true;
            }

            var name = Objective.TargetName;
            if (string.IsNullOrEmpty(name))
            {
                position = default;
                MarkTargetMissing();
                return false;
            }

            if (state == RobotObjectiveState.TargetMissing)
            {
                if (now() < nextResolveAt)
                {
                    position = default;
                    return false;
                }
            }
            else if (goalIssued && now() < nextResolveAt)
            {
                // Between re-resolves, keep acting on the last known goal.
                position = lastIssuedGoal;
                liveObject = chasing;
                return true;
            }

            var snapshot = resolveTarget(name!);
            nextResolveAt = now() + ReResolveSeconds;
            if (snapshot == null)
            {
                position = default;
                MarkTargetMissing();
                return false;
            }

            if (state == RobotObjectiveState.TargetMissing)
            {
                state = RobotObjectiveState.Seeking;
                goalIssued = false;
            }

            position = snapshot.Value.Position;
            liveObject = snapshot.Value.GameObject;
            return true;
        }

        private void MarkTargetMissing()
        {
            if (state != RobotObjectiveState.TargetMissing)
            {
                state = RobotObjectiveState.TargetMissing;
                goalIssued = false;
                chasing = null;
                agent.Stop();
            }

            nextResolveAt = now() + MissingRetrySeconds;
        }

        private void IssueMoveTo(Vec3 goal)
        {
            goalIssued = true;
            lastIssuedGoal = goal;
            ApplyGait();
            agent.MoveTo(goal);
            state = RobotObjectiveState.Seeking;
        }

        private void ApplyGait()
        {
            agent.StopDistance = Objective.ArriveDistance;
            agent.Gait = Objective.Gait;
        }

        private bool MovedBeyondSlack(Vec3 current, Vec3 issued)
        {
            // Re-walk when the goal has drifted meaningfully from the point the walk was issued at — a prop being
            // carried away, the player wandering off a reached spot. The slack keeps a jittering target from
            // spamming native walks.
            var dx = current.X - issued.X;
            var dy = current.Y - issued.Y;
            var dz = current.Z - issued.Z;
            var slack = Objective.ArriveDistance + ReChaseSlack;
            return dx * dx + dy * dy + dz * dz > slack * slack;
        }
    }
}
