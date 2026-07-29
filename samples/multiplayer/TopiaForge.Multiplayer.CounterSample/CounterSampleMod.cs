using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.CounterSample
{
    /// <summary>Dogfoods generated session state, commands, prediction, and accepted presentation events.</summary>
    [MultiplayerContract(Id = "io.github.furroxide.topiaforge.samples.counter")]
    public sealed partial class CounterSampleMod : TopiaForgeMod
    {
        [ReplicatedState("counter")]
        private ReplicatedState<CounterState> counter =
            new ReplicatedState<CounterState>(new CounterState());

        /// <summary>Gets the last canonically accepted presentation value observed by this process.</summary>
        public int LastPresentedValue { get; private set; }

        /// <inheritdoc/>
        protected override void OnLoad()
        {
            var binding = BindMultiplayer(Context.RequireMultiplayer());
            if (!binding.TryGetValue(out var lease))
            {
                throw new InvalidOperationException(binding.ErrorMessage);
            }

            Context.Lifetime.Track(lease);
            Context.Logger.Info("Multiplayer counter sample registered its generated contract.");
        }

        [MultiplayerCommand(
            "increment",
            Prediction = PredictionMode.Owner,
            MaximumPerSecond = 20,
            MaximumPayloadBytes = 512)]
        private OperationResult<CounterResponse> Increment(
            MultiplayerCommandContext command,
            CounterRequest request)
        {
            if (request.Amount < 1 || request.Amount > 10)
            {
                return OperationResult<CounterResponse>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Amount must be between 1 and 10.");
            }

            var updated = counter.Update(current =>
                OperationResult<CounterState>.Success(
                    new CounterState
                    {
                        Value = checked(current.Value + request.Amount),
                        LastSender = command.SenderId.Value
                    }));
            if (!updated.TryGetValue(out var state))
            {
                return OperationResult<CounterResponse>.Failure(
                    updated.ErrorCode,
                    updated.ErrorMessage);
            }

            EmitOnIncrementAccepted(
                command,
                new CounterAcceptedEvent { Value = state.Value },
                MultiplayerAudience.Everyone);
            return OperationResult<CounterResponse>.Success(
                new CounterResponse { Value = state.Value });
        }

        [PresentationEvent("increment-accepted")]
        private void OnIncrementAccepted(CounterAcceptedEvent value)
        {
            LastPresentedValue = value.Value;
        }
    }

    public sealed class CounterState
    {
        public int Value { get; set; }

        [NetworkBound(128)]
        public string LastSender { get; set; } = string.Empty;
    }

    public sealed class CounterRequest
    {
        public int Amount { get; set; }
    }

    public sealed class CounterResponse
    {
        public int Value { get; set; }
    }

    public sealed class CounterAcceptedEvent
    {
        public int Value { get; set; }
    }
}
