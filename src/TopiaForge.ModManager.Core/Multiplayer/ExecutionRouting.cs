using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>Why mod code is running, as the runtime's intent scope records it.</summary>
    internal enum IntentSource
    {
        /// <summary>Scripted: timers, ticks, world events and anything else no player caused.</summary>
        None = 0,

        /// <summary>A frame in which the local player's input was active, after the input sample.</summary>
        InputActiveFrame = 1,

        /// <summary>A TopiaForgeUi renderer callback, such as a button press.</summary>
        UiCallback = 2,

        /// <summary>A command dispatched through the owner command service.</summary>
        OwnerCommand = 3,

        /// <summary>An interaction handler the local player triggered.</summary>
        InteractionHandler = 4,

        /// <summary>Code driving a motion lease the local player holds.</summary>
        MotionLease = 5
    }

    /// <summary>Where a call made by mod code takes effect.</summary>
    internal enum ExecutionRoute
    {
        /// <summary>The context never runs in this realm.</summary>
        NotRun = 0,

        /// <summary>The call takes effect here, and this result is the one every peer sees.</summary>
        Canonical = 1,

        /// <summary>Sent to the host as a request, predicted locally, then adopted or rolled back.</summary>
        Forwarded = 2,

        /// <summary>A local no-op bound to the host's result with the same (mod, sequence), so nothing duplicates.</summary>
        Shadow = 3,

        /// <summary>Takes effect on this peer only and is never replicated or forwarded.</summary>
        LocalOnly = 4
    }

    /// <summary>Who may change the session's time scale (freeze, slow or step).</summary>
    internal enum TimeControlRoute
    {
        /// <summary>The change applies, and online it is broadcast to every peer.</summary>
        Apply = 0,

        /// <summary>Refused: online, only the gamemode server on the host controls time; clients follow its scale.</summary>
        NotAuthoritative = 1
    }

    /// <summary>What the game's own pause does.</summary>
    internal enum NativePauseRoute
    {
        /// <summary>Single player: the pause menu freezes the world as it always has.</summary>
        FreezesWorld = 0,

        /// <summary>Online: the menu opens locally, and the shared world never freezes.</summary>
        LocalMenuOnly = 1
    }

    /// <summary>Where damage, healing and control of the local player take effect.</summary>
    internal enum LocalPlayerRoute
    {
        /// <summary>Single player: applied locally, as always.</summary>
        Local = 0,

        /// <summary>Online: applied by this player's own process and plausibility-checked by the host.</summary>
        OwnerAuthoritative = 1
    }

    /// <summary>Which sources count as player intent.</summary>
    internal static class IntentRules
    {
        public static bool IsDefined(IntentSource source) =>
            source >= IntentSource.None && source <= IntentSource.MotionLease;

        /// <summary>Every source except <see cref="IntentSource.None"/> is the local player acting.</summary>
        public static bool IsPlayerIntent(IntentSource source)
        {
            if (!IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
            return source != IntentSource.None;
        }
    }

    /// <summary>
    /// The execution model's routing tables. Scripts run on the host, a player's own actions run where that player
    /// is, and everyone sees the host's world.
    /// </summary>
    internal static class ExecutionRouting
    {
        // Routes for shared mods: [context, realm, intent], where intent 0 is scripted and 1 is player intent.
        // Realms are Offline, ListenHost and Client.
        private static readonly ExecutionRoute[,,] SharedRoutes =
        {
            // Process
            {
                { ExecutionRoute.Canonical, ExecutionRoute.Canonical },
                { ExecutionRoute.Canonical, ExecutionRoute.Canonical },
                { ExecutionRoute.Shadow, ExecutionRoute.Forwarded }
            },

            // WorldOwner
            {
                { ExecutionRoute.Canonical, ExecutionRoute.Canonical },
                { ExecutionRoute.Canonical, ExecutionRoute.Canonical },
                { ExecutionRoute.Shadow, ExecutionRoute.Forwarded }
            },

            // GamemodeServer
            {
                { ExecutionRoute.Canonical, ExecutionRoute.Canonical },
                { ExecutionRoute.Canonical, ExecutionRoute.Canonical },
                { ExecutionRoute.NotRun, ExecutionRoute.NotRun }
            },

            // Participant: a client's participant always acts for its own player, so it always forwards.
            {
                { ExecutionRoute.Canonical, ExecutionRoute.Canonical },
                { ExecutionRoute.Canonical, ExecutionRoute.Canonical },
                { ExecutionRoute.Forwarded, ExecutionRoute.Forwarded }
            }
        };

        /// <summary>The route for a shared mod, straight from the table.</summary>
        public static ExecutionRoute SharedRoute(NetRealm realm, ModExecutionContext context, bool playerIntent)
        {
            NetRealms.Require(realm);
            if (!ModExecutionContexts.IsDefined(context)) throw new ArgumentOutOfRangeException(nameof(context));
            return SharedRoutes[(int)context, (int)realm, playerIntent ? 1 : 0];
        }

        /// <summary>
        /// Where a world-mutating call takes effect. Code that does not run in the realm is <c>NotRun</c>;
        /// client-local mods are <c>LocalOnly</c>; everything else follows the shared table.
        /// </summary>
        public static ExecutionRoute Route(NetRealm realm, ModNetProfile profile, IntentSource intent)
        {
            var playerIntent = IntentRules.IsPlayerIntent(intent);
            if (!profile.RunsOn(realm)) return ExecutionRoute.NotRun;
            if (profile.NetworkClass == ModNetworkClass.ClientLocal) return ExecutionRoute.LocalOnly;
            return SharedRoute(realm, profile.Context, playerIntent);
        }

        /// <summary>
        /// Chronos freeze, slow or step. Offline, every context controls time. Online, only a gamemode server on the
        /// host does, and never a client-local mod's: its change would reach every peer, and client-local mods are never
        /// networked. A profile that does not run in the realm is not authoritative either.
        /// </summary>
        public static TimeControlRoute TimeControl(NetRealm realm, ModNetProfile profile)
        {
            NetRealms.Require(realm);
            if (!profile.RunsOn(realm)) return TimeControlRoute.NotAuthoritative;
            if (realm == NetRealm.Offline) return TimeControlRoute.Apply;
            return realm == NetRealm.ListenHost &&
                   profile.Context == ModExecutionContext.GamemodeServer &&
                   profile.NetworkClass != ModNetworkClass.ClientLocal
                ? TimeControlRoute.Apply
                : TimeControlRoute.NotAuthoritative;
        }

        public static NativePauseRoute NativePause(NetRealm realm) =>
            NetRealms.IsOnline(realm) ? NativePauseRoute.LocalMenuOnly : NativePauseRoute.FreezesWorld;

        public static LocalPlayerRoute LocalPlayerAction(NetRealm realm) =>
            NetRealms.IsOnline(realm) ? LocalPlayerRoute.OwnerAuthoritative : LocalPlayerRoute.Local;
    }
}
