using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.SdkAcceptance
{
    [MultiplayerContract(Id = "dev.topiaforge.sdk-acceptance.loopback")]
    public sealed partial class SdkAcceptanceMod
    {
        [ReplicatedState("loopback-state")]
        private ReplicatedState<AcceptanceMultiplayerState> multiplayerProbeState =
            new ReplicatedState<AcceptanceMultiplayerState>(new AcceptanceMultiplayerState());

        private int multiplayerPresentedValue;

        // The standalone loopback provider confirms inline today, but a submitted command is asynchronous by
        // contract and live transport will not. Waiting on it would hang the game the moment that changes, so
        // the acceptance mod demonstrates the supported drain: keep the task and poll it per frame.
        private Task<MultiplayerCommandConfirmation<AcceptanceMultiplayerResponse>>? loopbackConfirmation;

        private void RunMultiplayerLoopbackChecks()
        {
            if (!Context.TryGetMultiplayer(out var multiplayer) || multiplayer == null)
            {
                Fail("integration.multiplayer-loopback", "the declared multiplayer provider did not resolve");
                return;
            }

            var session = multiplayer.Snapshot;
            var localParticipant = session.LocalParticipantId;
            var standaloneSides = MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server;
            var localParticipantReady = false;
            if (localParticipant.HasValue)
            {
                foreach (var participant in session.Participants)
                {
                    if (participant.Id.Equals(localParticipant.Value)
                        && participant.IsConnected
                        && participant.IsLocal)
                    {
                        localParticipantReady = true;
                        break;
                    }
                }
            }

            if (session.State != MultiplayerSessionState.Ready
                || session.ProcessKind != MultiplayerProcessKind.Interactive
                || session.ExecutionSides != standaloneSides
                || !session.HasPresentation
                || !localParticipantReady)
            {
                Fail("integration.multiplayer-loopback", "the standalone loopback session contract was incomplete");
                return;
            }

            var binding = BindMultiplayer(multiplayer);
            if (!binding.TryGetValue(out var lease))
            {
                Fail("integration.multiplayer-loopback", "generated contract registration failed: " + binding.ErrorMessage);
                return;
            }

            Context.Lifetime.Track(lease);

            // Submit, then drain per frame. Waiting on the returned task would block the main thread that the
            // provider needs in order to complete it — the loopback provider confirms inline today, but live
            // transport will not, and a blocking wait hangs the game outright (see TF1008).
            loopbackConfirmation = SubmitProbeLoopbackAsync(new AcceptanceMultiplayerRequest { Delta = 1 });
            Context.Lifetime.Track(Context.Events.SubscribeUpdate(_ =>
            {
                if (loopbackConfirmation == null || !loopbackConfirmation.IsCompleted)
                {
                    return;
                }

                var finished = loopbackConfirmation;
                loopbackConfirmation = null;

                MultiplayerCommandConfirmation<AcceptanceMultiplayerResponse> confirmation;
                try
                {
                    confirmation = finished.GetAwaiter().GetResult();
                }
                catch (System.Exception exception)
                {
                    Fail(
                        "integration.multiplayer-loopback",
                        "the generated command faulted: " + exception.Message);
                    return;
                }

                if (!confirmation.Result.TryGetValue(out var response)
                    || confirmation.WasPredicted
                    || response.Value != 1
                    || multiplayerProbeState.Value.Value != 1
                    || multiplayerPresentedValue != 1)
                {
                    Fail(
                        "integration.multiplayer-loopback",
                        "generated state, command, codec, or accepted presentation did not complete canonically");
                    return;
                }

                Pass(
                    "integration.multiplayer-loopback",
                    "session=" + session.Id +
                    ";participant=" + localParticipant.GetValueOrDefault() +
                    ";generated-value=" + response.Value);
            }));
        }

        [MultiplayerCommand(
            "loopback-probe",
            Prediction = PredictionMode.None,
            MaximumPerSecond = 4,
            MaximumPayloadBytes = 256)]
        private OperationResult<AcceptanceMultiplayerResponse> ProbeLoopback(
            MultiplayerCommandContext command,
            AcceptanceMultiplayerRequest request)
        {
            if (request.Delta != 1)
            {
                return OperationResult<AcceptanceMultiplayerResponse>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The acceptance probe requires an exact delta of one.");
            }

            var updated = multiplayerProbeState.Update(current =>
                OperationResult<AcceptanceMultiplayerState>.Success(
                    new AcceptanceMultiplayerState { Value = current.Value + request.Delta }));
            if (!updated.TryGetValue(out var state))
            {
                return OperationResult<AcceptanceMultiplayerResponse>.Failure(
                    updated.ErrorCode,
                    updated.ErrorMessage);
            }

            var emitted = EmitOnLoopbackAccepted(
                command,
                new AcceptanceMultiplayerEvent { Value = state.Value },
                MultiplayerAudience.Everyone);
            if (!emitted.Succeeded)
            {
                return OperationResult<AcceptanceMultiplayerResponse>.Failure(
                    emitted.ErrorCode,
                    emitted.ErrorMessage);
            }

            return OperationResult<AcceptanceMultiplayerResponse>.Success(
                new AcceptanceMultiplayerResponse { Value = state.Value });
        }

        [PresentationEvent("loopback-accepted")]
        private void OnLoopbackAccepted(AcceptanceMultiplayerEvent value)
        {
            multiplayerPresentedValue = value.Value;
        }
    }

    public sealed class AcceptanceMultiplayerState
    {
        public int Value { get; set; }
    }

    public sealed class AcceptanceMultiplayerRequest
    {
        public int Delta { get; set; }
    }

    public sealed class AcceptanceMultiplayerResponse
    {
        public int Value { get; set; }
    }

    public sealed class AcceptanceMultiplayerEvent
    {
        public int Value { get; set; }
    }
}
