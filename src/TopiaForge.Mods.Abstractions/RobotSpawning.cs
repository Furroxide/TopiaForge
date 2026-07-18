namespace TopiaForge.Mods
{
    /// <summary>Parameters for <see cref="IRobotAgentService.Spawn"/>.</summary>
    public sealed class RobotAgentSpawnRequest
    {
        /// <summary>Creates a spawn request at a world position, optionally facing a direction.</summary>
        /// <param name="position">World position to spawn at.</param>
        /// <param name="facing">Optional initial facing direction (need not be normalized); <c>null</c> keeps prefab rotation.</param>
        /// <param name="brainMode">Initial native brain policy.</param>
        /// <param name="gait">Initial native movement gait.</param>
        /// <param name="moveSpeed">Optional movement-speed override in metres per second.</param>
        /// <param name="turnSpeed">Optional turn-speed override in degrees per second.</param>
        /// <param name="stopDistance">Arrival distance in metres.</param>
        /// <param name="tint">Optional whole-body color tint.</param>
        /// <param name="name">Optional diagnostic and UI name.</param>
        /// <param name="scale">Uniform scale, where one is native size.</param>
        /// <param name="interaction">Player interaction policy.</param>
        /// <param name="robotTypeId">Optional robot type descriptor identifier.</param>
        public RobotAgentSpawnRequest(
            Vec3 position,
            Vec3? facing = null,
            RobotBrainMode brainMode = RobotBrainMode.Dormant,
            RobotGait gait = RobotGait.Run,
            float moveSpeed = 0f,
            float turnSpeed = 0f,
            float stopDistance = 0f,
            RobotColor? tint = null,
            string? name = null,
            float scale = 1f,
            RobotInteractionOptions? interaction = null,
            string? robotTypeId = null)
        {
            Position = position;
            Facing = facing;
            BrainMode = brainMode;
            Gait = gait;
            MoveSpeed = moveSpeed;
            TurnSpeed = turnSpeed;
            StopDistance = stopDistance;
            Tint = tint;
            Name = name;
            Scale = scale;
            Interaction = interaction ?? RobotInteractionOptions.NativeTalk();
            RobotTypeId = robotTypeId;
        }

        /// <summary>World position to spawn the robot at.</summary>
        public Vec3 Position { get; }

        /// <summary>Optional initial facing direction; <c>null</c> keeps the prefab's rotation.</summary>
        public Vec3? Facing { get; }

        /// <summary>
        /// Whether the robot's brain is dormant (default — mod drives it) or autonomous (the native LLM agent
        /// thinks for itself). See <see cref="RobotBrainMode"/>.
        /// </summary>
        public RobotBrainMode BrainMode { get; }

        /// <summary>Which native speed tier the robot moves at; defaults to <see cref="RobotGait.Run"/>.</summary>
        public RobotGait Gait { get; }

        /// <summary>Optional gait-speed override in m/s applied to the spawned robot; <c>0</c> keeps the prefab default.</summary>
        public float MoveSpeed { get; }

        /// <summary>Optional turn-speed override in deg/s; <c>0</c> keeps the prefab default.</summary>
        public float TurnSpeed { get; }

        /// <summary>Initial <see cref="IRobotAgent.StopDistance"/> (metres).</summary>
        public float StopDistance { get; }

        /// <summary>Optional whole-body tint applied on spawn; <c>null</c> keeps native colours.</summary>
        public RobotColor? Tint { get; }

        /// <summary>Optional name for the spawned robot entity; <c>null</c> keeps a default.</summary>
        public string? Name { get; }

        /// <summary>Uniform spawn scale (1 = native size).</summary>
        public float Scale { get; }

        /// <summary>
        /// Player-facing interaction policy for the spawned robot. Defaults to the game's native talk prompt.
        /// </summary>
        public RobotInteractionOptions Interaction { get; }

        /// <summary>
        /// Which robot type (prefab) to spawn — an <see cref="RobotTypeDescriptor.Id"/> from
        /// <see cref="IRobotAgentService.RobotTypes"/>. <c>null</c> (default) spawns the default type. An unknown
        /// id logs a warning and falls back to the default rather than failing the spawn.
        /// </summary>
        public string? RobotTypeId { get; }
    }

    /// <summary>One spawnable robot type (a distinct robot prefab the current level exposes).</summary>
    public sealed class RobotTypeDescriptor
    {
        /// <summary>Creates a robot type descriptor.</summary>
        public RobotTypeDescriptor(string id, string displayName)
        {
            Id = id ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName;
        }

        /// <summary>Stable slug of the prefab name (e.g. <c>"worker-robot"</c>) — pass as <see cref="RobotAgentSpawnRequest.RobotTypeId"/>.</summary>
        public string Id { get; }

        /// <summary>Human-readable name for spawn UIs.</summary>
        public string DisplayName { get; }
    }
}
