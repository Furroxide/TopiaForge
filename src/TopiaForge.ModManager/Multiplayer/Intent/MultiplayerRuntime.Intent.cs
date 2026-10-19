using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerRuntime
    {
        private bool intentModeReported;

        /// <summary>
        /// Gets the intent tracking (WS3.4): the record of the player intent the current code acts on, its entry points, and
        /// the realm epoch it follows. The realm reports each change to it at the pump that shows it.
        /// </summary>
        internal MultiplayerIntents Intents { get; } = new MultiplayerIntents();

        /// <summary>
        /// Says once, when the runtime first attaches to the provider, how intent tracking runs its records here, so a
        /// fallback that allocates on every input-active frame shows in the manager log, as a warning, rather than only in a
        /// profiler. It names the way alone, never a player.
        /// </summary>
        private void ReportIntentMode()
        {
            if (intentModeReported) return;
            intentModeReported = true;
            ReportIntentMode(log, Intents.Scope.Mode);
        }

        /// <summary>Writes <paramref name="mode"/>'s line: a warning for the allocating fallback, information otherwise.</summary>
        internal static void ReportIntentMode(IMultiplayerLog log, IntentScopeMode mode)
        {
            if (mode == IntentScopeMode.AsyncLocal) log.Warn(MultiplayerIntents.DescribeMode(mode));
            else log.Info(MultiplayerIntents.DescribeMode(mode));
        }
    }
}
