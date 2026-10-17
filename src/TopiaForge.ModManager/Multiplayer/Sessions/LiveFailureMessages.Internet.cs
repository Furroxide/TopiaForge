using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The TopiaForge service's failures in this game's words, as the Internet sessions' design record words each code: a
    /// join's sentence, the step it stops at, and whether trying again can help. None repeats what the service or a host
    /// sent, and none names an address or an invite code.
    /// </summary>
    internal static partial class LiveFailureMessages
    {
        internal const string NoService =
            "Public and invite-only sessions, invites and listed servers need the TopiaForge service, which this game is not set up to reach. LAN play still works.";

        internal const string RelayKeepsHideMyIp =
            "The relay is unavailable right now, and " + MultiplayerPrivacyText.HideMyIp + " keeps this session on the relay, so it did not connect.";

        internal const string RelayHostHidden = "The host keeps its address hidden behind the relay, and the relay is unavailable right now.";

        private static FailureMessage? InternetJoin(InternalLiveFailure failure, bool hostAllowsDirect)
        {
            switch (failure)
            {
                case InternalLiveFailure.ServiceUnavailable:
                    return new FailureMessage("Service unreachable", "The TopiaForge service could not be reached. Try again shortly.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.ServiceNotConfigured:
                    return new FailureMessage("No service set up", ServiceNotConfigured, ConnectingStep.Resolving, false);
                case InternalLiveFailure.ServiceDisabled:
                    return new FailureMessage("Turned off for now", "The TopiaForge service has turned this off for now. Try again later.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.ServiceRateLimited:
                    return new FailureMessage("Too many requests",
                        "The TopiaForge service is turning away requests from this network for a moment. Try again shortly.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.InviteNotFound:
                    return new FailureMessage("Invite not found",
                        "That invite code does not work: it may be mistyped, expired or withdrawn, or its session may have ended.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.ServerNotListed:
                    return new FailureMessage("Server not listed", "That server is not listed any more.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.RelayUnavailable:
                    return new FailureMessage("Relay unavailable", hostAllowsDirect ? RelayKeepsHideMyIp : RelayHostHidden, ConnectingStep.Connecting, true);
                case InternalLiveFailure.UdpBlocked:
                    return new FailureMessage("Network blocked", "This network blocks the connections Internet play needs. Try another network.", ConnectingStep.Connecting, false);
                case InternalLiveFailure.HostOffline:
                    return new FailureMessage("Host offline", "The host is not online right now.", ConnectingStep.Connecting, true);
                case InternalLiveFailure.HostNotAnswering:
                    return new FailureMessage("Host not answering", "The host did not answer.", ConnectingStep.Connecting, true);
                case InternalLiveFailure.NoRoute:
                    return new FailureMessage("No connection", "No connection to the host could be made.", ConnectingStep.Connecting, true);
                case InternalLiveFailure.SignalingFailed:
                    return new FailureMessage("Service connection lost",
                        "The connection to the TopiaForge service was lost before the host could be reached.", ConnectingStep.Connecting, true);
                case InternalLiveFailure.InviteWithdrawn:
                    return new FailureMessage("Invite withdrawn",
                        "The host withdrew the invite, or the server stopped being public. Ask the host for a new invite.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.ServerBlocked:
                    return new FailureMessage("Server held back", "The TopiaForge service is holding this server back for now.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.ServiceRefused:
                    return new FailureMessage("Request refused", "The TopiaForge service refused a request from this game; it may need an update.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.InviteLimit:
                    return new FailureMessage("Too many invites",
                        "This session already has as many invites as it may hold. Withdraw one, or wait for one to expire.", ConnectingStep.Resolving, false);
                default:
                    return null;
            }
        }

        /// <summary>A hosted session's standing with the service, worded for its host: hosting goes on meanwhile.</summary>
        private static FailureMessage? InternetHost(InternalLiveFailure failure)
        {
            switch (failure)
            {
                case InternalLiveFailure.ServiceUnavailable:
                    return new FailureMessage("Service unreachable", "The TopiaForge service could not be reached. This game keeps trying by itself.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.ServiceNotConfigured:
                    return new FailureMessage("No service set up", NoService, ConnectingStep.Resolving, false);
                case InternalLiveFailure.ServiceDisabled:
                    return new FailureMessage("Turned off for now", "The TopiaForge service has turned hosting off for now. This game keeps checking by itself.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.ServiceRateLimited:
                    return new FailureMessage("Too many requests",
                        "The TopiaForge service is turning away requests from this network for a moment. This game tries again by itself.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.RelayUnavailable:
                    return new FailureMessage("Relay unavailable", "Players cannot reach this session until the relay returns.", ConnectingStep.Resolving, true);
                case InternalLiveFailure.ServerBlocked:
                    return new FailureMessage("Server held back", "The TopiaForge service is holding this server back for now. This game tries again much later.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.ServiceRefused:
                    return new FailureMessage("Request refused", "The TopiaForge service refused this session; this game may need an update.", ConnectingStep.Resolving, false);
                case InternalLiveFailure.InviteLimit:
                    return new FailureMessage("Too many invites",
                        "This session already has as many invites as it may hold. Withdraw one, or wait for one to expire.", ConnectingStep.Resolving, false);
                default:
                    return null;
            }
        }
    }
}
