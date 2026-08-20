using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Async owner-cancellable adapter over the game's brain backend. No Task or native transport handle crosses
    // the public contract; consumers receive a stable OperationResult and the runtime supplies lifetime cancellation.
    //
    // Reaches Tomato Cake's RoboAPI, which TopiaForge has no authorization for and no control over; they may restrict
    // or withdraw it at any time. Off by default, and every consumer must stay fully playable on the unavailable
    // result. See the warning on RoboApiClient and the P0-PRIV-01 gate.
    internal sealed class RobotBrainQueryService : IRobotBrainQueryService,
        IOwnerBoundExtensionFactory, IDisposable
    {
        private const float HardTimeoutSeconds = 3f;
        private const int MaxConcurrent = 4;

        private readonly RoboApiClient client;
        private readonly IModLogger logger;
        private readonly Func<IPromptOverrideRegistry?>? promptRegistryResolver;
        private readonly CancellationTokenSource serviceCts = new CancellationTokenSource();
        private CancellationTokenSource sceneCts = new CancellationTokenSource();
        private int activeQueries;
        private int loggedDirectiveOverflow;
        private int loggedDirectiveResolutionFailure;
        private bool disposed;
        private bool loggedAvailability;

        public RobotBrainQueryService(
            IModLogger logger,
            Func<IPromptOverrideRegistry?>? promptRegistryResolver = null)
        {
            this.logger = logger;
            this.promptRegistryResolver = promptRegistryResolver;
            client = new RoboApiClient(
                Application.persistentDataPath, Guid.NewGuid().ToString("N"), logger);
        }

        // Probed by RuntimeCapabilityProbe on every load and scene change, so this must not read or cache the
        // player's credential. See the note on RoboApiClient.HasTokenFile.
        public bool IsAvailable => !disposed && client.HasTokenFile;

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
                    var effectiveRequest = ApplyGlobalRobotDirective(request);
                    return await client.Check3Async(effectiveRequest, HardTimeoutSeconds, linked.Token);
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
            // Mono never unloads the assembly, so a cached player token would otherwise stay resident for the
            // rest of the process after the mod is unloaded. Drop it with everything else this service owns.
            client.InvalidateToken();
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

        private BrainQueryRequest ApplyGlobalRobotDirective(BrainQueryRequest request)
        {
            var effectiveRequest = BrainQueryDirectiveComposer.ApplyFromRegistry(
                request,
                promptRegistryResolver,
                out var exceededPromptLimit,
                out var resolutionFailure);
            if (resolutionFailure != null
                && Interlocked.Exchange(ref loggedDirectiveResolutionFailure, 1) == 0)
            {
                logger.Warn("RobotKit could not resolve the optional global robot directive: "
                    + resolutionFailure.Message);
            }

            if (exceededPromptLimit && Interlocked.Exchange(ref loggedDirectiveOverflow, 1) == 0)
            {
                logger.Warn("RobotKit skipped the global robot directive because the combined "
                    + "/agent/check3 prompt would exceed "
                    + BrainQueryDirectiveComposer.MaxPromptChars
                    + " characters.");
            }

            return effectiveRequest;
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
