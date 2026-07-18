using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Async owner-cancellable adapter over the game's brain backend. No Task or native transport handle crosses
    // the public contract; consumers receive a stable OperationResult and the runtime supplies lifetime cancellation.
    internal sealed class RobotBrainQueryService : IRobotBrainQueryService,
        IOwnerBoundExtensionFactory, IDisposable
    {
        private const float HardTimeoutSeconds = 3f;
        private const int MaxConcurrent = 4;

        private readonly RoboApiClient client;
        private readonly IModLogger logger;
        private readonly CancellationTokenSource serviceCts = new CancellationTokenSource();
        private CancellationTokenSource sceneCts = new CancellationTokenSource();
        private int activeQueries;
        private bool disposed;
        private bool loggedAvailability;

        public RobotBrainQueryService(IModLogger logger)
        {
            this.logger = logger;
            var tokenPath = Path.Combine(Application.persistentDataPath, "robo_token.json");
            client = new RoboApiClient(tokenPath, Guid.NewGuid().ToString("N"), logger);
        }

        public bool IsAvailable => !disposed && client.HasToken;

        public async Task<OperationResult<BrainQueryResult>> QueryAsync(
            BrainQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (disposed)
            {
                return OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.InvalidState,
                    "RobotKit brain service has been disposed.");
            }

            if (!client.HasToken)
            {
                return OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.Unavailable,
                    "Robot brain credentials are unavailable.");
            }

            if (request.Outputs.Count == 0 || request.Outputs.Count > RoboApiProtocol.MaxOutputs)
            {
                return OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A brain query requires 1 to " + RoboApiProtocol.MaxOutputs + " output fields.");
            }

            if (Interlocked.Increment(ref activeQueries) > MaxConcurrent)
            {
                Interlocked.Decrement(ref activeQueries);
                return OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.Conflict,
                    "RobotKit already has the maximum number of brain queries in flight.");
            }

            try
            {
                LogAvailabilityOnce();
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    serviceCts.Token,
                    sceneCts.Token))
                {
                    return await client.Check3Async(request, HardTimeoutSeconds, linked.Token);
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeQueries);
            }
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(IRobotBrainQueryService))
            {
                throw new ArgumentException("Unsupported RobotKit brain extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, lifetime);
        }

        // Kept as a no-op pump so the provider's unified tick remains stable; query completion is Task-native now.
        public void Tick(float deltaTime)
        {
        }

        public void OnSceneChanged()
        {
            var previous = Interlocked.Exchange(ref sceneCts, new CancellationTokenSource());
            previous.Cancel();
            previous.Dispose();
            client.InvalidateToken();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            serviceCts.Cancel();
            sceneCts.Cancel();
            sceneCts.Dispose();
            serviceCts.Dispose();
        }

        private void LogAvailabilityOnce()
        {
            if (loggedAvailability)
            {
                return;
            }

            loggedAvailability = true;
            logger.Info("RobotKit: structured brain queries are available.");
        }

        private sealed class OwnerFacade : IRobotBrainQueryService
        {
            private readonly RobotBrainQueryService service;
            private readonly IModLifetime lifetime;

            public OwnerFacade(RobotBrainQueryService service, IModLifetime lifetime)
            {
                this.service = service;
                this.lifetime = lifetime;
            }

            public bool IsAvailable => !lifetime.IsStopping && service.IsAvailable;

            public async Task<OperationResult<BrainQueryResult>> QueryAsync(
                BrainQueryRequest request,
                CancellationToken cancellationToken = default)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.StoppingToken))
                {
                    return await service.QueryAsync(request, linked.Token);
                }
            }
        }
    }
}
