namespace TopiaForge.Mods.Internal
{
    // The settle refusal shares its External code with native exceptions. A world provider that
    // replaces unknown External text compares against this exact text to name the cause instead.
    internal static class WorldSpawnReadinessText
    {
        internal const string SettleRefused = "The native player did not retain the applied spawn position and rotation.";
    }
}
