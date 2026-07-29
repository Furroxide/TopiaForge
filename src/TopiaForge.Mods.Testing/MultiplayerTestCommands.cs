using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    internal interface ITestCommand : IMultiplayerCommandRegistration
    {
        PredictionMode Prediction { get; }
        void ResetForNewSession();
    }

    internal sealed class TestCommand<TRequest, TResponse> : ITestCommand
        where TRequest : class
        where TResponse : class
    {
        private readonly MultiplayerTestSession session;
        private readonly MultiplayerCommandDefinition<TRequest, TResponse> definition;
        private readonly Dictionary<ParticipantId, Queue<ulong>> rateWindows =
            new Dictionary<ParticipantId, Queue<ulong>>();

        internal TestCommand(
            MultiplayerTestSession session,
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
        {
            this.session = session;
            this.definition = definition;
            IsActive = true;
        }

        public string Id => definition.Id;
        public bool IsActive { get; private set; }
        public PredictionMode Prediction => definition.Prediction;

        internal bool TryConsumeRate(ParticipantId sender, NetworkTick tick)
        {
            if (!rateWindows.TryGetValue(sender, out var ticks))
            {
                ticks = new Queue<ulong>();
                rateWindows.Add(sender, ticks);
            }

            while (ticks.Count > 0 && ticks.Peek() + MultiplayerTestRig.TicksPerSecond <= tick.Value) ticks.Dequeue();
            if (ticks.Count >= definition.MaximumPerSecond) return false;
            ticks.Enqueue(tick.Value);
            return true;
        }

        internal OperationResult<TRequest> CloneRequest(TRequest request) =>
            MultiplayerTestCodec.RoundTrip(definition.RequestCodec, request);

        internal OperationResult<TResponse> CloneResponse(TResponse response) =>
            MultiplayerTestCodec.RoundTrip(definition.ResponseCodec, response);

        public void ResetForNewSession() => rateWindows.Clear();

        internal CommandOutcome<TResponse> Invoke(
            ParticipantId sender,
            NetworkTick tick,
            TRequest request,
            CancellationToken cancellationToken)
        {
            if (!IsActive)
            {
                return new CommandOutcome<TResponse>(
                    OperationResult<TResponse>.Failure(ModErrorCode.InvalidState, "The command registration is inactive."),
                    Array.Empty<BufferedTestPresentation>());
            }

            var copied = CloneRequest(request);
            if (!copied.TryGetValue(out var requestCopy))
            {
                return new CommandOutcome<TResponse>(
                    OperationResult<TResponse>.Failure(copied.ErrorCode, copied.ErrorMessage),
                    Array.Empty<BufferedTestPresentation>());
            }

            var events = new List<BufferedTestPresentation>();
            var context = new MultiplayerCommandContext(
                sender,
                tick,
                cancellationToken,
                (id, value, audience) =>
                {
                    events.Add(new BufferedTestPresentation(id, value, audience));
                    return OperationResult<bool>.Success(true);
                });
            OperationResult<TResponse> result;
            try
            {
                result = definition.Handler(context, requestCopy) ??
                    OperationResult<TResponse>.Failure(
                        ModErrorCode.Unknown,
                        "The multiplayer command handler returned no result.");
            }
            catch (Exception exception)
            {
                result = MultiplayerTestCodec.FromException<TResponse>(
                    exception,
                    "The multiplayer command handler failed.");
            }

            if (!result.TryGetValue(out var response))
            {
                return new CommandOutcome<TResponse>(result, events);
            }

            var responseCopy = CloneResponse(response);
            return new CommandOutcome<TResponse>(responseCopy, events);
        }

        public void Dispose()
        {
            if (!IsActive) return;
            IsActive = false;
            session.RemoveCommand(Id, this);
        }
    }

    internal sealed class CommandOutcome<TResponse> where TResponse : class
    {
        internal CommandOutcome(
            OperationResult<TResponse> result,
            IReadOnlyList<BufferedTestPresentation> events)
        {
            Result = result;
            Events = events;
        }

        internal OperationResult<TResponse> Result { get; }
        internal IReadOnlyList<BufferedTestPresentation> Events { get; }
    }

    internal sealed class CachedCommandResult<TResponse> where TResponse : class
    {
        internal CachedCommandResult(
            NetworkTick confirmedAt,
            OperationResult<TResponse> result,
            IReadOnlyList<TestStateSnapshot> states)
        {
            ConfirmedAt = confirmedAt;
            Result = result;
            States = states;
        }

        internal NetworkTick ConfirmedAt { get; }
        internal OperationResult<TResponse> Result { get; }
        internal IReadOnlyList<TestStateSnapshot> States { get; }
    }

    internal interface IPendingTestCommand : IPendingTestPrediction
    {
        ulong Sequence { get; }
        void Cancel(string message);
    }

    internal sealed class PendingTestCommand<TRequest, TResponse> : IPendingTestCommand
        where TRequest : class
        where TResponse : class
    {
        private readonly MultiplayerTestSession session;
        private readonly TaskCompletionSource<MultiplayerCommandConfirmation<TResponse>> completion =
            new TaskCompletionSource<MultiplayerCommandConfirmation<TResponse>>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        internal PendingTestCommand(
            MultiplayerTestSession session,
            TestCommand<TRequest, TResponse> command,
            ulong sequence,
            ulong predictionOrder,
            NetworkTick submittedAt,
            TRequest request,
            bool wasPredicted)
        {
            this.session = session;
            Command = command;
            Sequence = sequence;
            PredictionOrder = predictionOrder;
            SubmittedAt = submittedAt;
            Request = request;
            WasPredicted = wasPredicted;
        }

        internal TestCommand<TRequest, TResponse> Command { get; }
        internal NetworkTick SubmittedAt { get; }
        internal TRequest Request { get; }
        internal Task<MultiplayerCommandConfirmation<TResponse>> Task => completion.Task;
        public ulong Sequence { get; }
        public ulong PredictionOrder { get; }
        public bool WasPredicted { get; }
        public bool IsCompleted => completion.Task.IsCompleted;

        public void Replay() => session.PredictCommand(this);

        internal void RegisterCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled) return;
            cancellationToken.Register(() =>
                session.CancelPendingCommand(this, "The multiplayer command submission was cancelled."));
        }

        public void Cancel(string message)
        {
            completion.TrySetResult(new MultiplayerCommandConfirmation<TResponse>(
                SubmittedAt,
                session.Snapshot.Tick,
                WasPredicted,
                OperationResult<TResponse>.Failure(ModErrorCode.Cancelled, message)));
        }

        internal void Complete(NetworkTick confirmedAt, OperationResult<TResponse> result)
        {
            if (result.TryGetValue(out var response))
            {
                var copy = Command.CloneResponse(response);
                result = copy.TryGetValue(out var cloned)
                    ? OperationResult<TResponse>.Success(cloned)
                    : OperationResult<TResponse>.Failure(copy.ErrorCode, copy.ErrorMessage);
            }

            completion.TrySetResult(new MultiplayerCommandConfirmation<TResponse>(
                SubmittedAt,
                confirmedAt,
                WasPredicted,
                result));
        }
    }

    internal sealed partial class MultiplayerTestSession
    {
        internal void PredictCommand<TRequest, TResponse>(PendingTestCommand<TRequest, TResponse> pending)
            where TRequest : class
            where TResponse : class
        {
            var transaction = BeginPredictedStateTransaction();
            if (!transaction.TryGetValue(out var before)) return;
            var predicted = pending.Command.Invoke(
                snapshot.LocalParticipantId ?? default,
                snapshot.Tick,
                pending.Request,
                currentSession.Token);

            FinishPredictedStateTransaction(before, predicted.Result.Succeeded);
        }

        internal void ProcessCanonicalCommand<TRequest, TResponse>(
            ParticipantId sender,
            ulong sequence,
            string commandId,
            TRequest request,
            MultiplayerTestSession senderSession)
            where TRequest : class
            where TResponse : class
        {
            if (ended) return;
            OperationResult<TResponse> result;
            IReadOnlyList<BufferedTestPresentation> events = Array.Empty<BufferedTestPresentation>();
            var sequenceKey = sender.Value + "\u001f" + commandId;
            var resultKey = sequenceKey + "\u001f" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (canonicalCommandResults.TryGetValue(resultKey, out var cachedValue)
                && cachedValue is CachedCommandResult<TResponse> cached)
            {
                Action redeliver = () => senderSession.ReceiveCommandResult<TRequest, TResponse>(
                    sequence,
                    cached.ConfirmedAt,
                    cached.Result,
                    cached.States);
                if (ReferenceEquals(senderSession, this)) redeliver();
                else rig.SendReliable(senderSession, redeliver);
                return;
            }

            if (!rig.IsParticipant(sender))
            {
                result = OperationResult<TResponse>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "The authenticated sender is no longer connected to this session.");
            }
            else if (lastCanonicalCommandSequence.TryGetValue(sequenceKey, out var last) && sequence <= last)
            {
                result = OperationResult<TResponse>.Failure(
                    ModErrorCode.Conflict,
                    "The server rejected a stale or duplicate command sequence.");
            }
            else if (!commands.TryGetValue(commandId, out var found) || found is not TestCommand<TRequest, TResponse> command)
            {
                lastCanonicalCommandSequence[sequenceKey] = sequence;
                result = OperationResult<TResponse>.Failure(
                    ModErrorCode.NotFound,
                    "The canonical server has no matching registration for command '" + commandId + "'.");
            }
            else if (!command.TryConsumeRate(sender, snapshot.Tick))
            {
                lastCanonicalCommandSequence[sequenceKey] = sequence;
                result = OperationResult<TResponse>.Failure(
                    ModErrorCode.RateLimited,
                    "The multiplayer command rate limit was exceeded.");
            }
            else
            {
                lastCanonicalCommandSequence[sequenceKey] = sequence;
                var transaction = BeginCanonicalStateTransaction();
                if (!transaction.TryGetValue(out var before))
                {
                    result = OperationResult<TResponse>.Failure(transaction.ErrorCode, transaction.ErrorMessage);
                }
                else
                {
                    var outcome = command.Invoke(sender, snapshot.Tick, request, currentSession.Token);
                    result = outcome.Result;
                    var completed = FinishCanonicalStateTransaction(before, result.Succeeded);
                    if (!completed.Succeeded)
                    {
                        result = OperationResult<TResponse>.Failure(completed.ErrorCode, completed.ErrorMessage);
                    }
                    else if (result.Succeeded)
                    {
                        PublishCanonicalStateChanges(before);
                        events = outcome.Events;
                    }

                    if (!result.Succeeded) events = Array.Empty<BufferedTestPresentation>();
                }
            }

            var confirmedAt = snapshot.Tick;
            var canonicalStates = CaptureCanonicalStates();
            canonicalCommandResults[resultKey] = new CachedCommandResult<TResponse>(
                confirmedAt,
                result,
                canonicalStates.Select(item => item.Copy()).ToArray());
            Action deliver = () => senderSession.ReceiveCommandResult<TRequest, TResponse>(
                sequence,
                confirmedAt,
                result,
                canonicalStates);
            if (ReferenceEquals(senderSession, this)) deliver();
            else rig.SendReliable(senderSession, deliver);

            foreach (var target in rig.ClientSessions)
            {
                if (ReferenceEquals(target, senderSession) || ReferenceEquals(target, this)) continue;
                var captured = canonicalStates.Select(item => item.Copy()).ToArray();
                rig.SendReliable(target, () => target.ApplyCanonicalStates(
                    captured,
                    TestStateSnapshotScope.Complete));
            }

            if (result.Succeeded)
            {
                foreach (var item in events) rig.DispatchPresentation(item, sender);
            }
        }

        private void ReceiveCommandResult<TRequest, TResponse>(
            ulong sequence,
            NetworkTick confirmedAt,
            OperationResult<TResponse> result,
            IReadOnlyList<TestStateSnapshot> canonicalStates)
            where TRequest : class
            where TResponse : class
        {
            for (var index = 0; index < pendingCommands.Count; index++)
            {
                var candidate = pendingCommands[index];
                if (candidate.Sequence != sequence) continue;
                if (candidate is not PendingTestCommand<TRequest, TResponse> typed) return;
                pendingCommands.RemoveAt(index);
                ApplyCanonicalStates(canonicalStates, TestStateSnapshotScope.Complete);
                typed.Complete(confirmedAt, result);
                return;
            }
        }

        internal void CancelPendingCommand(IPendingTestCommand pending, string message)
        {
            if (!pendingCommands.Remove(pending)) return;
            pending.Cancel(message);
            ReconcilePendingPredictions();
        }

        private void RestoreCurrentStates(
            IReadOnlyList<TestStateSnapshot> snapshots,
            bool notify = true)
        {
            foreach (var snapshotValue in snapshots)
            {
                if (states.TryGetValue(snapshotValue.Id, out var state)) state.RestoreCurrent(snapshotValue, notify);
            }
        }
    }
}
