using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// A client's command or object input from submission until the host's result completes it. Its prediction can
    /// replay any number of times until the host acknowledges its sequence.
    /// </summary>
    internal abstract class PendingSubmission
    {
        private CancellationTokenRegistration registration;

        protected PendingSubmission(SessionEngine engine, ulong sequence, ulong epoch, byte[] payload, NetworkTick submittedAt, bool predicted)
        {
            Engine = engine;
            Sequence = sequence;
            Epoch = epoch;
            Payload = payload;
            SubmittedAt = submittedAt;
            Predicted = predicted;
        }

        internal SessionEngine Engine { get; }
        internal ulong Sequence { get; }
        internal ulong Epoch { get; }
        internal byte[] Payload { get; }
        internal NetworkTick SubmittedAt { get; }
        internal bool Predicted { get; }
        internal bool Sent { get; set; }
        internal bool Finished { get; private set; }
        internal SubmissionResultMessage? Result { get; set; }

        /// <summary>Gets the object an input targets, or null for a command.</summary>
        internal virtual ObjectRecord? Target => null;

        internal void RegisterCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled) return;
            registration = cancellationToken.Register(() => Engine.RequestCancellation(this));
        }

        /// <summary>Writes the submission message for the host.</summary>
        internal abstract void Write(ProtocolWriter frame, ProtocolWriter body, InternWriter intern);

        /// <summary>Runs the owner prediction inside <paramref name="scope"/>.</summary>
        internal abstract WireOutcome Predict(TransactionScope scope);

        /// <summary>Marks the submission finished so it stops replaying; returns false if it already was.</summary>
        internal bool TryFinish()
        {
            if (Finished) return false;
            Finished = true;
            return true;
        }

        /// <summary>Completes the caller's task. The engine calls it once, at a settle point, after <see cref="TryFinish"/>.</summary>
        internal void Complete(NetworkTick confirmedAt, WireOutcome outcome)
        {
            registration.Dispose();
            CompleteTask(confirmedAt, outcome);
        }

        protected abstract void CompleteTask(NetworkTick confirmedAt, WireOutcome outcome);
    }

    internal sealed class PendingCommand<TRequest, TResponse> : PendingSubmission
        where TRequest : class
        where TResponse : class
    {
        private readonly CommandBinding<TRequest, TResponse> binding;
        private readonly TaskCompletionSource<MultiplayerCommandConfirmation<TResponse>> completion =
            new TaskCompletionSource<MultiplayerCommandConfirmation<TResponse>>();

        internal PendingCommand(
            SessionEngine engine,
            CommandBinding<TRequest, TResponse> binding,
            ulong sequence,
            ulong epoch,
            byte[] payload,
            NetworkTick submittedAt,
            bool predicted)
            : base(engine, sequence, epoch, payload, submittedAt, predicted)
        {
            this.binding = binding;
        }

        internal Task<MultiplayerCommandConfirmation<TResponse>> Task => completion.Task;

        internal override void Write(ProtocolWriter frame, ProtocolWriter body, InternWriter intern) =>
            ReplicationMessages.WriteCommand(frame, body, intern, Epoch, Sequence, binding.Key.Owner, binding.Key.Id, Payload);

        internal override WireOutcome Predict(TransactionScope scope) =>
            binding.IsActive
                ? binding.Invoke(scope.Sender, Sequence, Payload, scope)
                : WireOutcome.Failure(ModErrorCode.InvalidState, "The command registration is inactive.");

        protected override void CompleteTask(NetworkTick confirmedAt, WireOutcome outcome) =>
            completion.TrySetResult(new MultiplayerCommandConfirmation<TResponse>(
                SubmittedAt,
                confirmedAt,
                Predicted,
                binding.DecodeResponse(outcome)));
    }

    internal sealed class PendingObjectInput<TState, TInput> : PendingSubmission
        where TState : class
        where TInput : class
    {
        private readonly ObjectRecord<TState, TInput> record;
        private readonly TaskCompletionSource<MultiplayerCommandConfirmation<TState>> completion =
            new TaskCompletionSource<MultiplayerCommandConfirmation<TState>>();

        internal PendingObjectInput(
            SessionEngine engine,
            ObjectRecord<TState, TInput> record,
            ulong sequence,
            ulong epoch,
            byte[] payload,
            NetworkTick submittedAt,
            bool predicted)
            : base(engine, sequence, epoch, payload, submittedAt, predicted)
        {
            this.record = record;
        }

        internal Task<MultiplayerCommandConfirmation<TState>> Task => completion.Task;

        internal override ObjectRecord? Target => record;

        internal override void Write(ProtocolWriter frame, ProtocolWriter body, InternWriter intern) =>
            ReplicationMessages.WriteObjectInput(frame, body, Epoch, Sequence, record.Id.Value, Payload);

        internal override WireOutcome Predict(TransactionScope scope)
        {
            if (record.Life != ObjectLife.Live) return WireOutcome.Failure(ModErrorCode.NotFound, "The replicated object is not spawned.");
            var owns = record.CurrentOwner.HasValue && record.CurrentOwner.Value.Equals(scope.Sender);
            return record.InvokeInput(scope.Sender, Sequence, owns, Payload, scope);
        }

        protected override void CompleteTask(NetworkTick confirmedAt, WireOutcome outcome) =>
            completion.TrySetResult(new MultiplayerCommandConfirmation<TState>(
                SubmittedAt,
                confirmedAt,
                Predicted,
                record.DecodeOutcome(outcome)));
    }
}
