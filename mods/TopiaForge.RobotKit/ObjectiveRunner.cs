using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
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

        // How long a wander leg may run before the pick is written off as unreachable and a new one is chosen.
        private const float WanderLegTimeoutSeconds = 12f;

        // Extra metres beyond FleeDistance a threat must retreat before a fleeing robot counts as safe (hysteresis).
        private const float FleeSlackMeters = 2f;

        private readonly IRobotAgent agent;
        private readonly Func<string, RobotTargetSnapshot?> resolveTarget;
        private readonly Func<float> now;
        private readonly Func<float> random01;                       // [0,1) — wander legs, flee escape angles
        private readonly Func<object, IRobotAgent?>? resolveAgent;   // live object -> agent, for Reprogram delivery
        private readonly Action<IRobotAgent, RobotObjective>? deliver; // (recipient, payload); service closes over the sender
        private readonly Action<Exception>? reportFailure;

        private RobotObjectiveState state;
        private int waypointIndex;
        private IReadOnlyList<Vec3>? route;      // materialised patrol route (PatrolTo resolves lazily)
        private Vec3 lastIssuedGoal;
        private bool goalIssued;
        private object? chasing;                 // the live object a Follow is natively tracking
        private float nextResolveAt;
        private float dwellUntil;
        private Vec3? wanderHome;                // materialised wander home (set-time position or last named resolve)
        private float legDeadline;               // when the current wander leg is abandoned as unreachable
        private float deliverAttemptAt;          // hand-over retry cadence for an arrived courier

        public ObjectiveRunner(
            IRobotAgent agent,
            RobotObjective objective,
            Func<string, RobotTargetSnapshot?> resolveTarget,
            Func<float> now,
            Func<float>? random01 = null,
            Func<object, IRobotAgent?>? resolveAgent = null,
            Action<IRobotAgent, RobotObjective>? deliver = null,
            Action<Exception>? reportFailure = null)
        {
            this.agent = agent;
            Objective = objective;
            this.resolveTarget = resolveTarget;
            this.now = now;
            this.random01 = random01 ?? (() => 0.5f);
            this.resolveAgent = resolveAgent;
            this.deliver = deliver;
            this.reportFailure = reportFailure;
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
            try
            {
                if (agent.IsAlive)
                {
                    agent.Stop();
                }
            }
            catch (Exception exception)
            {
                try
                {
                    reportFailure?.Invoke(exception);
                }
                catch
                {
                    // Cancellation must stay safe even if an advisory logger is broken.
                }
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
                case RobotObjectiveKind.Wander:
                    StepWander();
                    break;
                case RobotObjectiveKind.Flee:
                    StepFlee();
                    break;
                case RobotObjectiveKind.Reprogram:
                    StepReprogram();
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

        private void StepWander()
        {
            if (!TryWanderHome(out var home))
            {
                return; // TargetMissing (named home currently unresolvable)
            }

            if (state == RobotObjectiveState.Dwelling)
            {
                if (now() < dwellUntil)
                {
                    return;
                }

                goalIssued = false;
            }

            if (!goalIssued)
            {
                // A fresh leg: a random spot around home, biased away from zero-length hops.
                var angle = random01() * 2f * (float)Math.PI;
                var distance = Objective.WanderRadius * (0.35f + 0.65f * random01());
                IssueMoveTo(new Vec3(
                    home.X + (float)Math.Sin(angle) * distance,
                    home.Y,
                    home.Z + (float)Math.Cos(angle) * distance));
                legDeadline = now() + WanderLegTimeoutSeconds;
                return;
            }

            if (state == RobotObjectiveState.Seeking && agent.HasReachedTarget)
            {
                state = RobotObjectiveState.Dwelling;
                dwellUntil = now() + Math.Max(0f, Objective.DwellSeconds);
                return;
            }

            if (state == RobotObjectiveState.Seeking && now() >= legDeadline)
            {
                // The pick never panned out (inside a wall, off the walkable grid) — quietly choose another.
                goalIssued = false;
            }
        }

        private void StepFlee()
        {
            var name = Objective.TargetName;
            if (string.IsNullOrEmpty(name))
            {
                // A flee without a threat has nothing to run from; treat as idle (the degenerate-patrol shape).
                state = RobotObjectiveState.Idle;
                if (!goalIssued)
                {
                    goalIssued = true;
                    agent.Stop();
                }

                return;
            }

            // Between evaluations the current hop keeps running natively (a missing threat waits out its retry).
            if (now() < nextResolveAt)
            {
                return;
            }

            var snapshot = resolveTarget(name!);
            nextResolveAt = now() + ReResolveSeconds;
            if (snapshot == null)
            {
                MarkTargetMissing(); // nothing to flee right now; stand down and keep watching for it
                return;
            }

            if (state == RobotObjectiveState.TargetMissing)
            {
                state = RobotObjectiveState.Seeking;
                goalIssued = false;
            }

            var position = agent.Position;
            var threat = snapshot.Value.Position;
            var dx = position.X - threat.X;
            var dz = position.Z - threat.Z;
            var distance = (float)Math.Sqrt(dx * dx + dz * dz);
            if (distance <= Objective.FleeDistance)
            {
                // Threatened: hop directly away, recomputed from live positions each evaluation so a cornered
                // robot keeps re-aiming and slides along whatever the native pathing allows.
                float awayX;
                float awayZ;
                if (distance < 0.01f)
                {
                    var angle = random01() * 2f * (float)Math.PI; // standing on the threat — any way out will do
                    awayX = (float)Math.Sin(angle);
                    awayZ = (float)Math.Cos(angle);
                }
                else
                {
                    awayX = dx / distance;
                    awayZ = dz / distance;
                }

                var hop = Math.Max(4f, Objective.FleeDistance * 0.5f);
                IssueMoveTo(new Vec3(position.X + awayX * hop, position.Y, position.Z + awayZ * hop));
                return;
            }

            if (distance > Objective.FleeDistance + FleeSlackMeters && state != RobotObjectiveState.Arrived)
            {
                // Safe (with slack so the boundary never oscillates): drop any hop mid-stride and stand watchful.
                state = RobotObjectiveState.Arrived;
                if (goalIssued)
                {
                    agent.Stop();
                    goalIssued = false;
                }
            }

            // In the hysteresis band: finish the current hop / keep standing.
        }

        private void StepReprogram()
        {
            if (state == RobotObjectiveState.Delivered)
            {
                return; // terminal: the payload was handed over; the courier holds here like an arrived GoTo
            }

            if (!TryCurrentTargetPosition(out var goal, out _))
            {
                return; // TargetMissing (recipient despawned mid-journey) — park and retry
            }

            if (!goalIssued || MovedBeyondSlack(goal, lastIssuedGoal))
            {
                IssueMoveTo(goal); // the recipient walked off — chase it down
                return;
            }

            if (!agent.HasReachedTarget)
            {
                return;
            }

            if (state == RobotObjectiveState.Seeking)
            {
                state = RobotObjectiveState.Arrived;
            }

            TryDeliver();
        }

        // Hand the payload over: re-resolve the recipient fresh (the journey cache may carry a stale goal with no
        // live object) and map its live object back to an agent. Unmappable names (a prop, the player) leave the
        // courier standing Arrived and retrying on the resolve cadence. State flips to Delivered and the courier
        // stops BEFORE the callback runs: SetObjective(recipient, payload) cancels/replaces runners mid-step —
        // including this one, if a consumer let a courier target itself — and nothing here runs after the callback.
        private void TryDeliver()
        {
            var payload = Objective.Payload;
            var name = Objective.TargetName;
            if (payload == null || deliver == null || resolveAgent == null || string.IsNullOrEmpty(name))
            {
                return; // not deliverable (no payload/wiring) — stand Arrived, like a GoTo that reached its goal
            }

            if (now() < deliverAttemptAt)
            {
                return;
            }

            deliverAttemptAt = now() + ReResolveSeconds;
            var snapshot = resolveTarget(name!);
            var live = snapshot?.GameObject;
            var recipient = live != null ? resolveAgent(live) : null;
            if (recipient == null || !recipient.IsAlive)
            {
                return;
            }

            state = RobotObjectiveState.Delivered;
            agent.Stop();
            deliver(recipient, payload);
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

        // The wander home: a fixed point as-is; a named target on the shared re-resolve cadence (the home drifts
        // with it, so WANDER NEAR PLAYER keeps orbiting the player); otherwise wherever the robot stood when the
        // objective was set, materialised once. Not TryCurrentTargetPosition — its between-resolves cache returns
        // the last issued LEG goal, which for wander is a spot near home, not home itself.
        private bool TryWanderHome(out Vec3 home)
        {
            if (Objective.TargetPoint != null)
            {
                home = Objective.TargetPoint.Value;
                return true;
            }

            var name = Objective.TargetName;
            if (string.IsNullOrEmpty(name))
            {
                wanderHome ??= agent.Position;
                home = wanderHome.Value;
                return true;
            }

            if (now() < nextResolveAt)
            {
                if (state == RobotObjectiveState.TargetMissing || wanderHome == null)
                {
                    home = default;
                    return false;
                }

                home = wanderHome.Value;
                return true;
            }

            var snapshot = resolveTarget(name!);
            nextResolveAt = now() + ReResolveSeconds;
            if (snapshot == null)
            {
                MarkTargetMissing();
                home = default;
                return false;
            }

            if (state == RobotObjectiveState.TargetMissing)
            {
                state = RobotObjectiveState.Seeking;
                goalIssued = false;
            }

            wanderHome = snapshot.Value.Position;
            home = wanderHome.Value;
            return true;
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
