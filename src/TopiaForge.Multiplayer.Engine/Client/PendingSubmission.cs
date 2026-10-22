using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// A client's command or object input from submission until the host's commit covers it. Its prediction can replay
    /// any number of times until then, even after its caller cancelled it, because a sent submission still executes.
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

        /// <summary>Gets or sets whether the submission reached the link. A sent submission always executes on the host.</summary>
        internal bool Sent { get; set; }

        /// <summary>Gets whether the caller's task has completed, with a result or as cancelled.</summary>
        internal bool Finished { get; private set; }

        internal SubmissionResultMessage? Result { get; set; }

        /// <summary>Gets the object an input targets, or null for a command.</summary>
        internal virtual ObjectRecord? Target => null;

        /// <summary>Gets the owner mod of the command or object type this submission targets.</summary>
        internal abstract string Owner { get; }

        /// <summary>
        /// Gets or sets the facade a command was submitted through, so closing that facade cancels it and no other facade's.
        /// Null for a replicated object's input, which belongs to the object's owner as a whole.
        /// </summary>
        internal object? Submitter { get; set; }

        internal void RegisterCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled) return;
            registration = cancellationToken.Register(() => Engine.RequestCancellation(this));
        }

        /// <summary>Writes the submission message for the host.</summary>
        internal abstract void Write(ProtocolWriter frame, ProtocolWriter body, InternWriter intern);

        /// <summary>Runs the owner prediction inside <paramref name="scope"/>.</summary>
        internal abstract WireOutcome Predict(TransactionScope scope);

        /// <summary>Marks the caller's task as about to complete; returns false if it already has.</summary>
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

        internal override string Owner => binding.Key.Owner;

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

        internal override string Owner => record.Type.Key.Owner;

        internal override void Write(ProtocolWriter frame, ProtocolWriter body, InternWriter intern) =>
            ReplicationMessages.WriteObjectInput(frame, body, Epoch, Sequence, record.Id.Value, Payload);

        internal override WireOutcome Predict(TransactionScope scope)
        {
            if (record.Life != ObjectLife.Live) return WireOutcome.Failure(ModErrorCode.NotFound, "The replicated object is not spawned.");
            if (!record.Type.IsActive) return WireOutcome.Failure(ModErrorCode.InvalidState, "The replicated-object type registration is inactive.");
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
