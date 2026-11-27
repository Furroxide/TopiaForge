using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// Where a join stands on the connecting overlay's stepper, from the control's join phase and the session snapshot. The
    /// provider resolves the host and checks this game's mods against its signed description before it connects, then
    /// connects (asking for a password first when the session needs one), then waits for the host to admit the player,
    /// then synchronizes; so a running join walks Resolving, Connecting, Authenticating, then Loading world or
    /// Synchronizing, and Verifying mods is done once the host admits the player. A failure stops at the step it belongs to,
    /// or at the step the join had reached when that is later, and the steps it never ran are shown skipped.
    /// </summary>
    internal static class ConnectingStepMap
    {
        internal const string FindingHost = "Finding the host";
        internal const string LeavingGame = "Leaving your game";
        internal const string WaitingForGame = "Waiting for your game";
        internal const string PasswordNeeded = "Password needed";
        internal const string PreparingPassword = "Preparing the password";
        internal const string Reconnecting = "Reconnecting";
        internal const string WaitingForHost = "Waiting for the host";
        internal const string LoadingWorld = "Loading the host's world";
        internal const string ReceivingWorld = "Receiving the world";

        /// <summary>Gets the step a running join is on.</summary>
        internal static ConnectingStep StepOf(InternalJoinPhase phase, MultiplayerSessionState? session, bool readinessHeld)
        {
            switch (phase)
            {
                case InternalJoinPhase.AwaitingPassword:
                case InternalJoinPhase.PreparingProof:
                case InternalJoinPhase.Connecting:
                case InternalJoinPhase.Reconnecting:
                    return ConnectingStep.Connecting;
                case InternalJoinPhase.Synchronizing:
                    // The link is up; until the host accepts, the session is still connecting.
                    if (session == null || session.Value == MultiplayerSessionState.Connecting) return ConnectingStep.Authenticating;
                    return readinessHeld ? ConnectingStep.LoadingWorld : ConnectingStep.Synchronizing;
                case InternalJoinPhase.InSession:
                    return ConnectingStep.Spawning;
                default:
                    return ConnectingStep.Resolving;
            }
        }

        /// <summary>Gets the line under the step a running join is on.</summary>
        internal static string DetailOf(InternalJoinPhase phase, ConnectingStep step, string path)
        {
            switch (phase)
            {
                case InternalJoinPhase.AwaitingPassword:
                    return PasswordNeeded;
                case InternalJoinPhase.PreparingProof:
                    return PreparingPassword;
                case InternalJoinPhase.Reconnecting:
                    return Reconnecting;
                case InternalJoinPhase.Connecting:
                    return path;
                case InternalJoinPhase.Synchronizing:
                    return step == ConnectingStep.Authenticating ? WaitingForHost : step == ConnectingStep.LoadingWorld ? LoadingWorld : ReceivingWorld;
                default:
                    return step == ConnectingStep.Resolving ? FindingHost : string.Empty;
            }
        }

        /// <summary>Gets the step a failure stops at: its own step, or the one the join had reached when that is later.</summary>
        internal static ConnectingStep FailureStep(InternalLiveFailure failure, InternalAdmissionCode code, ConnectingStep reached)
        {
            var own = LiveFailureMessages.ForJoin(failure, code).Step;
            return own > reached ? own : reached;
        }

        /// <summary>
        /// Gets how a join reaches its host, for the Connecting step's line: "LAN", "Direct" or "Relay". Once the link is up,
        /// the route the status reports says it. Before that the target does: a LAN session, or a typed host joined with Hide
        /// my IP on, is on the local network, since nothing beyond it is contacted; a typed host the player allowed a direct
        /// connection is reached directly; an invite or a listed server starts on the relay, even when a direct path may
        /// take over later.
        /// </summary>
        internal static string PathOf(InternalJoinTargetKind kind, bool allowDirect, InternalConnectionRoute route)
        {
            if (route == InternalConnectionRoute.Direct) return "Direct";
            if (route == InternalConnectionRoute.Relayed) return "Relay";
            if (kind == InternalJoinTargetKind.Lan || (kind == InternalJoinTargetKind.Direct && !allowDirect)) return "LAN";
            return kind == InternalJoinTargetKind.Direct ? "Direct" : "Relay";
        }
    }
}
