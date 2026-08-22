using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Projects the live status surfaces of framework-owned providers into RuntimeInfo. Presence alone is not
    /// enough: a module can load successfully while its current game binding, credentials, or scene adapter is
    /// unavailable. Probes are run only by the main-thread runtime lifecycle.
    /// </summary>
    internal static class RuntimeCapabilityProbe
    {
        private const string RobotKitProvider = "io.github.furroxide.topiaforge.robotkit";
        private const string WorldsProvider = "io.github.furroxide.topiaforge.worlds";
        private const string ChronosProvider = "io.github.furroxide.topiaforge.chronos";
        private const string PromptsProvider = "io.github.furroxide.topiaforge.prompts";

        internal static void Refresh(RuntimeInfo runtimeInfo, ModServiceRegistry registry)
        {
            if (runtimeInfo == null) throw new ArgumentNullException(nameof(runtimeInfo));
            if (registry == null) throw new ArgumentNullException(nameof(registry));

            Report<IRobotAgentService>(
                runtimeInfo,
                registry,
                RobotKitProvider,
                "robotkit",
                service => service.IsAvailable,
                "RobotKit loaded, but did not register its robot game adapter.",
                "RobotKit loaded, but its robot game adapter is unavailable in the current scene or game build.");
            Report<IRobotAgentService>(
                runtimeInfo,
                registry,
                RobotKitProvider,
                "robotkit.navigation",
                service => service.IsNavigationAvailable,
                "RobotKit loaded, but did not register its navigation adapter.",
                "RobotKit navigation is unavailable in the current scene or game build.");
            Report<IRobotBrainQueryService>(
                runtimeInfo,
                registry,
                RobotKitProvider,
                "robotkit.brain",
                service => service.IsAvailable,
                "RobotKit loaded, but did not register its brain-query adapter.",
                "RobotKit brain queries are unavailable because the backend or player credentials are not available.");
            Report<IRobotConversationService>(
                runtimeInfo,
                registry,
                RobotKitProvider,
                "robotkit.conversation",
                service => service.IsAvailable,
                "RobotKit loaded, but did not register its conversation adapter.",
                "RobotKit conversations are unavailable because the brain backend is not available.");
            Report<IPlayerDialogueInputService>(
                runtimeInfo,
                registry,
                RobotKitProvider,
                "robotkit.voice",
                service => service.IsVoiceAvailable,
                "RobotKit loaded, but did not register its dialogue-input adapter.",
                "RobotKit voice input is unavailable because a microphone, backend, or player credentials are not available.");
            Report<IRobotObjectiveService>(
                runtimeInfo,
                registry,
                RobotKitProvider,
                "robotkit.objectives",
                service => service.IsAvailable,
                "RobotKit loaded, but did not register its objective service.",
                "RobotKit objectives are unavailable.");

            Report<IWorldGamemodeService>(
                runtimeInfo,
                registry,
                WorldsProvider,
                "worlds",
                _ => true,
                "Worlds loaded, but did not register its world and gamemode service.",
                "World and gamemode registration is unavailable.");
            Report<IWorldPauseMenuService>(
                runtimeInfo,
                registry,
                WorldsProvider,
                "worlds.pause-menu",
                service => service.IsAvailable,
                "Worlds loaded, but did not register its pause-menu adapter.",
                "The Worlds pause-menu adapter is unavailable in the current scene or game build.");

            Report<ITimeControlService>(
                runtimeInfo,
                registry,
                ChronosProvider,
                "chronos",
                service => service.IsAvailable,
                "Chronos loaded, but did not register its time-control service.",
                "Chronos time control is unavailable.");
            Report<IPromptOverrideRegistry>(
                runtimeInfo,
                registry,
                PromptsProvider,
                "prompts",
                _ => true,
                "Prompts loaded, but did not register its prompt override service.",
                "Prompt overrides are unavailable.");
        }

        private static void Report<T>(
            RuntimeInfo runtimeInfo,
            ModServiceRegistry registry,
            string providerId,
            string capability,
            Func<T, bool> isAvailable,
            string missingReason,
            string unavailableReason) where T : class
        {
            var provider = registry.Get<T>();
            if (provider == null)
            {
                runtimeInfo.ReportCapabilityAvailability(providerId, capability, false, missingReason);
                return;
            }

            try
            {
                runtimeInfo.ReportCapabilityAvailability(
                    providerId,
                    capability,
                    isAvailable(provider),
                    unavailableReason);
            }
            catch (Exception exception)
            {
                runtimeInfo.ReportCapabilityAvailability(
                    providerId,
                    capability,
                    false,
                    "The " + capability + " provider could not report availability: " + exception.Message);
            }
        }
    }
}
