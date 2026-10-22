using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        /// <summary>
        /// Submits a command. Closing the <paramref name="submitter"/> facade, when given, cancels the command while it
        /// still waits for the host. An <paramref name="answer"/>, when given, receives the same confirmation as the task: at
        /// once for a refusal or a host's own command, otherwise at the settle point that completes the submission.
        /// </summary>
        internal Task<MultiplayerCommandConfirmation<TResponse>> SubmitCommand<TRequest, TResponse>(
            string owner,
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            CancellationToken cancellationToken,
            object? submitter = null,
            ICommandAnswer<TResponse>? answer = null)
            where TRequest : class
            where TResponse : class
        {
            if (commandType == null) throw new ArgumentNullException(nameof(commandType));
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            RequireOwnerThread();
            if (ended) throw new ObjectDisposedException(nameof(IMultiplayerSession));
            var refusal = SubmissionRefusal<TResponse>("Commands");
            if (refusal != null) return Answered(refusal, answer);
            if (!commands.TryGetValue(new WireKey(owner, commandType.Id), out var found) ||
                found is not CommandBinding<TRequest, TResponse> binding || !binding.IsActive)
            {
                return Answered(
                    Refused<TResponse>(
                        ModErrorCode.NotFound,
                        "Command '" + commandType.Id + "' is not registered with the requested generated types."),
                    answer);
            }

            var encoded = EngineCodec.Encode(binding.Definition.RequestCodec, request);
            if (!encoded.TryGetValue(out var bytes)) return Answered(Refused<TResponse>(encoded.ErrorCode, encoded.ErrorMessage), answer);
            var sender = localParticipantId!.Value;
            if (Role == EngineRole.Host)
            {
                var outcome = ExecuteCommand(sender, binding.Key, NextHostLocalSequence(sender), bytes);
                var confirmation = new MultiplayerCommandConfirmation<TResponse>(
                    CurrentTick,
                    CurrentTick,
                    false,
                    binding.DecodeResponse(outcome));
                EndOperation();

                // The settle point above presented the command's events, so the host's own player sees them before the
                // answer, as a remote player does.
                return Answered(confirmation, answer);
            }

            var pending = new PendingCommand<TRequest, TResponse>(
                this,
                binding,
                ++nextSequence,
                epoch,
                bytes,
                CurrentTick,
                binding.Prediction == PredictionMode.Owner,
                answer)
            {
                Submitter = submitter
            };
            return SubmitPending(pending, pending.Task, cancellationToken);
        }

        private static Task<MultiplayerCommandConfirmation<T>> Answered<T>(
            MultiplayerCommandConfirmation<T> confirmation,
            ICommandAnswer<T>? answer)
            where T : class
        {
            answer?.Deliver(confirmation);
            return Task.FromResult(confirmation);
        }

        internal Task<MultiplayerCommandConfirmation<TState>> SubmitObjectInput<TState, TInput>(
            ObjectRecord<TState, TInput> record,
            TInput input,
            CancellationToken cancellationToken)
            where TState : class
            where TInput : class
        {
            RequireOwnerThread();
            if (ended) throw new ObjectDisposedException(nameof(IMultiplayerSession));
            var refusal = SubmissionRefusal<TState>("Replicated input");
            if (refusal != null) return Task.FromResult(refusal);
            var definition = record.TypedType.Definition;
            var encoded = EngineCodec.Encode(definition.InputCodec, input);
            if (!encoded.TryGetValue(out var bytes)) return Task.FromResult(Refused<TState>(encoded.ErrorCode, encoded.ErrorMessage));
            var sender = localParticipantId!.Value;
            if (Role == EngineRole.Host)
            {
                var outcome = ExecuteObjectInput(sender, record.Id.Value, NextHostLocalSequence(sender), bytes);
                var confirmation = new MultiplayerCommandConfirmation<TState>(
                    CurrentTick,
                    CurrentTick,
                    false,
                    record.DecodeOutcome(outcome));
                EndOperation();
                return Task.FromResult(confirmation);
            }

            var predicted = definition.Prediction == PredictionMode.Owner &&
                record.CurrentOwner.HasValue && record.CurrentOwner.Value.Equals(sender);
            var pending = new PendingObjectInput<TState, TInput>(this, record, ++nextSequence, epoch, bytes, CurrentTick, predicted);
            return SubmitPending(pending, pending.Task, cancellationToken);
        }

        private MultiplayerCommandConfirmation<T>? SubmissionRefusal<T>(string what) where T : class
        {
            if (state != MultiplayerSessionState.Ready)
            {
                return Refused<T>(ModErrorCode.InvalidState, what + " cannot be submitted until the canonical snapshot is ready.");
            }

            if (!localParticipantId.HasValue)
            {
                return Refused<T>(ModErrorCode.NotAuthoritative, "A headless server has no authenticated local participant.");
            }

            if (scope.IsOpen)
            {
                return Refused<T>(ModErrorCode.InvalidState, what + " cannot be submitted from inside a multiplayer transaction.");
            }

            return null;
        }

        private MultiplayerCommandConfirmation<T> Refused<T>(ModErrorCode code, string message) where T : class =>
            new MultiplayerCommandConfirmation<T>(CurrentTick, CurrentTick, false, OperationResult<T>.Failure(code, message));
    }
}
