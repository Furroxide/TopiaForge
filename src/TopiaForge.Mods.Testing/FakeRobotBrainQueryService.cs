using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Controlled asynchronous structured-query fake.</summary>
    public sealed class FakeRobotBrainQueryService : IRobotBrainQueryService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<PendingQuery> pending = new List<PendingQuery>();
        private readonly Queue<OperationResult<BrainQueryResult>> queued =
            new Queue<OperationResult<BrainQueryResult>>();

        /// <summary>Creates a fake brain-query service.</summary>
        public FakeRobotBrainQueryService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public bool IsAvailable { get; set; } = true;

        /// <summary>Gets or sets whether calls complete synchronously from queued/default results.</summary>
        public bool AutoCompleteQueries { get; set; } = true;

        /// <summary>Gets the number of manually controlled pending queries.</summary>
        public int PendingQueryCount => pending.Count;

        /// <summary>Queues a successful structured response.</summary>
        public void EnqueueResult(IReadOnlyDictionary<string, string> values) =>
            queued.Enqueue(OperationResult<BrainQueryResult>.Success(new BrainQueryResult(values)));

        /// <summary>Queues a stable expected failure.</summary>
        public void EnqueueFailure(ModErrorCode errorCode, string message) =>
            queued.Enqueue(OperationResult<BrainQueryResult>.Failure(errorCode, message));

        /// <inheritdoc />
        public Task<OperationResult<BrainQueryResult>> QueryAsync(
            BrainQueryRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake brain query was cancelled."));
            }

            if (!IsAvailable)
            {
                return Task.FromResult(OperationResult<BrainQueryResult>.Failure(
                    ModErrorCode.Unavailable,
                    "The fake robot brain is unavailable."));
            }

            if (AutoCompleteQueries)
            {
                return Task.FromResult(queued.Count > 0 ? queued.Dequeue() : DefaultResult(request));
            }

            var operation = new PendingQuery();
            pending.Add(operation);
            operation.AttachCancellation(
                cancellationToken,
                lifetime.StoppingToken,
                value => pending.Remove(value));
            return operation.Task;
        }

        /// <summary>Completes the oldest pending query with queued or request-independent values.</summary>
        public bool CompleteNext(IReadOnlyDictionary<string, string> values)
        {
            return TryTake(out var operation) && operation.Complete(
                OperationResult<BrainQueryResult>.Success(new BrainQueryResult(values)));
        }

        /// <summary>Fails the oldest pending query with a stable expected error.</summary>
        public bool FailNext(ModErrorCode errorCode, string message)
        {
            return TryTake(out var operation) && operation.Complete(
                OperationResult<BrainQueryResult>.Failure(errorCode, message));
        }

        private bool TryTake(out PendingQuery operation)
        {
            while (pending.Count > 0)
            {
                operation = pending[0];
                pending.RemoveAt(0);
                if (!operation.Task.IsCompleted)
                {
                    return true;
                }
            }

            operation = null!;
            return false;
        }

        private static OperationResult<BrainQueryResult> DefaultResult(BrainQueryRequest request)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in request.Outputs)
            {
                values[field.Name] = field.AllowedStrings != null && field.AllowedStrings.Count > 0
                    ? field.AllowedStrings[0]
                    : string.Empty;
            }

            return OperationResult<BrainQueryResult>.Success(new BrainQueryResult(values));
        }

        private sealed class PendingQuery
        {
            private readonly TaskCompletionSource<OperationResult<BrainQueryResult>> completion =
                new TaskCompletionSource<OperationResult<BrainQueryResult>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private CancellationTokenRegistration callerCancellation;
            private CancellationTokenRegistration lifetimeCancellation;
            private Action<PendingQuery>? release;

            public void AttachCancellation(
                CancellationToken callerToken,
                CancellationToken lifetimeToken,
                Action<PendingQuery> completed)
            {
                release = completed;
                if (callerToken.CanBeCanceled)
                {
                    callerCancellation = callerToken.Register(Cancel);
                    if (Task.IsCompleted)
                    {
                        callerCancellation.Dispose();
                    }
                }

                if (!Task.IsCompleted && lifetimeToken.CanBeCanceled)
                {
                    lifetimeCancellation = lifetimeToken.Register(Cancel);
                    if (Task.IsCompleted)
                    {
                        lifetimeCancellation.Dispose();
                    }
                }
            }

            public Task<OperationResult<BrainQueryResult>> Task => completion.Task;

            public bool Complete(OperationResult<BrainQueryResult> result)
            {
                var changed = completion.TrySetResult(result);
                if (changed)
                {
                    callerCancellation.Dispose();
                    lifetimeCancellation.Dispose();
                    var completed = release;
                    release = null;
                    completed?.Invoke(this);
                }

                return changed;
            }

            private void Cancel() => Complete(OperationResult<BrainQueryResult>.Failure(
                ModErrorCode.Cancelled,
                "The fake brain query was cancelled."));
        }
    }
}
