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
                catch
                {
                    result = BrainQueryResult.Unavailable;
                }

                query.CompleteWith(result);
                query.Cts?.Dispose();
                pending.RemoveAt(index);
            }
        }

        public void OnSceneChanged()
        {
            // The signed-in user (and therefore the token) may change between scenes; re-read it next access.
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
            catch
            {
            }

            foreach (var query in pending)
            {
                query.CompleteUnavailable();
                query.Cts?.Dispose();
            }

            pending.Clear();
            serviceCts.Dispose();
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
