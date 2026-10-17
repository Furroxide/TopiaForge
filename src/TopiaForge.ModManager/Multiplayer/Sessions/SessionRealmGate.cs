using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>What the interface does about a request to host or join, by the runtime's realm rules.</summary>
    internal enum RealmDecision
    {
        /// <summary>Join now: nothing local is running.</summary>
        Join = 0,

        /// <summary>A game is running; joining replaces it, so ask "Leave current game?" first.</summary>
        ConfirmLeaveGame = 1,

        /// <summary>Open the running game to other players in place.</summary>
        PromoteInPlace = 2,

        /// <summary>Start the chosen game, then open it.</summary>
        StartAsHost = 3,

        /// <summary>A game is starting or stopping, or the main menu is still loading; wait until it settles.</summary>
        Busy = 4,

        /// <summary>This game is hosting or joined; that session must end first.</summary>
        LeaveSessionFirst = 5,

        /// <summary>An enabled mod refuses every multiplayer session.</summary>
        StandaloneRefused = 6,

        /// <summary>Already in the realm asked for, as when hosting again.</summary>
        AlreadyThere = 7
    }

    /// <summary>
    /// Reads the runtime's realm verdict (<see cref="MultiplayerRuntime.EvaluateRealmChange"/>) as what the interface does
    /// next. It decides nothing itself: the runtime's rules read the session orchestrator's <c>RealmPhase</c>, so an idle
    /// session that is still changing, as while the main menu loads after leaving a game, is busy and a join waits. A gate
    /// that read the world session's raw phase would see that idle session as settled and join during the load.
    /// </summary>
    internal static class SessionRealmGate
    {
        /// <summary>Reads what joining another session does; <paramref name="realm"/> is the realm the verdict was asked from.</summary>
        internal static RealmDecision ForJoin(NetRealmTransition transition, NetRealm realm)
        {
            switch (transition.Verdict)
            {
                case NetRealmTransitionVerdict.Allowed:
                    return RealmDecision.Join;
                case NetRealmTransitionVerdict.Busy:
                    return RealmDecision.Busy;
                case NetRealmTransitionVerdict.StandaloneRefused:
                    return RealmDecision.StandaloneRefused;
                case NetRealmTransitionVerdict.RequiresLeave:
                    // Offline with a running game asks to leave it; from a session that session must end first.
                    return realm == NetRealm.Offline ? RealmDecision.ConfirmLeaveGame : RealmDecision.LeaveSessionFirst;
                default:
                    return RealmDecision.LeaveSessionFirst;
            }
        }

        /// <summary>Reads what hosting does.</summary>
        internal static RealmDecision ForHost(NetRealmTransition transition)
        {
            switch (transition.Verdict)
            {
                case NetRealmTransitionVerdict.Allowed:
                    return transition.Effect == NetRealmTransitionEffect.PromoteInPlace ? RealmDecision.PromoteInPlace : RealmDecision.StartAsHost;
                case NetRealmTransitionVerdict.Busy:
                    return RealmDecision.Busy;
                case NetRealmTransitionVerdict.StandaloneRefused:
                    return RealmDecision.StandaloneRefused;
                case NetRealmTransitionVerdict.Unchanged:
                    return RealmDecision.AlreadyThere;
                default:
                    return RealmDecision.LeaveSessionFirst;
            }
        }
    }
}
