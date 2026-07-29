using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Gives a spawned robot a <b>standing objective</b> — a persistent program ("go to the red marker", "follow the
    /// player", "patrol between here and the crate", "wander around the fountain", "keep away from the zombie",
    /// "walk this program over to ROBOT 2") that the service keeps executing frame-to-frame on top of the robot's
    /// native movement intents, so a mod (or an LLM decision) can program a robot once and walk away instead
    /// of hand-driving <see cref="IRobotAgent.MoveTo"/> every frame. Objectives reference world things by
    /// <b>registered target names</b> — a session-scoped vocabulary the consumer publishes (the player, marker pads,
    /// spawned props) — which is also exactly the closed set an LLM brain can be asked to choose from.
    /// </summary>
    /// <remarks>
    /// Published by the <c>TopiaForge.RobotKit</c> framework mod and resolved with
    /// <c>context.RequireExtension&lt;IRobotObjectiveService&gt;()</c>, exactly like
    /// <see cref="IRobotAgentService"/>.
    /// <para>
    /// Everything is poll-based and degrades gracefully: setting an objective returns a handle whose
    /// <see cref="IRobotObjectiveHandle.State"/> reports progress; a named target that currently resolves to nothing
    /// parks the robot in <see cref="RobotObjectiveState.TargetMissing"/> and quietly retries. Never throws.
    /// Objectives are <b>session-only</b> — the service drops all objectives and registered targets on a scene
    /// change; nothing is persisted.
    /// </para>
    /// </remarks>
    public interface IRobotObjectiveService
    {
        /// <summary><c>true</c> while the service is live (it is; kept for parity with the sibling services).</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Registers a named target the objective vocabulary can reference. Names are matched
        /// case-insensitively and normalised to upper case. The resolver is invoked on the service tick whenever an
        /// objective needs the target's current whereabouts; return <c>null</c> to say the target is currently
        /// missing (despawned, not yet loaded) — objectives then wait rather than fail.
        /// </summary>
        OperationResult<IRobotTargetRegistration> RegisterTarget(
            string name,
            RobotTargetKind kind,
            Func<RobotTargetSnapshot?> resolve);

        /// <summary>All currently registered target names (upper-cased) — the closed set to offer an LLM brain.</summary>
        IReadOnlyList<string> TargetNames { get; }

        /// <summary>All currently registered targets with their metadata, sorted like <see cref="TargetNames"/>.</summary>
        IReadOnlyList<RobotTargetInfo> Targets { get; }

        /// <summary>Looks up a registered target's metadata by (case-insensitive) name.</summary>
        bool TryGetTargetInfo(string name, out RobotTargetInfo info);

        /// <summary>Resolves a named target's current snapshot. Returns <c>false</c> when unknown or currently missing.</summary>
        bool TryResolveTarget(string name, out RobotTargetSnapshot snapshot);

        /// <summary>
        /// Programs the robot from a clean slate: any existing objective on the agent is cancelled and replaced.
        /// Returns the handle for the new objective (also readable later via <see cref="TryGetObjective"/>).
        /// </summary>
        OperationResult<IRobotObjectiveHandle> SetObjective(IRobotAgent agent, RobotObjective objective);

        /// <summary>Tries to get the agent's current objective handle.</summary>
        bool TryGetObjective(IRobotAgent agent, out IRobotObjectiveHandle? objective);

        /// <summary>Removes the agent's objective (if any) and stops it, leaving it idling natively.</summary>
        OperationResult<bool> ClearObjective(IRobotAgent agent);

        /// <summary>
        /// Raised on the service tick when a <see cref="RobotObjectiveKind.Reprogram"/> courier hands its payload to
        /// the recipient — the payload has already been applied via <see cref="SetObjective"/> when this fires, so
        /// subscribers are reacting (toasts, emotes), not deciding. Subscriber exceptions are swallowed and logged.
        /// </summary>
        event Action<RobotProgramDelivery>? ProgramDelivered;
    }

    /// <summary>A lifetime-owned named target registration.</summary>
    public interface IRobotTargetRegistration : IDisposable
    {
        /// <summary>Gets the normalized target name.</summary>
        string Name { get; }

        /// <summary>Gets the target category exposed to objective authors.</summary>
        RobotTargetKind Kind { get; }

        /// <summary>Gets whether this registration remains authoritative.</summary>
        bool IsActive { get; }
    }

    /// <summary>What a <see cref="IRobotObjectiveService.ProgramDelivered"/> hand-over was: who couriered what to whom.</summary>
    public sealed class RobotProgramDelivery
    {
        /// <summary>Creates a delivery record. All three parts are required.</summary>
        public RobotProgramDelivery(IRobotAgent sender, IRobotAgent recipient, RobotObjective payload)
        {
            Sender = sender ?? throw new ArgumentNullException(nameof(sender));
            Recipient = recipient ?? throw new ArgumentNullException(nameof(recipient));
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        /// <summary>The courier robot that walked the program over.</summary>
        public IRobotAgent Sender { get; }

        /// <summary>The robot that received (and is now running) the payload.</summary>
        public IRobotAgent Recipient { get; }

        /// <summary>The program that was applied to the recipient.</summary>
        public RobotObjective Payload { get; }
    }

    /// <summary>
    /// What a registered named target resolves to <i>right now</i>. A non-null <see cref="Entity"/> means the
    /// target is a live scene entity a robot can natively track (<see cref="IRobotAgent.Chase"/>); otherwise only
    /// the <see cref="Position"/> is meaningful.
    /// </summary>
    public readonly struct RobotTargetSnapshot
    {
        /// <summary>Creates a snapshot of a target's current whereabouts.</summary>
        /// <param name="position">The target's current world position.</param>
        /// <param name="entity">The live SDK entity, or <c>null</c> for a plain point.</param>
        public RobotTargetSnapshot(Vec3 position, IEntity? entity = null)
        {
            Position = position;
            Entity = entity;
        }

        /// <summary>The target's current world position.</summary>
        public Vec3 Position { get; }

        /// <summary>The live scene entity when the target is chaseable; <c>null</c> for a plain point.</summary>
        public IEntity? Entity { get; }
    }

    /// <summary>What kind of world thing a registered target is — used to describe targets to an LLM brain.</summary>
    public enum RobotTargetKind
    {
        /// <summary>Anything else; describe it via <see cref="RobotTargetInfo.Description"/>.</summary>
        Custom = 0,

        /// <summary>The human player/operator.</summary>
        Player,

        /// <summary>Another spawned robot.</summary>
        Robot,

        /// <summary>A spawned prop.</summary>
        Prop,

        /// <summary>A named marker pad — a stable place.</summary>
        Marker
    }

    /// <summary>A registered target's identity: its normalised name, kind, and optional flavour description.</summary>
    public sealed class RobotTargetInfo
    {
        /// <summary>Creates target metadata. The name is normalised (trimmed, upper-cased) like the registry keys.</summary>
        public RobotTargetInfo(string name, RobotTargetKind kind, string? description = null)
        {
            Name = (name ?? string.Empty).Trim().ToUpperInvariant();
            Kind = kind;
            Description = description;
        }

        /// <summary>The registered name, normalised to upper case.</summary>
        public string Name { get; }

        /// <summary>What kind of world thing the target is.</summary>
        public RobotTargetKind Kind { get; }

        /// <summary>Optional flavour text (e.g. "a red service robot") for <see cref="RobotTargetKind.Custom"/> targets.</summary>
        public string? Description { get; }
    }

    /// <summary>The behaviour family of a <see cref="RobotObjective"/>.</summary>
    public enum RobotObjectiveKind
    {
        /// <summary>Stand down and idle natively.</summary>
        Idle,

        /// <summary>Walk to a target (named or a fixed point) once and stay there.</summary>
        GoTo,

        /// <summary>Continuously pursue a target, re-pathing as it moves. Never completes.</summary>
        Follow,

        /// <summary>Loop over a route of waypoints forever, dwelling briefly at each.</summary>
        Patrol,

        /// <summary>Roam around a home spot (a point, a named target, or wherever the robot was), dwelling between random legs.</summary>
        Wander,

        /// <summary>Keep away from a named target, hopping off whenever it comes within <see cref="RobotObjective.FleeDistance"/>.</summary>
        Flee,

        /// <summary>Courier: walk to a target robot and apply the <see cref="RobotObjective.Payload"/> program to it on arrival.</summary>
        Reprogram
    }

    /// <summary>
    /// An immutable robot program: what to do (<see cref="Kind"/>) and what it applies to — a named target
    /// (<see cref="TargetName"/>), a fixed point (<see cref="TargetPoint"/>), or a patrol route
    /// (<see cref="Waypoints"/>). Build one with the factory methods and hand it to
    /// <see cref="IRobotObjectiveService.SetObjective"/>.
    /// </summary>
    public sealed class RobotObjective
    {
        private RobotObjective(RobotObjectiveKind kind, string? targetName, Vec3? targetPoint, IReadOnlyList<Vec3>? waypoints,
            RobotObjective? payload = null,
            RobotObjectiveOptions? options = null)
        {
            var tuning = options ?? RobotObjectiveOptions.Default;
            Kind = kind;
            TargetName = targetName;
            TargetPoint = targetPoint;
            Waypoints = waypoints == null
                ? null
                : new ReadOnlyCollection<Vec3>(new List<Vec3>(waypoints));
            Payload = payload;
            ArriveDistance = tuning.ArriveDistance;
            DwellSeconds = tuning.DwellSeconds;
            Gait = tuning.Gait;
            WanderRadius = tuning.WanderRadius;
            FleeDistance = tuning.FleeDistance;
        }

        /// <summary>The behaviour family.</summary>
        public RobotObjectiveKind Kind { get; }

        /// <summary>The registered target name this objective applies to, or <c>null</c> when it uses a point/route.</summary>
        public string? TargetName { get; }

        /// <summary>The fixed world point this objective applies to, or <c>null</c> when it uses a name/route.</summary>
        public Vec3? TargetPoint { get; }

        /// <summary>The patrol route (two or more points), or <c>null</c> for non-patrol objectives.</summary>
        public IReadOnlyList<Vec3>? Waypoints { get; }

        /// <summary>The program a <see cref="RobotObjectiveKind.Reprogram"/> courier delivers, or <c>null</c> for every other kind.</summary>
        public RobotObjective? Payload { get; }

        /// <summary>How close (metres) to the current goal counts as arrived. Applied as the agent's <see cref="IRobotAgent.StopDistance"/>.</summary>
        public float ArriveDistance { get; }

        /// <summary>How long (seconds) a patrolling robot pauses at each waypoint before moving on.</summary>
        public float DwellSeconds { get; }

        /// <summary>The native speed tier the robot moves at while executing this objective.</summary>
        public RobotGait Gait { get; }

        /// <summary>How far (metres) a wandering robot roams from its home spot.</summary>
        public float WanderRadius { get; }

        /// <summary>How close (metres) a fled-from target may come before the robot hops away again.</summary>
        public float FleeDistance { get; }

        /// <summary>Stand down and idle.</summary>
        public static RobotObjective Idle(RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(RobotObjectiveKind.Idle, null, null, null, options: options);
        }

        /// <summary>Walk to the named target once and stay there (re-walks if the target moves away).</summary>
        public static RobotObjective GoTo(string targetName, RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(RobotObjectiveKind.GoTo, targetName ?? string.Empty, null, null, options: options);
        }

        /// <summary>Walk to a fixed world point once and stay there.</summary>
        public static RobotObjective GoTo(Vec3 point, RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(RobotObjectiveKind.GoTo, null, point, null, options: options);
        }

        /// <summary>Continuously pursue the named target (natively when it is a live entity). Never completes.</summary>
        public static RobotObjective Follow(string targetName, RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(RobotObjectiveKind.Follow, targetName ?? string.Empty, null, null, options: options);
        }

        /// <summary>Loop over an explicit route of two or more points forever.</summary>
        public static RobotObjective Patrol(IReadOnlyList<Vec3> waypoints, RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(
                RobotObjectiveKind.Patrol,
                null,
                null,
                waypoints ?? Array.Empty<Vec3>(),
                options: options);
        }

        /// <summary>
        /// Patrol between wherever the robot is when the objective is set and the named target — the one-target
        /// patrol shape an LLM can request with a single closed-set field.
        /// </summary>
        public static RobotObjective PatrolTo(string targetName, RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(RobotObjectiveKind.Patrol, targetName ?? string.Empty, null, null, options: options);
        }

        /// <summary>Roam around wherever the robot is when the objective is set, dwelling between random legs.</summary>
        public static RobotObjective Wander(RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(RobotObjectiveKind.Wander, null, null, null, options: options);
        }

        /// <summary>Roam around the named target — the home drifts with it as it moves (re-resolved like Follow).</summary>
        public static RobotObjective Wander(string targetName, RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(RobotObjectiveKind.Wander, targetName ?? string.Empty, null, null, options: options);
        }

        /// <summary>Roam around a fixed world point.</summary>
        public static RobotObjective Wander(Vec3 home, RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(RobotObjectiveKind.Wander, null, home, null, options: options);
        }

        /// <summary>
        /// Keep away from the named target: whenever it comes within <see cref="FleeDistance"/>, hop directly away,
        /// re-evaluating as it moves. <see cref="RobotObjectiveState.Arrived"/> means "currently at a safe distance".
        /// </summary>
        public static RobotObjective Flee(string targetName, RobotObjectiveOptions? options = null)
        {
            return new RobotObjective(RobotObjectiveKind.Flee, targetName ?? string.Empty, null, null, options: options);
        }

        /// <summary>
        /// Courier a program to another robot: walk to the named target robot and, on arrival, apply
        /// <paramref name="payload"/> to it as its new objective (clean-slate, like
        /// <see cref="IRobotObjectiveService.SetObjective"/>). The messenger then parks in
        /// <see cref="RobotObjectiveState.Delivered"/> and the service raises
        /// <see cref="IRobotObjectiveService.ProgramDelivered"/>. The payload cannot itself be a Reprogram —
        /// couriers deliver programs, not chain letters.
        /// </summary>
        public static RobotObjective Reprogram(
            string targetRobotName,
            RobotObjective payload,
            RobotObjectiveOptions? options = null)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (payload.Kind == RobotObjectiveKind.Reprogram)
            {
                throw new ArgumentException("A reprogram payload cannot itself be a Reprogram.", nameof(payload));
            }

            return new RobotObjective(
                RobotObjectiveKind.Reprogram,
                targetRobotName ?? string.Empty,
                null,
                null,
                payload,
                options);
        }

        /// <summary>A short human-readable description (e.g. <c>"FOLLOW PLAYER"</c>) for HUD badges and ground-truth facts.</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case RobotObjectiveKind.GoTo:
                    return TargetName != null ? "GO TO " + TargetName : "GO TO POINT";
                case RobotObjectiveKind.Follow:
                    return "FOLLOW " + (TargetName ?? "TARGET");
                case RobotObjectiveKind.Patrol:
                    return TargetName != null ? "PATROL TO " + TargetName : "PATROL ROUTE";
                case RobotObjectiveKind.Wander:
                    return TargetName != null ? "WANDER NEAR " + TargetName : "WANDER";
                case RobotObjectiveKind.Flee:
                    return "FLEE FROM " + (TargetName ?? "TARGET");
                case RobotObjectiveKind.Reprogram:
                    return "REPROGRAM " + (TargetName ?? "ROBOT") + ": " + (Payload?.Describe() ?? "IDLE");
                default:
                    return "IDLE";
            }
        }
    }

    /// <summary>Immutable movement and timing tuning copied into a <see cref="RobotObjective"/>.</summary>
    public sealed class RobotObjectiveOptions
    {
        private static readonly RobotObjectiveOptions DefaultValue = new RobotObjectiveOptions();

        /// <summary>Creates validated objective tuning.</summary>
        /// <param name="arriveDistance">Distance in metres that counts as arrival.</param>
        /// <param name="dwellSeconds">Pause duration at patrol and wander destinations.</param>
        /// <param name="gait">Native locomotion gait used while executing the objective.</param>
        /// <param name="wanderRadius">Maximum wander radius in metres.</param>
        /// <param name="fleeDistance">Distance in metres a fleeing robot tries to maintain.</param>
        public RobotObjectiveOptions(
            float arriveDistance = 1.5f,
            float dwellSeconds = 1f,
            RobotGait gait = RobotGait.Run,
            float wanderRadius = 8f,
            float fleeDistance = 8f)
        {
            RequireFiniteNonNegative(arriveDistance, nameof(arriveDistance));
            RequireFiniteNonNegative(dwellSeconds, nameof(dwellSeconds));
            RequireFinitePositive(wanderRadius, nameof(wanderRadius));
            RequireFinitePositive(fleeDistance, nameof(fleeDistance));
            if (!Enum.IsDefined(typeof(RobotGait), gait))
            {
                throw new ArgumentOutOfRangeException(nameof(gait));
            }

            ArriveDistance = arriveDistance;
            DwellSeconds = dwellSeconds;
            Gait = gait;
            WanderRadius = wanderRadius;
            FleeDistance = fleeDistance;
        }

        /// <summary>Gets the shared default tuning.</summary>
        public static RobotObjectiveOptions Default => DefaultValue;

        /// <summary>Gets the distance in metres that counts as arrival.</summary>
        public float ArriveDistance { get; }

        /// <summary>Gets the pause duration at patrol and wander destinations.</summary>
        public float DwellSeconds { get; }

        /// <summary>Gets the native locomotion gait.</summary>
        public RobotGait Gait { get; }

        /// <summary>Gets the maximum wander radius in metres.</summary>
        public float WanderRadius { get; }

        /// <summary>Gets the distance in metres a fleeing robot tries to maintain.</summary>
        public float FleeDistance { get; }

        private static void RequireFiniteNonNegative(float value, string parameter)
        {
            if (value < 0f || float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }

        private static void RequireFinitePositive(float value, string parameter)
        {
            if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }
    }

    /// <summary>Where an objective currently is in its life. Poll <see cref="IRobotObjectiveHandle.State"/>.</summary>
    public enum RobotObjectiveState
    {
        /// <summary>The objective is an <see cref="RobotObjectiveKind.Idle"/> program (or has nothing to do).</summary>
        Idle,

        /// <summary>The robot is moving toward its current goal.</summary>
        Seeking,

        /// <summary>The robot reached its goal (a follower keeps tracking; a go-to holds position).</summary>
        Arrived,

        /// <summary>A patrolling robot is pausing at a waypoint before moving on.</summary>
        Dwelling,

        /// <summary>The named target currently resolves to nothing; the robot waits and the service retries.</summary>
        TargetMissing,

        /// <summary>The objective was cancelled or replaced; the handle is inert.</summary>
        Cancelled,

        /// <summary>A <see cref="RobotObjectiveKind.Reprogram"/> courier handed its payload over; the messenger holds position. Terminal.</summary>
        Delivered
    }

    /// <summary>
    /// A live objective handle. Read-only view of the program and its progress; disposal stops it and idles the
    /// robot. Safe to keep polling after disposal; never throws.
    /// </summary>
    public interface IRobotObjectiveHandle : IDisposable
    {
        /// <summary>The program being executed.</summary>
        RobotObjective Objective { get; }

        /// <summary>Where the objective currently is in its life.</summary>
        RobotObjectiveState State { get; }

        /// <summary>The index of the waypoint a patrol is currently heading to (0 for non-patrol objectives).</summary>
        int WaypointIndex { get; }

        /// <summary>Gets whether this objective remains active.</summary>
        bool IsActive { get; }
    }
}
