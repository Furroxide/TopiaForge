using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>What the interface does about a request to host or join, by the realm rules.</summary>
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

        /// <summary>A game is starting or stopping; ask again once it settles.</summary>
        Busy = 4,

        /// <summary>This game is hosting or joined; that session must end first.</summary>
        LeaveSessionFirst = 5,

        /// <summary>An enabled mod refuses every multiplayer session.</summary>
        StandaloneRefused = 6,

        /// <summary>Already in the realm asked for, as when hosting again.</summary>
        AlreadyThere = 7
    }

    /// <summary>
    /// The realm rules as the interface applies them, from <see cref="NetRealms.EvaluateTransition"/>: opening a solo game
    /// promotes it in place, joining only replaces an idle local session (a running one asks "Leave current game?" first),
    /// and leaving is never refused. Until the runtime holds the realm itself (WS3 step 2), the realm is read from the
    /// control's role, and these rules gate the interface before it calls the SPI.
    /// </summary>
    internal static class SessionRealmGate
    {
        internal static NetRealm RealmOf(InternalSessionRole role) =>
            role == InternalSessionRole.ListenHost ? NetRealm.ListenHost
            : role == InternalSessionRole.Client ? NetRealm.Client
            : NetRealm.Offline;

        internal static SessionPhase PhaseOf(WorldSessionPhase phase) =>
            phase == WorldSessionPhase.Preparing ? SessionPhase.Preparing
            : phase == WorldSessionPhase.LoadingWorld ? SessionPhase.LoadingWorld
            : phase == WorldSessionPhase.StartingMode ? SessionPhase.StartingMode
            : phase == WorldSessionPhase.Running ? SessionPhase.Running
            : phase == WorldSessionPhase.Stopping ? SessionPhase.Stopping
            : SessionPhase.Idle;

        /// <summary>Decides what joining another session does from <paramref name="role"/> and the local game's phase.</summary>
        internal static RealmDecision ForJoin(InternalSessionRole role, WorldSessionPhase phase, bool standaloneModEnabled)
        {
            var transition = NetRealms.EvaluateTransition(RealmOf(role), NetRealm.Client, PhaseOf(phase), standaloneModEnabled);
            switch (transition.Verdict)
            {
                case NetRealmTransitionVerdict.Allowed:
                    return RealmDecision.Join;
                case NetRealmTransitionVerdict.Busy:
                    return RealmDecision.Busy;
                case NetRealmTransitionVerdict.StandaloneRefused:
                    return RealmDecision.StandaloneRefused;
                case NetRealmTransitionVerdict.Unchanged:
                    return RealmDecision.LeaveSessionFirst;
                default:
                    // Offline with a running game asks to leave it; from a session the session must end first.
                    return role == InternalSessionRole.Solo ? RealmDecision.ConfirmLeaveGame : RealmDecision.LeaveSessionFirst;
            }
        }

        /// <summary>Decides what hosting does from <paramref name="role"/> and the local game's phase.</summary>
        internal static RealmDecision ForHost(InternalSessionRole role, WorldSessionPhase phase, bool standaloneModEnabled)
        {
            var transition = NetRealms.EvaluateTransition(RealmOf(role), NetRealm.ListenHost, PhaseOf(phase), standaloneModEnabled);
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

        /// <summary>Gets whether leaving or ending the session may happen: it always may.</summary>
        internal static bool MayLeave(InternalSessionRole role) =>
            NetRealms.EvaluateTransition(RealmOf(role), NetRealm.Offline, SessionPhase.Running, standaloneModEnabled: false).Verdict != NetRealmTransitionVerdict.RequiresLeave;
    }
}
