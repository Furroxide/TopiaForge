using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.RobotKit
{
    // Publishes IRobotBrainQueryService: starts a /agent/check3 brain query off the main thread and marshals its
    // result back onto the service tick, so consumers poll a handle and never touch threads or the network. Mirrors
    // the ReachableSpawnSearch lifecycle (tick-driven, cancel-on-dispose). A small concurrency cap protects the
    // metered backend from a runaway caller; excess requests complete immediately as unavailable so the consumer's
    // deterministic fallback stands.
    internal sealed class RobotBrainQueryService : IRobotBrainQueryService, IDisposable
    {
        private const float HardTimeoutSeconds = 3f;
        private const int MaxConcurrent = 4;

        private readonly RoboApiClient client;
        private readonly CancellationTokenSource serviceCts = new CancellationTokenSource();
        private readonly List<PendingQuery> pending = new List<PendingQuery>();
        private readonly IModLogger logger;

        private bool disposed;
        private bool loggedAvailability;

        public RobotBrainQueryService(IModLogger logger)
        {
            this.logger = logger;

            // Application.persistentDataPath must be read on the main thread (it is here, at mod load); the resolved
            // path string is then safe to use from the background HTTP task.
            var tokenPath = Path.Combine(Application.persistentDataPath, "robo_token.json");
            client = new RoboApiClient(tokenPath, Guid.NewGuid().ToString("N"), logger);
        }

        public bool IsAvailable => !disposed && client.HasToken;

        public IRobotBrainQuery BeginQuery(BrainQueryRequest request)
        {
            var handle = new PendingQuery();
            if (disposed || request == null || !client.HasToken || pending.Count >= MaxConcurrent)
            {
                handle.CompleteUnavailable();
                return handle;
            }

            // The backend rejects a request with more than RoboApiProtocol.MaxOutputs fields as a whole-turn 400
            // ("Too many outputs"), which otherwise degrades opaquely to "unavailable". Warn loudly so a consumer
            // that over-requests fields sees the cause instead of a silent brain-offline symptom.
            if (request.Outputs != null && request.Outputs.Count > RoboApiProtocol.MaxOutputs)
            {
                logger.Warn("RobotKit brain query has " + request.Outputs.Count + " output fields; the backend caps "
                    + "a request at " + RoboApiProtocol.MaxOutputs + " and will reject this turn. Reduce the "
                    + "request's fields (a conversation may add at most " + (RoboApiProtocol.MaxOutputs - 2)
                    + " ExtraOutputs beyond reply+decision).");
            }

            try
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceCts.Token);
                handle.Cts = cts;
                handle.Task = client.Check3Async(request, HardTimeoutSeconds, cts.Token);
                pending.Add(handle);
                LogAvailabilityOnce();
            }
            catch (Exception ex)
            {
                logger.Debug("RobotKit brain query could not start: " + ex.Message);
                handle.Cts?.Dispose();
                handle.Cts = null;
                handle.CompleteUnavailable();
            }

            return handle;
        }

        // Drain finished queries onto the main thread. Check3Async never throws, so a completed task carries a result;
        // the defensive catch covers a cancelled/faulted task all the same.
        public void Tick(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var query = pending[index];
                var task = query.Task;
                if (task == null || !task.IsCompleted)
                {
                    continue;
                }

                BrainQueryResult result;
                try
                {
                    result = task.Status == TaskStatus.RanToCompletion ? task.Result : BrainQueryResult.Unavailable;
                }
                catch (Exception ex)
                {
                    logger.Debug("RobotKit brain query completion failed: " + ex.Message);
                    result = BrainQueryResult.Unavailable;
                }

                query.CompleteWith(result);
                query.Cts?.Dispose();
                pending.RemoveAt(index);
            }
        }

        public void OnSceneChanged()
        {
            // The signed-in user (and therefore the token) may change between scenes. Old-scene requests cannot
            // have a valid consumer after the robots are gone, so cancel them rather than occupying the cap.
            for (var index = 0; index < pending.Count; index++)
            {
                CancelPending(pending[index], "scene change");
            }

            pending.Clear();
            client.InvalidateToken();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                serviceCts.Cancel();
            }
            catch (Exception ex)
            {
                logger.Debug("RobotKit brain service cancellation failed during unload: " + ex.Message);
            }

            foreach (var query in pending)
            {
                CancelPending(query, "unload");
            }

            pending.Clear();
            serviceCts.Dispose();
        }

        private void CancelPending(PendingQuery query, string reason)
        {
            try
            {
                query.Cts?.Cancel();
            }
            catch (Exception ex)
            {
                logger.Debug("RobotKit brain query cancellation failed during " + reason + ": " + ex.Message);
            }

            try
            {
                query.Cts?.Dispose();
            }
            catch (Exception ex)
            {
                logger.Debug("RobotKit brain query cleanup failed during " + reason + ": " + ex.Message);
            }

            query.Cts = null;
            query.CompleteUnavailable();
        }

        private void LogAvailabilityOnce()
        {
            if (loggedAvailability)
            {
                return;
            }

            loggedAvailability = true;
            logger.Info("RobotKit: brain queries enabled — robot decisions can consult the RoboAPI backend (llama-3.3-70b).");
        }

        // The pollable handle. Its result is written by the tick (main thread) and read by the consumer (main thread),
        // so no cross-thread state escapes the Task itself.
        private sealed class PendingQuery : IRobotBrainQuery
        {
            private BrainQueryResult result = BrainQueryResult.Unavailable;
            private bool complete;

            public Task<BrainQueryResult>? Task { get; set; }

            public CancellationTokenSource? Cts { get; set; }

            public bool IsComplete => complete;

            public bool Found => complete && result.Succeeded;

            public BrainQueryResult Result => result;

            public void CompleteWith(BrainQueryResult value)
            {
                result = value ?? BrainQueryResult.Unavailable;
                complete = true;
            }

            public void CompleteUnavailable()
            {
                result = BrainQueryResult.Unavailable;
                complete = true;
            }
        }
    }
}
