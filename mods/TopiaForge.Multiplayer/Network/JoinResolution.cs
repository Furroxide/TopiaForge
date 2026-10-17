using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.ControlPlane;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// A join target resolved to its host's verified descriptor and a way there: a path for a LAN or direct target, or the
    /// service's route for an invite or a listed server, whose path the NAT layer finds later. Or the reason it could not be.
    /// </summary>
    internal sealed class ResolvedTarget
    {
        private ResolvedTarget(LiveFailure failure, string detail, TransportPath? path, SignedServerDescriptor? descriptor, string? servedServerId, ServiceRoute? route = null)
        {
            Failure = failure;
            Detail = detail;
            Path = path;
            Descriptor = descriptor;
            ServedServerId = servedServerId;
            Route = route;
        }

        internal LiveFailure Failure { get; }
        internal string Detail { get; }
        internal TransportPath? Path { get; }
        internal SignedServerDescriptor? Descriptor { get; }

        /// <summary>Gets how the TopiaForge service reaches the server, for an invite or a listed server; such a target has no path.</summary>
        internal ServiceRoute? Route { get; }

        /// <summary>Gets, for a changed identity, the server the address serves now: the one the player may choose to trust.</summary>
        internal string? ServedServerId { get; }

        /// <summary>Gets the delay the TopiaForge service named when it refused the lookup, in seconds, or zero.</summary>
        internal int RetryAfterSeconds { get; private set; }

        internal static ResolvedTarget Found(TransportPath path, SignedServerDescriptor descriptor) =>
            new ResolvedTarget(LiveFailure.None, string.Empty, path ?? throw new ArgumentNullException(nameof(path)), descriptor ?? throw new ArgumentNullException(nameof(descriptor)), null);

        internal static ResolvedTarget Failed(LiveFailure failure, string detail, int? retryAfterSeconds = null) =>
            new ResolvedTarget(failure, detail, null, null, null) { RetryAfterSeconds = retryAfterSeconds ?? 0 };

        internal static ResolvedTarget ThroughService(SignedServerDescriptor descriptor, ServiceRoute route) =>
            new ResolvedTarget(LiveFailure.None, string.Empty, null, descriptor ?? throw new ArgumentNullException(nameof(descriptor)), null,
                route ?? throw new ArgumentNullException(nameof(route)));

        internal static ResolvedTarget IdentityChanged(string servedServerId) =>
            new ResolvedTarget(LiveFailure.HostIdentityChanged, "This address served a different server last time.", null, null, servedServerId);
    }

    /// <summary>Finds where a target is and fetches what it says it is, off the main thread.</summary>
    internal interface IJoinTargetResolver
    {
        /// <summary>
        /// Resolves <paramref name="target"/>. Unless <paramref name="allowDirect"/>, nothing beyond the local network
        /// is contacted, a name server included: such a host fails with <see cref="LiveFailure.DirectNotAllowed"/>
        /// before anything is sent. A typed address that served another server before is trusted only when the server
        /// it serves now is <paramref name="trustedServerId"/>.
        /// </summary>
        Task<ResolvedTarget> ResolveAsync(JoinTarget target, bool allowDirect, string? trustedServerId, CancellationToken cancellationToken);
    }
}
