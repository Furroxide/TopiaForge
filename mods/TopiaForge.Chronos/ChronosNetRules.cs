using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Chronos
{
    /// <summary>
    /// Chronos' rules in a multiplayer session (WS3.13), Unity-free so they unit-test. Time is the shared world's: online
    /// only the gamemode's server controller on the host freezes, slows or steps it, and everything else, every part of a
    /// joined player included, is refused with <see cref="ModErrorCode.NotAuthoritative"/> while that player follows the
    /// host's time. Online the game's own pause menu is one player's alone, so Chronos stops yielding the clock to it.
    /// Offline every rule is single player's, unchanged.
    /// </summary>
    internal static class ChronosNetRules
    {
        internal const string HostRefusal =
            "Time is shared in a multiplayer session, so only the gamemode's server controller can freeze, slow or step it; ask it to, for example through a command.";

        internal const string ClientRefusal =
            "This game follows the host's time in a multiplayer session, so only the host's gamemode server controller can freeze, slow or step it; ask it to through a command.";

        internal const string ClientLocalRefusal =
            "A client-local mod cannot freeze, slow or step time in a multiplayer session, which every player shares.";

        /// <summary>
        /// Says why code running in <paramref name="consumer"/>'s context may not freeze, slow or step time now, or null when
        /// it may: always in single player and for a context without the runtime's seam, which is single player; online only
        /// the gamemode's server controller on the host, as the execution model's table says.
        /// </summary>
        internal static string? Refusal(IRuntimeReplication? consumer)
        {
            if (consumer == null || consumer.TimeAuthority == InternalTimeAuthority.Apply) return null;
            if (consumer.NetworkClass == InternalNetworkClass.ClientLocal) return ClientLocalRefusal;
            return consumer.Realm == InternalNetRealm.Client ? ClientRefusal : HostRefusal;
        }

        /// <summary>
        /// Gets whether the game's own pause menu holds the clock: offline by single player's rule
        /// (<see cref="TimeScaleOwnership.IsNativePaused"/>), and never online, where the shared world never yields to one
        /// player's menu.
        /// </summary>
        internal static bool NativePauseHolds(
            bool yieldsToNativePause,
            bool explicitNativePause,
            bool hasWritten,
            float ownedScale,
            float observedScale) =>
            yieldsToNativePause && TimeScaleOwnership.IsNativePaused(explicitNativePause, hasWritten, ownedScale, observedScale);

        /// <summary>
        /// Plans putting real time back once no lease holds the clock: offline by single player's rule
        /// (<see cref="TimeScaleOwnership.PlanRestore"/>), which waits for the game's own pause menu; online at once, since
        /// nothing but Chronos owns the shared world's clock there.
        /// </summary>
        internal static TimeScaleRestorePlan PlanRestore(
            bool yieldsToNativePause,
            bool explicitNativePause,
            bool hasWritten,
            float ownedScale,
            float observedScale) =>
            yieldsToNativePause
                ? TimeScaleOwnership.PlanRestore(explicitNativePause, hasWritten, ownedScale, observedScale)
                : new TimeScaleRestorePlan(writeBaseline: hasWritten, retainOwnership: false);

        /// <summary>
        /// Describes Chronos' derived state as a session time; false for a state no session time can carry, such as a
        /// driver's scale that is not a number, which is then not published.
        /// </summary>
        internal static bool TryDescribe(float worldScale, TimeMode mode, out InternalSessionTime time) =>
            InternalSessionTime.TryCreate(worldScale, (InternalTimeMode)(int)mode, out time);
    }
}
