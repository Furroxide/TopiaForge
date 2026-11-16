namespace TopiaForge.Mods.Internal
{
    // The settle refusal shares its External code with native exceptions. A world provider that
    // replaces unknown External text compares against this exact text to name the cause instead.
    internal static class WorldSpawnReadinessText
    {
        internal const string SettleRefused = "The native player did not retain the applied spawn position and rotation.";
        // The spawn-settle stage's own budgets. Other time-outs (scene, player or content) are not about settling.
        internal const string SettleOverallTimedOut = "World spawn readiness did not complete within 120 seconds overall.";
        internal const string SettleUnpausedTimedOut = "World spawn readiness did not complete within 30 unpaused seconds.";
    }
}
