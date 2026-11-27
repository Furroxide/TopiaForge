using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
{
    // Structured autonomous queries use this facade. Conversations receive the local service directly;
    // neither path sends credentials, prompts, audio or transcripts through replication.
    internal sealed class OwnerRobotBrainQueries : IRobotBrainQueryService
    {
        private readonly IRobotBrainQueryService service;
        private readonly IModLifetime lifetime;
        private readonly Func<bool> authoritative;

        internal OwnerRobotBrainQueries(IRobotBrainQueryService service, IModLifetime lifetime, Func<bool> authoritative)
        { this.service = service; this.lifetime = lifetime; this.authoritative = authoritative; }

        public bool IsAvailable => authoritative() && !lifetime.IsStopping && service.IsAvailable;

        public async Task<OperationResult<BrainQueryResult>> QueryAsync(BrainQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            // Check before even probing the local token store.
            if (!authoritative()) return OperationResult<BrainQueryResult>.Failure(ModErrorCode.NotAuthoritative,
                "Autonomous robot queries run on the host. Use a local conversation for dialogue.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.StoppingToken);
            return await service.QueryAsync(request, linked.Token);
        }
    }
}
