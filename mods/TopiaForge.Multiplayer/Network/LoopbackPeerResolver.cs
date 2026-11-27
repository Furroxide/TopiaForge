using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>Development peers never resolve a name, use discovery or reach an external service.</summary>
    internal sealed class LoopbackPeerResolver : IJoinTargetResolver
    {
        private readonly IJoinTargetResolver inner;
        internal LoopbackPeerResolver(IJoinTargetResolver inner) { this.inner = inner; }
        public Task<ResolvedTarget> ResolveAsync(JoinTarget target, bool allowDirect, string? trustedServerId, CancellationToken cancellationToken) =>
            target.Kind == JoinTargetKind.Direct && string.Equals(target.Host, "127.0.0.1", StringComparison.Ordinal)
                ? inner.ResolveAsync(target, false, trustedServerId, cancellationToken)
                : Task.FromResult(ResolvedTarget.Failed(LiveFailure.DirectNotAllowed, "Development peers can join only their local host. Restart the dev loop."));
    }
}
