using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// How the TopiaForge service reaches a server it found: the server, the invite it was found with, the host's privacy
    /// choice from its listing, and whether the relay was available when the service was asked. It names no address: the
    /// path to the host is found later, by the NAT layer, and never comes back up.
    /// </summary>
    internal sealed class ServiceRoute
    {
        internal ServiceRoute(string serverId, string? inviteCode, bool hostRelayOnly, bool relayAvailable)
        {
            ServerId = serverId ?? throw new ArgumentNullException(nameof(serverId));
            InviteCode = inviteCode;
            HostRelayOnly = hostRelayOnly;
            RelayAvailable = relayAvailable;
        }

        internal string ServerId { get; }

        /// <summary>Gets the invite's canonical code, for a server found by invite; the join ticket is asked for with it.</summary>
        internal string? InviteCode { get; }

        internal bool HostRelayOnly { get; }

        /// <summary>Gets whether the service's status offered relay credentials: the relay on and the kill switch released.</summary>
        internal bool RelayAvailable { get; }

        /// <summary>Never prints the invite's code.</summary>
        public override string ToString() => "service route";
    }

    /// <summary>
    /// Resolves invites and listed servers through the TopiaForge service, and hands every other target to the resolver
    /// for LAN and direct play. The service is asked first for its status, so a join learns at once that Internet joining
    /// is off or that the relay is unavailable; then for the invite or the server. The answer's descriptor has already
    /// been verified against its server id when it arrives here. Without a service, an invite or a listed server fails as
    /// not configured, and nothing is contacted.
    /// </summary>
    internal sealed class ServiceAwareResolver : IJoinTargetResolver
    {
        private readonly IJoinTargetResolver inner;
        private readonly Func<InternetServices?> services;

        internal ServiceAwareResolver(IJoinTargetResolver inner, Func<InternetServices?> services)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public Task<ResolvedTarget> ResolveAsync(JoinTarget target, bool allowDirect, string? trustedServerId, CancellationToken cancellationToken)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (!target.NeedsService) return inner.ResolveAsync(target, allowDirect, trustedServerId, cancellationToken);
            var current = services();
            if (current == null)
            {
                return Task.FromResult(ResolvedTarget.Failed(LiveFailure.ServiceNotConfigured, InternetFailures.Detail(LiveFailure.ServiceNotConfigured)));
            }

            return ResolveThroughServiceAsync(current.Client, target, cancellationToken);
        }

        internal static async Task<ResolvedTarget> ResolveThroughServiceAsync(ControlPlaneClient client, JoinTarget target, CancellationToken cancellationToken)
        {
            var invite = target.Kind == JoinTargetKind.Invite;
            var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.Succeeded) return Failed(InternetFailures.OfService(status.Failure), status.RetryAfterSeconds);
            if (!status.Value!.Signaling || (invite && !status.Value.Invites)) return Failed(LiveFailure.ServiceDisabled);
            var found = invite
                ? await client.ResolveInviteAsync(target.InviteCode!, cancellationToken).ConfigureAwait(false)
                : await client.GetServerAsync(target.ServerId!, cancellationToken).ConfigureAwait(false);
            if (!found.Succeeded) return Failed(InternetFailures.OfService(found.Failure, invite), found.RetryAfterSeconds);
            var server = found.Value!;

            // An invite a session launch pinned to the server the launcher verified must still lead there.
            if (target.ServerId != null && !string.Equals(server.ServerId, target.ServerId, StringComparison.Ordinal))
            {
                return ResolvedTarget.Failed(LiveFailure.DescriptorInvalid, "The invite now leads to a different server than the one checked before.");
            }

            return ResolvedTarget.ThroughService(server.Descriptor, new ServiceRoute(server.ServerId, invite ? target.InviteCode : null, server.HostRelayOnly, status.Value.RelayAvailable));
        }

        private static ResolvedTarget Failed(LiveFailure failure, int? retryAfterSeconds = null) =>
            ResolvedTarget.Failed(failure, InternetFailures.Detail(failure), retryAfterSeconds);
    }
}
