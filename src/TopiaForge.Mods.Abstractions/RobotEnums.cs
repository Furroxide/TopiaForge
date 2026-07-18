namespace TopiaForge.Mods
{
    /// <summary>How much of the native robot brain runs on a spawned <see cref="IRobotAgent"/>.</summary>
    public enum RobotBrainMode
    {
        /// <summary>
        /// Default. The robot comes up fully native (body, locomotion, animation) but its LLM brain is suppressed
        /// — no autonomous planning, no RoboAPI calls, no self-directed walking/talking. The mod owns its
        /// decisions via the movement/visual intents. Predictable and free; right for enemies and scripted NPCs.
        /// </summary>
        Dormant,

        /// <summary>
        /// The native LLM agent is left running, so the robot perceives, thinks, talks, and moves on its own like
        /// any game robot (a true out-of-the-box NPC/companion). Costs RoboAPI gateway calls. Mod movement
        /// intents are not the intended driver in this mode — the robot drives itself.
        /// </summary>
        Autonomous
    }

    /// <summary>The native locomotion speed tier a robot moves at.</summary>
    public enum RobotGait
    {
        /// <summary>Walking speed.</summary>
        Walk,

        /// <summary>Running speed (default).</summary>
        Run,

        /// <summary>Sprinting speed.</summary>
        Sprint
    }

    /// <summary>Mirrors the game's native damage types for <see cref="IRobotAgent.ApplyDamage"/>/<see cref="IRobotAgent.Kill"/>.</summary>
    public enum RobotDamageType
    {
        /// <summary>Generic/physical damage.</summary>
        Normal,

        /// <summary>Fire damage.</summary>
        Fire,

        /// <summary>Electricity damage.</summary>
        Electricity,

        /// <summary>Poison damage.</summary>
        Poison,

        /// <summary>Water damage.</summary>
        Water
    }
}
