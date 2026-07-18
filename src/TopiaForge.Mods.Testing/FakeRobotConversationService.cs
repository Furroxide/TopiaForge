using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Queued deterministic conversation service.</summary>
    public sealed class FakeRobotConversationService : IRobotConversationService
    {
        private readonly FakeModLifetime lifetime;
        private readonly Queue<OperationResult<RobotConversationTurnResult>> turns =
            new Queue<OperationResult<RobotConversationTurnResult>>();

        /// <summary>Creates a fake conversation service.</summary>
        public FakeRobotConversationService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public bool IsAvailable { get; set; } = true;

        /// <summary>Gets the number of active conversations.</summary>
        public int ActiveConversationCount { get; private set; }

        /// <summary>Queues one successful turn for the next submit call.</summary>
        public void EnqueueTurn(
            string reply,
            string decision,
            IReadOnlyDictionary<string, string>? values = null) =>
            turns.Enqueue(OperationResult<RobotConversationTurnResult>.Success(
                new RobotConversationTurnResult(
                    reply,
                    decision,
                    values ?? new Dictionary<string, string>())));

        /// <summary>Queues one failed turn for the next submit call.</summary>
        public void EnqueueFailure(ModErrorCode errorCode, string message) =>
            turns.Enqueue(OperationResult<RobotConversationTurnResult>.Failure(errorCode, message));

        /// <inheritdoc />
        public OperationResult<IRobotConversation> BeginConversation(RobotConversationRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!IsAvailable)
            {
                return OperationResult<IRobotConversation>.Failure(
                    ModErrorCode.Unavailable,
                    "The fake conversation service is unavailable.");
            }

            var conversation = new Conversation(this, request.MaxTurns, () => ActiveConversationCount--);
            ActiveConversationCount++;
            return lifetime.TrackResult<IRobotConversation>(
                conversation,
                conversation.AttachLifetimeLease,
                "The fake mod stopped before the conversation could begin.");
        }

        private OperationResult<RobotConversationTurnResult> TakeTurn()
        {
            return turns.Count > 0
                ? turns.Dequeue()
                : OperationResult<RobotConversationTurnResult>.Failure(
                    ModErrorCode.NotFound,
                    "No fake conversation turn is queued.");
        }

        private sealed class Conversation : IRobotConversation
        {
            private readonly FakeRobotConversationService service;
            private Action? release;
            private IDisposable? lifetimeLease;

            public Conversation(FakeRobotConversationService service, int maxTurns, Action release)
            {
                this.service = service;
                MaxTurns = maxTurns;
                this.release = release;
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public bool IsEnded => release == null || TurnCount >= MaxTurns;
            public int TurnCount { get; private set; }
            public int MaxTurns { get; }

            public Task<OperationResult<RobotConversationTurnResult>> SubmitAsync(
                string playerText,
                CancellationToken cancellationToken = default)
            {
                if (cancellationToken.IsCancellationRequested ||
                    service.lifetime.StoppingToken.IsCancellationRequested)
                {
                    return Task.FromResult(OperationResult<RobotConversationTurnResult>.Failure(
                        ModErrorCode.Cancelled,
                        "The fake conversation turn was cancelled."));
                }

                if (IsEnded)
                {
                    return Task.FromResult(OperationResult<RobotConversationTurnResult>.Failure(
                        ModErrorCode.InvalidState,
                        "The fake conversation has ended."));
                }

                var result = service.TakeTurn();
                if (result.Succeeded)
                {
                    TurnCount++;
                    if (TurnCount >= MaxTurns)
                    {
                        Dispose();
                    }
                }

                return Task.FromResult(result);
            }

            public void Dispose()
            {
                var callback = release;
                release = null;
                try
                {
                    callback?.Invoke();
                }
                finally
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }
}
