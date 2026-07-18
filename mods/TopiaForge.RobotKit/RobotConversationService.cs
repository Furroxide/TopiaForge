using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    internal sealed class RobotConversationService : IRobotConversationService,
        IOwnerBoundExtensionFactory, IDisposable
    {
        private readonly IRobotBrainQueryService brains;
        private readonly List<RobotConversation> active = new List<RobotConversation>();
        private bool disposed;

        public RobotConversationService(IRobotBrainQueryService brains, IModLogger logger)
        {
            this.brains = brains ?? throw new ArgumentNullException(nameof(brains));
        }

        public bool IsAvailable => !disposed && brains.IsAvailable;

        public OperationResult<IRobotConversation> BeginConversation(RobotConversationRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (disposed)
            {
                return OperationResult<IRobotConversation>.Failure(
                    ModErrorCode.InvalidState,
                    "RobotKit conversation service has been disposed.");
            }

            if (!brains.IsAvailable)
            {
                return OperationResult<IRobotConversation>.Failure(
                    ModErrorCode.Unavailable,
                    "Robot brain conversations are unavailable.");
            }

            var conversation = new RobotConversation(brains, request, Remove);
            active.Add(conversation);
            return OperationResult<IRobotConversation>.Success(conversation);
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(IRobotConversationService))
            {
                throw new ArgumentException("Unsupported RobotKit conversation extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, lifetime);
        }

        public void Tick(float deltaTime)
        {
        }

        public void OnSceneChanged()
        {
            DisposeActive();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            DisposeActive();
        }

        private void Remove(RobotConversation conversation)
        {
            active.Remove(conversation);
        }

        private void DisposeActive()
        {
            var snapshot = active.ToArray();
            active.Clear();
            foreach (var conversation in snapshot)
            {
                conversation.Dispose();
            }
        }

        private sealed class OwnerFacade : IRobotConversationService
        {
            private readonly RobotConversationService service;
            private readonly IModLifetime lifetime;

            public OwnerFacade(RobotConversationService service, IModLifetime lifetime)
            {
                this.service = service;
                this.lifetime = lifetime;
            }

            public bool IsAvailable => !lifetime.IsStopping && service.IsAvailable;

            public OperationResult<IRobotConversation> BeginConversation(RobotConversationRequest request)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<IRobotConversation>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot begin a robot conversation.");
                }

                var result = service.BeginConversation(request);
                if (!result.TryGetValue(out var conversation))
                {
                    return result;
                }

                try
                {
                    return OperationResult<IRobotConversation>.Success(
                        new OwnerConversation(conversation, lifetime.Track(conversation)));
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IRobotConversation>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before its robot conversation could be retained.");
                }
            }

            private sealed class OwnerConversation : IRobotConversation
            {
                private readonly IRobotConversation conversation;
                private IDisposable? lifetimeLease;

                public OwnerConversation(IRobotConversation conversation, IDisposable lifetimeLease)
                {
                    this.conversation = conversation;
                    this.lifetimeLease = lifetimeLease;
                }

                public bool IsEnded => lifetimeLease == null || conversation.IsEnded;
                public int TurnCount => conversation.TurnCount;
                public int MaxTurns => conversation.MaxTurns;

                public Task<OperationResult<RobotConversationTurnResult>> SubmitAsync(
                    string playerText,
                    CancellationToken cancellationToken = default) =>
                    conversation.SubmitAsync(playerText, cancellationToken);

                public void Dispose()
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }

    internal sealed class RobotConversation : IRobotConversation
    {
        private readonly IRobotBrainQueryService brains;
        private readonly RobotConversationRequest config;
        private readonly List<ConversationTurn> history = new List<ConversationTurn>();
        private readonly CancellationTokenSource lifetimeCts = new CancellationTokenSource();
        private readonly Action<RobotConversation> onDisposed;
        private int disposed;
        private int inFlight;
        private bool ended;
        private int turnCount;

        public RobotConversation(
            IRobotBrainQueryService brains,
            RobotConversationRequest config,
            Action<RobotConversation> onDisposed)
        {
            this.brains = brains;
            this.config = config;
            this.onDisposed = onDisposed;
        }

        public bool IsEnded => ended;
        public int TurnCount => turnCount;
        public int MaxTurns => Math.Max(1, config.MaxTurns);

        public async Task<OperationResult<RobotConversationTurnResult>> SubmitAsync(
            string playerText,
            CancellationToken cancellationToken = default)
        {
            if (ended)
            {
                return OperationResult<RobotConversationTurnResult>.Failure(
                    ModErrorCode.InvalidState,
                    "The robot conversation has ended.");
            }

            var sanitized = ConversationPrompt.Sanitize(playerText);
            if (sanitized.Length == 0)
            {
                return OperationResult<RobotConversationTurnResult>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A non-empty player line is required.");
            }

            if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0)
            {
                return OperationResult<RobotConversationTurnResult>.Failure(
                    ModErrorCode.Conflict,
                    "This robot conversation already has a turn in flight.");
            }

            try
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetimeCts.Token))
                {
                    var result = await brains.QueryAsync(
                        ConversationPrompt.BuildRequest(config, history, sanitized),
                        linked.Token);
                    if (!result.TryGetValue(out var brainResult))
                    {
                        return OperationResult<RobotConversationTurnResult>.Failure(
                            result.ErrorCode,
                            result.ErrorMessage);
                    }

                    brainResult.TryGet(ConversationPrompt.ReplyField, out var reply);
                    brainResult.TryGet(ConversationPrompt.DecisionField, out var decision);
                    var maximumReply = config.MaxReplyChars > 0 ? config.MaxReplyChars : 200;
                    if (reply.Length > maximumReply)
                    {
                        reply = reply.Substring(0, maximumReply);
                    }

                    history.Add(new ConversationTurn(sanitized, reply, decision));
                    turnCount++;
                    if (turnCount >= MaxTurns)
                    {
                        ended = true;
                    }

                    return OperationResult<RobotConversationTurnResult>.Success(
                        new RobotConversationTurnResult(reply, decision, brainResult.Values));
                }
            }
            finally
            {
                Interlocked.Exchange(ref inFlight, 0);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            ended = true;
            lifetimeCts.Cancel();
            lifetimeCts.Dispose();
            onDisposed(this);
        }
    }
}
