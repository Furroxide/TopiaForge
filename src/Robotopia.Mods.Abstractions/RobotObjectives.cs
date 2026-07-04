using System;
using System.Collections.Generic;

namespace Robotopia.Mods
{
    /// <summary>
    /// Gives a spawned robot a <b>standing objective</b> — a persistent program ("go to the red marker", "follow the
    /// player", "patrol between here and the crate") that the service keeps executing frame-to-frame on top of the
    /// robot's native movement intents, so a mod (or an LLM decision) can program a robot once and walk away instead
    /// of hand-driving <see cref="IRobotAgent.MoveTo"/> every frame. Objectives reference world things by
    /// <b>registered target names</b> — a session-scoped vocabulary the consumer publishes (the player, marker pads,
    /// spawned props) — which is also exactly the closed set an LLM brain can be asked to choose from.
    /// </summary>
    /// <remarks>
    /// Published by the <c>Robotopia.RobotKit</c> framework mod and resolved with
    /// <c>context.GetService&lt;IRobotObjectiveService&gt;()</c>, exactly like <see cref="IRobotAgentService"/>.
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
        /// Registers (or replaces) a named target the objective vocabulary can reference. Names are matched
        /// case-insensitively and normalised to upper case. The resolver is invoked on the service tick whenever an
        /// objective needs the target's current whereabouts; return <c>null</c> to say the target is currently
        /// missing (despawned, not yet loaded) — objectives then wait rather than fail. Registers as
        /// <see cref="RobotTargetKind.Custom"/>; use the kinded overload when the target's nature is known.
        /// </summary>
        void RegisterTarget(string name, Func<RobotTargetSnapshot?> resolve);

        /// <summary>
        /// Registers (or replaces) a named target together with what KIND of thing it is, so consumers building
        /// LLM ground truth can describe it ("another robot", "a marker pad") instead of offering a bare name.
        /// </summary>
        void RegisterTarget(string name, RobotTargetKind kind, Func<RobotTargetSnapshot?> resolve);

        /// <summary>Removes a named target. Objectives referencing it park in <see cref="RobotObjectiveState.TargetMissing"/>.</summary>
        void UnregisterTarget(string name);

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
        /// Returns the handle for the new objective (also readable later via <see cref="GetObjective"/>).
        /// </summary>
        IRobotObjectiveHandle SetObjective(IRobotAgent agent, RobotObjective objective);

        /// <summary>The agent's current objective handle, or <c>null</c> when it has none.</summary>
        IRobotObjectiveHandle? GetObjective(IRobotAgent agent);

        /// <summary>Removes the agent's objective (if any) and stops it, leaving it idling natively.</summary>
        void ClearObjective(IRobotAgent agent);
    }

    /// <summary>
    /// What a registered named target resolves to <i>right now</i>. A non-null <see cref="GameObject"/> means the
    /// target is a live scene object a robot can natively track (<see cref="IRobotAgent.Chase"/>); otherwise only
    /// the <see cref="Position"/> is meaningful.
    /// </summary>
    public readonly struct RobotTargetSnapshot
    {
        /// <summary>Creates a snapshot of a target's current whereabouts.</summary>
        /// <param name="position">The target's current world position.</param>
        /// <param name="gameObject">The live <c>UnityEngine.GameObject</c> (as <see cref="object"/>, SDK stays Unity-free), or <c>null</c> for a plain point.</param>
        public RobotTargetSnapshot(Vec3 position, object? gameObject = null)
        {
            Position = position;
            GameObject = gameObject;
        }

        /// <summary>The target's current world position.</summary>
        public Vec3 Position { get; }

        /// <summary>The live scene object when the target is chaseable; <c>null</c> for a plain point.</summary>
        public object? GameObject { get; }
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
        Patrol
    }

    /// <summary>
    /// An immutable robot program: what to do (<see cref="Kind"/>) and what it applies to — a named target
    /// (<see cref="TargetName"/>), a fixed point (<see cref="TargetPoint"/>), or a patrol route
    /// (<see cref="Waypoints"/>). Build one with the factory methods and hand it to
    /// <see cref="IRobotObjectiveService.SetObjective"/>.
    /// </summary>
    public sealed class RobotObjective
    {
        private RobotObjective(RobotObjectiveKind kind, string? targetName, Vec3? targetPoint, IReadOnlyList<Vec3>? waypoints)
        {
            Kind = kind;
            TargetName = targetName;
            TargetPoint = targetPoint;
            Waypoints = waypoints;
        }

        /// <summary>The behaviour family.</summary>
        public RobotObjectiveKind Kind { get; }

        /// <summary>The registered target name this objective applies to, or <c>null</c> when it uses a point/route.</summary>
        public string? TargetName { get; }

        /// <summary>The fixed world point this objective applies to, or <c>null</c> when it uses a name/route.</summary>
        public Vec3? TargetPoint { get; }

        /// <summary>The patrol route (two or more points), or <c>null</c> for non-patrol objectives.</summary>
        public IReadOnlyList<Vec3>? Waypoints { get; }

        /// <summary>How close (metres) to the current goal counts as arrived. Applied as the agent's <see cref="IRobotAgent.StopDistance"/>.</summary>
        public float ArriveDistance { get; set; } = 1.5f;

        /// <summary>How long (seconds) a patrolling robot pauses at each waypoint before moving on.</summary>
        public float DwellSeconds { get; set; } = 1.0f;

        /// <summary>The native speed tier the robot moves at while executing this objective.</summary>
        public RobotGait Gait { get; set; } = RobotGait.Run;

        /// <summary>Stand down and idle.</summary>
        public static RobotObjective Idle()
        {
            return new RobotObjective(RobotObjectiveKind.Idle, null, null, null);
        }

        /// <summary>Walk to the named target once and stay there (re-walks if the target moves away).</summary>
        public static RobotObjective GoTo(string targetName)
        {
            return new RobotObjective(RobotObjectiveKind.GoTo, targetName ?? string.Empty, null, null);
        }

        /// <summary>Walk to a fixed world point once and stay there.</summary>
        public static RobotObjective GoTo(Vec3 point)
        {
            return new RobotObjective(RobotObjectiveKind.GoTo, null, point, null);
        }

        /// <summary>Continuously pursue the named target (natively when it is a live object). Never completes.</summary>
        public static RobotObjective Follow(string targetName)
        {
            return new RobotObjective(RobotObjectiveKind.Follow, targetName ?? string.Empty, null, null);
        }

        /// <summary>Loop over an explicit route of two or more points forever.</summary>
        public static RobotObjective Patrol(IReadOnlyList<Vec3> waypoints)
        {
            return new RobotObjective(RobotObjectiveKind.Patrol, null, null, waypoints ?? Array.Empty<Vec3>());
        }

        /// <summary>
        /// Patrol between wherever the robot is when the objective is set and the named target — the one-target
        /// patrol shape an LLM can request with a single closed-set field.
        /// </summary>
        public static RobotObjective PatrolTo(string targetName)
        {
            return new RobotObjective(RobotObjectiveKind.Patrol, targetName ?? string.Empty, null, null);
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
                default:
                    return "IDLE";
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
        Cancelled
    }

    /// <summary>
    /// A live objective handle. Read-only view of the program and its progress; <see cref="Cancel"/> stops it (the
    /// robot idles). Safe to keep polling after cancellation; never throws.
    /// </summary>
    public interface IRobotObjectiveHandle
    {
        /// <summary>The program being executed.</summary>
        RobotObjective Objective { get; }

        /// <summary>Where the objective currently is in its life.</summary>
        RobotObjectiveState State { get; }

        /// <summary>The index of the waypoint a patrol is currently heading to (0 for non-patrol objectives).</summary>
        int WaypointIndex { get; }

        /// <summary>Stops the objective and idles the robot. Idempotent.</summary>
        void Cancel();
    }
}
