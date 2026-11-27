using TopiaForge.Multiplayer.NatTraversal;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// Turns what the service and the NAT layer report into the live sessions' failures, and says each in this game's own
    /// words. No sentence repeats anything the service or a host sent, and none names an address.
    /// </summary>
    internal static class InternetFailures
    {
        /// <summary>Maps a failed service request; <paramref name="invite"/> says whether a miss means an invite or a server.</summary>
        internal static LiveFailure OfService(ServiceFailure failure, bool invite = false)
        {
            switch (failure)
            {
                case ServiceFailure.None:
                    return LiveFailure.None;
                case ServiceFailure.NotConfigured:
                    return LiveFailure.ServiceNotConfigured;
                case ServiceFailure.Cancelled:
                    return LiveFailure.Cancelled;
                case ServiceFailure.FeatureDisabled:
                    return LiveFailure.ServiceDisabled;
                case ServiceFailure.RateLimited:
                    return LiveFailure.ServiceRateLimited;
                case ServiceFailure.NotFound:
                    return invite ? LiveFailure.InviteNotFound : LiveFailure.ServerNotListed;
                case ServiceFailure.ServerBlocked:
                    return LiveFailure.ServerBlocked;
                case ServiceFailure.RelayUnavailable:
                case ServiceFailure.TicketLimit:
                    return LiveFailure.RelayUnavailable;
                case ServiceFailure.Unauthorized:
                    // Only a ticket or a token that stopped holding is refused this way once it was issued.
                    return invite ? LiveFailure.InviteWithdrawn : LiveFailure.ServerNotListed;
                case ServiceFailure.InviteLimit:
                    return LiveFailure.InviteLimit;
                case ServiceFailure.InvalidRequest:
                case ServiceFailure.InvalidDescriptor:
                case ServiceFailure.UnsupportedVisibility:
                case ServiceFailure.ChallengeInvalid:
                case ServiceFailure.ProofInvalid:
                case ServiceFailure.MethodNotAllowed:
                case ServiceFailure.StaleDescriptor:
                case ServiceFailure.PayloadTooLarge:
                case ServiceFailure.UnsupportedMediaType:
                case ServiceFailure.Refused:
                    return LiveFailure.ServiceRefused;
                default:
                    return LiveFailure.ServiceUnavailable;
            }
        }

        /// <summary>Maps why traversal failed. A session that ended to make way, or was stopped, ends as the flow ended it.</summary>
        internal static LiveFailure OfNat(NatFailure failure)
        {
            switch (failure)
            {
                case NatFailure.UdpBlocked:
                    return LiveFailure.UdpBlocked;
                case NatFailure.RelayUnavailable:
                    return LiveFailure.RelayUnavailable;
                case NatFailure.HostOffline:
                    return LiveFailure.HostOffline;
                case NatFailure.NoAnswer:
                    return LiveFailure.HostNotAnswering;
                case NatFailure.SignalingFailed:
                    return LiveFailure.SignalingFailed;
                case NatFailure.HostEnded:
                    return LiveFailure.HostEnded;
                case NatFailure.Cancelled:
                    return LiveFailure.Cancelled;
                case NatFailure.TicketRevoked:
                    return LiveFailure.InviteWithdrawn;
                default:
                    return LiveFailure.NoRoute;
            }
        }

        /// <summary>
        /// Says a failure in this game's own words, for the join's detail line. <paramref name="hostAllowsDirect"/> tells a
        /// relay-only join that could not get the relay whether the host would take a direct connection instead.
        /// </summary>
        internal static string Detail(LiveFailure failure, bool hostAllowsDirect = true)
        {
            switch (failure)
            {
                case LiveFailure.ServiceNotConfigured:
                    return "This game is not set up to reach the TopiaForge service, so invites and listed servers are unavailable. LAN and direct play still work.";
                case LiveFailure.ServiceUnavailable:
                    return "The TopiaForge service could not be reached. Try again shortly.";
                case LiveFailure.ServiceDisabled:
                    return "The TopiaForge service has turned this off for now. Try again later.";
                case LiveFailure.ServiceRateLimited:
                    return "The TopiaForge service is turning away requests from this network for a moment. Try again shortly.";
                case LiveFailure.InviteNotFound:
                    return "That invite code does not work: it may be mistyped, expired or withdrawn, or its session may have ended.";
                case LiveFailure.ServerNotListed:
                    return "That server is not listed any more.";
                case LiveFailure.RelayUnavailable:
                    return hostAllowsDirect
                        ? "The relay is unavailable right now, and Hide my IP keeps this session on the relay, so it did not connect."
                        : "The host keeps its address hidden behind the relay, and the relay is unavailable right now.";
                case LiveFailure.UdpBlocked:
                    return "This network blocks the connections Internet play needs.";
                case LiveFailure.HostOffline:
                    return "The host is not online right now.";
                case LiveFailure.HostNotAnswering:
                    return "The host did not answer.";
                case LiveFailure.NoRoute:
                    return "No connection to the host could be made.";
                case LiveFailure.SignalingFailed:
                    return "The connection to the TopiaForge service was lost before the host could be reached.";
                case LiveFailure.InviteWithdrawn:
                    return "The host withdrew the invite, or the server stopped being public.";
                case LiveFailure.ServerBlocked:
                    return "The TopiaForge service is holding this server back for now.";
                case LiveFailure.ServiceRefused:
                    return "The TopiaForge service refused a request from this game; it may need an update.";
                case LiveFailure.InviteLimit:
                    return "This session already has as many invites as it may hold. Withdraw one, or wait for one to expire.";
                case LiveFailure.HostEnded:
                    return "The host ended the session.";
                case LiveFailure.Cancelled:
                    return string.Empty;
                default:
                    return "The host could not be reached.";
            }
        }
    }
}
