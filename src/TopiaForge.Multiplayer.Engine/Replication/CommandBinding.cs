using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>One registered command handler with its bounds, prediction mode and per-sender rate limits.</summary>
    internal abstract class CommandBinding : IMultiplayerCommandRegistration
    {
        private readonly Dictionary<ParticipantId, SlidingWindowRateLimiter> limiters =
            new Dictionary<ParticipantId, SlidingWindowRateLimiter>();

        protected CommandBinding(SessionEngine engine, WireKey key, PredictionMode prediction, int maximumPerSecond, int maximumPayloadBytes)
        {
            Engine = engine;
            Key = key;
            Prediction = prediction;
            MaximumPerSecond = maximumPerSecond;
            MaximumPayloadBytes = maximumPayloadBytes;
        }

        internal SessionEngine Engine { get; }
        internal WireKey Key { get; }
        internal PredictionMode Prediction { get; }
        internal int MaximumPerSecond { get; }
        internal int MaximumPayloadBytes { get; }

        public string Id => Key.Id;

        public bool IsActive { get; internal set; } = true;

        public void Dispose() => Engine.UnregisterCommand(this);

        internal bool TryConsumeRate(ParticipantId sender, long nowMicros)
        {
            if (!limiters.TryGetValue(sender, out var limiter))
            {
                limiter = new SlidingWindowRateLimiter(MaximumPerSecond, 1_000_000);
                limiters.Add(sender, limiter);
            }

            return limiter.TryAcquire(nowMicros);
        }

        internal void ResetForNewSession() => limiters.Clear();

        /// <summary>Decodes the request, runs the handler inside <paramref name="scope"/>, and encodes the response.</summary>
        internal abstract WireOutcome Invoke(ParticipantId sender, ulong sequence, byte[] requestBytes, TransactionScope scope);
    }

    internal sealed class CommandBinding<TRequest, TResponse> : CommandBinding
        where TRequest : class
        where TResponse : class
    {
        internal CommandBinding(SessionEngine engine, WireKey key, MultiplayerCommandDefinition<TRequest, TResponse> definition)
            : base(engine, key, definition.Prediction, definition.MaximumPerSecond, definition.MaximumPayloadBytes)
        {
            Definition = definition;
        }

        internal MultiplayerCommandDefinition<TRequest, TResponse> Definition { get; }

        internal override WireOutcome Invoke(ParticipantId sender, ulong sequence, byte[] requestBytes, TransactionScope scope)
        {
            var request = EngineCodec.Decode(Definition.RequestCodec, requestBytes);
            if (!request.TryGetValue(out var requestValue)) return WireOutcome.Failure(request.ErrorCode, request.ErrorMessage);
            var context = new MultiplayerCommandContext(
                sender,
                Engine.CurrentTick,
                sequence,
                Engine.Seed,
                Engine.CurrentSessionToken,
                scope.CreateEmitter(Key.Owner, sender));
            OperationResult<TResponse> result;
            try
            {
                result = Definition.Handler(context, requestValue) ??
                    OperationResult<TResponse>.Failure(ModErrorCode.Unknown, "The multiplayer command handler returned no result.");
            }
            catch (Exception exception)
            {
                result = EngineCodec.FromException<TResponse>(exception, "The multiplayer command handler failed.");
            }

            if (!result.TryGetValue(out var response)) return WireOutcome.Failure(result.ErrorCode, result.ErrorMessage);
            var encoded = EngineCodec.Encode(Definition.ResponseCodec, response);
            return encoded.TryGetValue(out var bytes)
                ? WireOutcome.Success(bytes)
                : WireOutcome.Failure(encoded.ErrorCode, encoded.ErrorMessage);
        }

        internal OperationResult<TResponse> DecodeResponse(WireOutcome outcome)
        {
            if (!outcome.Succeeded) return OperationResult<TResponse>.Failure(outcome.Code, outcome.Message);
            return EngineCodec.Decode(Definition.ResponseCodec, outcome.Payload!);
        }
    }
}
