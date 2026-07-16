using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// A live standard-agent robot. Drive its behaviour with movement intents (<see cref="MoveTo"/>,
    /// <see cref="Chase"/>, <see cref="Stop"/>) that are carried out by the game's own locomotion — the robot
    /// path-finds, collides, grounds, and animates natively, and re-paths on its own as a chased target moves.
    /// Override visuals (<see cref="SetTint"/>, <see cref="SetEmote"/>, <see cref="SetName"/>,
    /// <see cref="SetScale"/>) and wire combat through the native health/ragdoll pipeline
    /// (<see cref="ApplyDamage"/>, <see cref="Kill"/>, <see cref="Ragdoll"/>, <see cref="Knockback"/>) — or
    /// compose additional behavior through SDK services.
    /// </summary>
    public interface IRobotAgent : IEntity, IDisposable
    {
        /// <summary>
        /// Approximate world position of the robot's head — the top-centre of its live rendered volume,
        /// scale-aware (so it tracks <see cref="SetScale"/>). Use it for hit-zone tests (e.g. headshots: compare a
        /// ray-hit height against this) and to anchor world-space combat HUD such as floating damage numbers or a
        /// health pip above an enemy, rather than hard-coding the native robot's height in your mod. Degrades
        /// gracefully: when no renderers are resolvable (e.g. mid-teardown) it estimates the head from
        /// <see cref="IEntity.Position"/> plus the native robot's nominal height.
        /// </summary>
        Vec3 HeadPosition { get; }

        /// <summary>The robot's CURRENT brain mode (initially the spawn request's; changed by <see cref="SetBrainMode"/>).</summary>
        RobotBrainMode BrainMode { get; }

        /// <summary>
        /// Switches the robot's brain mode at runtime. To <see cref="RobotBrainMode.Dormant"/>: the native
        /// LLM brain and behaviour tree are suppressed and mod movement intents take over — the way to
        /// reprogram/override an autonomous robot. To <see cref="RobotBrainMode.Autonomous"/>: mod intents are
        /// cleared and the native brain is best-effort woken back up. Idempotent; never throws.
        /// </summary>
        OperationResult<bool> SetBrainMode(RobotBrainMode mode);

        /// <summary><c>true</c> while the robot is actively walking toward an intent target this frame.</summary>
        bool IsMoving { get; }

        /// <summary>
        /// <c>true</c> when the most recent <see cref="MoveTo"/>/<see cref="Chase"/> intent is satisfied — the
        /// robot is within <see cref="StopDistance"/> of its target.
        /// </summary>
        bool HasReachedTarget { get; }

        /// <summary>
        /// Optional override of the native gait speed in metres per second (the speed for the current
        /// <see cref="Gait"/>). <c>0</c> keeps the prefab's native speed. Best-effort: ignored if the game build
        /// does not expose the speed field.
        /// </summary>
        float MoveSpeed { get; }

        /// <summary>Optional best-effort override of the native turn speed in degrees per second; <c>0</c> keeps the prefab default.</summary>
        float TurnSpeed { get; }

        /// <summary>How close (metres) to the target counts as arrived; the native walk stops there.</summary>
        float StopDistance { get; }

        /// <summary>Which native speed tier the robot moves at (walk/run/sprint).</summary>
        RobotGait Gait { get; }

        /// <summary>Applies immutable movement tuning to the live agent.</summary>
        OperationResult<bool> ConfigureMovement(RobotMovementSettings settings);

        /// <summary>Walks to a fixed world position once and stops there (a single native walk).</summary>
        OperationResult<bool> MoveTo(Vec3 position);

        /// <summary>
        /// Continuously pursues a live SDK entity (e.g. the player) — the native locomotion
        /// tracks and re-paths to the target as it moves, stopping within <see cref="StopDistance"/>. Cheap to
        /// call once; pass the same entity to keep chasing. Pass a different entity to retarget.
        /// </summary>
        OperationResult<bool> Chase(IEntity target);

        /// <summary>Clears the current intent so the robot stops moving and idles natively.</summary>
        OperationResult<bool> Stop();

        /// <summary>Tints the whole robot via a material property block (cheap, non-destructive). The default keeps native colours.</summary>
        OperationResult<bool> SetTint(RobotColor color);

        /// <summary>Sets the robot's facial emote from an emoji shortcode (native expression system); empty clears it.</summary>
        OperationResult<bool> SetEmote(string emojiShortcode);

        /// <summary>Renames the opaque robot entity for diagnostics and framework UI.</summary>
        OperationResult<bool> SetName(string name);

        /// <summary>Uniformly scales the robot (1 = native size).</summary>
        OperationResult<bool> SetScale(float scale);

        /// <summary>
        /// Updates how the player can interact with this robot: native talk, disabled native talk, a native talk
        /// distance override, or a custom synchronous callback. Custom interactions take precedence over native
        /// talk while installed.
        /// </summary>
        OperationResult<bool> SetInteraction(RobotInteractionOptions options);

        /// <summary>
        /// Deals damage through the robot's native <c>Health</c> component (driving the native hurt/death/ragdoll
        /// pipeline). Returns <c>false</c> when the robot has no resolvable health. Note the native health regen
        /// is always-on, so enemies with their own hit-points are better off tracking damage in the mod and
        /// calling <see cref="Kill"/> when defeated.
        /// </summary>
        OperationResult<bool> ApplyDamage(float amount, RobotDamageType type, string source);

        /// <summary>
        /// Forces the robot's native death (ragdoll + corpse cleanup) immediately — the right call when the mod
        /// tracks its own hit-points and the enemy is defeated. Safe to call more than once.
        /// </summary>
        OperationResult<bool> Kill(RobotDamageType type, string source);

        /// <summary>Knocks the robot down into a native ragdoll without killing it; it self-recovers after a few seconds.</summary>
        OperationResult<bool> Ragdoll();

        /// <summary>Applies a physical impulse (native): a strong enough impulse knocks the robot into a ragdoll, like a hit reaction.</summary>
        OperationResult<bool> Knockback(Vec3 impulse);

        /// <summary>Removes and destroys this robot. Safe to call more than once.</summary>
        OperationResult<bool> Despawn();
    }

    /// <summary>Immutable movement tuning for a RobotKit agent.</summary>
    public sealed class RobotMovementSettings
    {
        /// <summary>Creates movement tuning.</summary>
        public RobotMovementSettings(
            RobotGait gait = RobotGait.Run,
            float moveSpeed = 0f,
            float turnSpeed = 0f,
            float stopDistance = 0f)
        {
            Gait = gait;
            MoveSpeed = moveSpeed;
            TurnSpeed = turnSpeed;
            StopDistance = stopDistance;
        }

        /// <summary>Gets the native gait.</summary>
        public RobotGait Gait { get; }

        /// <summary>Gets the optional movement-speed override.</summary>
        public float MoveSpeed { get; }

        /// <summary>Gets the optional turn-speed override.</summary>
        public float TurnSpeed { get; }

        /// <summary>Gets the arrival distance.</summary>
        public float StopDistance { get; }
    }
}
