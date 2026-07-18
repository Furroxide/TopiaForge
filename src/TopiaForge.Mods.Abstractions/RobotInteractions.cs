using System;

namespace TopiaForge.Mods
{
    /// <summary>How RobotKit should expose the game's native "talk to this robot" interaction.</summary>
    public enum RobotNativeTalkMode
    {
        /// <summary>Keep the native talk interaction enabled (default).</summary>
        Enabled,

        /// <summary>Disable the native talk interaction for this robot.</summary>
        Disabled
    }

    /// <summary>
    /// Player interaction policy for a RobotKit agent. Leave <see cref="CustomInteraction"/> <c>null</c> to use
    /// native talk behaviour; set it to expose a custom prompt and callback instead.
    /// </summary>
    public sealed class RobotInteractionOptions
    {
        private RobotInteractionOptions(
            RobotNativeTalkMode nativeTalkMode,
            float nativeTalkDistance,
            RobotCustomInteraction? customInteraction)
        {
            NativeTalkMode = nativeTalkMode;
            NativeTalkDistance = nativeTalkDistance;
            CustomInteraction = customInteraction;
        }

        /// <summary>Whether the base game's native talk interaction should be available.</summary>
        public RobotNativeTalkMode NativeTalkMode { get; }

        /// <summary>
        /// Optional native talk distance override in metres. Values less than or equal to zero keep the prefab's
        /// default speak distance.
        /// </summary>
        public float NativeTalkDistance { get; }

        /// <summary>
        /// Optional custom player interaction. When present, RobotKit disables native talk on this agent so the
        /// custom prompt is selected reliably.
        /// </summary>
        public RobotCustomInteraction? CustomInteraction { get; }

        /// <summary>Default policy: keep native talk enabled at the prefab's distance.</summary>
        public static RobotInteractionOptions NativeTalk()
        {
            return new RobotInteractionOptions(RobotNativeTalkMode.Enabled, 0f, null);
        }

        /// <summary>Keep native talk enabled, overriding the prefab's speak distance.</summary>
        public static RobotInteractionOptions NativeTalkAtDistance(float distance)
        {
            return new RobotInteractionOptions(RobotNativeTalkMode.Enabled, distance, null);
        }

        /// <summary>Disable the base game's native talk prompt for this robot.</summary>
        public static RobotInteractionOptions DisableNativeTalk()
        {
            return new RobotInteractionOptions(RobotNativeTalkMode.Disabled, 0f, null);
        }

        /// <summary>Use a custom prompt and callback instead of the native talk prompt.</summary>
        public static RobotInteractionOptions Custom(RobotCustomInteraction interaction)
        {
            return new RobotInteractionOptions(
                RobotNativeTalkMode.Disabled,
                0f,
                interaction ?? throw new ArgumentNullException(nameof(interaction)));
        }

        /// <summary>Returns a shallow copy; callback delegates are intentionally reused.</summary>
        public RobotInteractionOptions Clone()
        {
            return this;
        }
    }

    /// <summary>A custom player-facing interaction installed on a RobotKit agent.</summary>
    public sealed class RobotCustomInteraction
    {
        /// <summary>Creates a custom interaction with the prompt shown in the game's interaction UI.</summary>
        public RobotCustomInteraction(
            string prompt,
            Action<RobotInteractionContext>? interact = null,
            float distance = 3f,
            float screenRectExpansion = 0f,
            Func<RobotInteractionContext, bool>? canInteract = null)
        {
            Prompt = prompt ?? string.Empty;
            Interact = interact;
            Distance = distance;
            ScreenRectExpansion = screenRectExpansion;
            CanInteract = canInteract;
        }

        /// <summary>Prompt shown while the robot is the selected interactable.</summary>
        public string Prompt { get; }

        /// <summary>Maximum player-hand distance in metres. Values less than or equal to zero use 3 metres.</summary>
        public float Distance { get; }

        /// <summary>How far the robot's screen-space bounds expand for center-reticle selection.</summary>
        public float ScreenRectExpansion { get; }

        /// <summary>Optional per-frame gate for whether the prompt can be selected.</summary>
        public Func<RobotInteractionContext, bool>? CanInteract { get; }

        /// <summary>Synchronous callback invoked when the player activates the prompt.</summary>
        public Action<RobotInteractionContext>? Interact { get; }
    }

    /// <summary>Unity-free context passed to custom RobotKit interaction callbacks.</summary>
    public sealed class RobotInteractionContext
    {
        /// <summary>Creates a callback context from the selected robot and sampled hand positions.</summary>
        public RobotInteractionContext(
            IRobotAgent agent,
            Vec3 agentPosition,
            Vec3 handPosition,
            float distance)
        {
            Agent = agent ?? throw new ArgumentNullException(nameof(agent));
            AgentPosition = agentPosition;
            HandPosition = handPosition;
            Distance = distance;
        }

        /// <summary>The agent whose custom interaction is being queried or invoked.</summary>
        public IRobotAgent Agent { get; }

        /// <summary>The agent's current world position.</summary>
        public Vec3 AgentPosition { get; }

        /// <summary>The player hand's current world position.</summary>
        public Vec3 HandPosition { get; }

        /// <summary>Current hand-to-agent distance in metres.</summary>
        public float Distance { get; }
    }
}
