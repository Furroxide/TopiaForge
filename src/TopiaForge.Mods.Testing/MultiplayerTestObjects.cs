using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    internal interface ITestObjectType : IReplicatedObjectTypeRegistration
    {
        ITestObject CreateRemote(MultiplayerTestSession session, TestObjectSnapshot snapshot);
    }

    internal sealed class TestObjectType<TState, TInput> : ITestObjectType
        where TState : class
        where TInput : class
    {
        private readonly MultiplayerTestSession session;

        internal TestObjectType(
            MultiplayerTestSession session,
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
        {
            this.session = session;
            Definition = definition;
            IsActive = true;
        }

        internal ReplicatedObjectTypeDefinition<TState, TInput> Definition { get; }
        public string TypeId => Definition.TypeId;
        public bool IsActive { get; private set; }

        public ITestObject CreateRemote(MultiplayerTestSession target, TestObjectSnapshot snapshot)
        {
            var decoded = MultiplayerTestCodec.Decode(Definition.StateCodec, snapshot.Bytes);
            if (!decoded.TryGetValue(out var value))
            {
                throw new InvalidOperationException(
                    "Unable to decode replicated object '" + snapshot.Id + "' with local type '" + TypeId + "': " +
                    decoded.ErrorMessage);
            }

            var item = new TestReplicatedObject<TState, TInput>(
                target,
                snapshot.Id,
                snapshot.OwnerId,
                value,
                Definition,
                snapshot.Version);
            item.CommitCurrent();
            return item;
        }

        public void Dispose()
        {
            if (!IsActive) return;
            IsActive = false;
            session.RemoveObjectType(TypeId, this);
        }
    }

    internal interface ITestObject : IDisposable
    {
        string TypeId { get; }
        NetworkObjectId Id { get; }
        ParticipantId? OwnerId { get; }
        ulong Version { get; }
        TestObjectSnapshot Capture();
        TestObjectSnapshot CaptureDespawn();
        void ApplyCanonical(TestObjectSnapshot snapshot, bool replayPredictions);
        void ResetForPredictionReplay();
        IEnumerable<IPendingTestPrediction> GetPendingPredictions();
        void BeginSynchronization();
        void ReleaseDisconnectedOwner(ParticipantId participantId);
        void PublishChange(MultiplayerTestSession session, ReplicatedObjectChangeKind kind);
    }

    internal sealed class TestReplicatedObject<TState, TInput> :
        IReplicatedObject<TState, TInput>,
        ITestObject
        where TState : class
        where TInput : class
    {
        private readonly MultiplayerTestSession session;
        private readonly ReplicatedObjectTypeDefinition<TState, TInput> definition;
        private readonly List<PendingObjectInput> pending = new List<PendingObjectInput>();
        private readonly Dictionary<ParticipantId, ulong> lastCanonicalSequence =
            new Dictionary<ParticipantId, ulong>();
        private readonly Dictionary<string, CachedObjectResult> canonicalResults =
            new Dictionary<string, CachedObjectResult>(StringComparer.Ordinal);
        private readonly Dictionary<ParticipantId, Queue<ulong>> canonicalRateWindows =
            new Dictionary<ParticipantId, Queue<ulong>>();
        private TState current;
        private TState confirmed;
        private ulong version;
        private ulong confirmedVersion;
        private ulong nextSequence;
        private ulong highestCanonicalConfirmationSequence;
        private bool disposed;

        internal TestReplicatedObject(
            MultiplayerTestSession session,
            NetworkObjectId id,
            ParticipantId? ownerId,
            TState initialState,
            ReplicatedObjectTypeDefinition<TState, TInput> definition,
            ulong version)
        {
            this.session = session;
            Id = id;
            OwnerId = ownerId;
            this.definition = definition;
            current = CloneState(initialState);
            confirmed = CloneState(initialState);
            this.version = version;
            confirmedVersion = version;
        }

        public string TypeId => definition.TypeId;
        public NetworkObjectId Id { get; }
        public bool IsSpawned => !disposed;
        public ParticipantId? OwnerId { get; private set; }
        public TState State => CloneState(current);
        public ulong Version => version;

        public Task<MultiplayerCommandConfirmation<TState>> SubmitInputAsync(
            TInput input,
            CancellationToken cancellationToken = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (session.Snapshot.State != MultiplayerSessionState.Ready)
            {
                return Task.FromResult(Failure(
                    ModErrorCode.InvalidState,
                    "Replicated input cannot be submitted until the canonical snapshot is ready."));
            }

            var sender = session.Snapshot.LocalParticipantId;
            if (!sender.HasValue)
            {
                return Task.FromResult(Failure(
                    ModErrorCode.NotAuthoritative,
                    "A headless server has no authenticated local participant."));
            }

            var copied = MultiplayerTestCodec.RoundTrip(definition.InputCodec, input);
            if (!copied.TryGetValue(out var inputCopy))
            {
                return Task.FromResult(Failure(copied.ErrorCode, copied.ErrorMessage));
            }

            var sequence = ++nextSequence;
            var wasPredicted = definition.Prediction == PredictionMode.Owner
                && OwnerId.HasValue
                && OwnerId.Value.Equals(sender.Value)
                && !session.HasServerSide;
            var item = new PendingObjectInput(
                this,
                sequence,
                wasPredicted ? session.AllocatePredictionOrder() : 0,
                session.Snapshot.Tick,
                inputCopy,
                wasPredicted);
            pending.Add(item);
            if (wasPredicted) item.Replay();
            item.RegisterCancellation(cancellationToken);

            if (session.HasServerSide)
            {
                ProcessCanonicalInput(sender.Value, sequence, inputCopy, this);
            }
            else
            {
                var sourceGeneration = session.ConnectionGeneration;
                session.Rig.SendReliable(session.Rig.ServerSession, () =>
                {
                    if (!item.IsCompleted && session.CanReceiveTransport(sourceGeneration))
                    {
                        session.Rig.ServerSession.ProcessCanonicalObjectInput(
                            Id,
                            sender.Value,
                            sequence,
                            inputCopy,
                            this);
                    }
                });
            }

            return item.Task;
        }

        public OperationResult<bool> TransferOwnership(ParticipantId? ownerId)
        {
            if (disposed)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The replicated object is disposed.");
            }

            if (!session.HasServerSide)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only the canonical server may transfer object ownership.");
            }

            if (ownerId.HasValue && !session.Rig.IsParticipant(ownerId.Value))
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The new owner is not an admitted participant.");
            }

            OwnerId = ownerId;
            version++;
            CommitCurrent();
            session.NotifyObjectChanged(this, ReplicatedObjectChangeKind.Changed);
            session.Rig.BroadcastObject(Capture(), session);
            return OperationResult<bool>.Success(true);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var item in pending.ToArray()) item.Cancel("The replicated test object was disposed.");
            pending.Clear();
            session.RemoveObject(Id, this);
        }

        public TestObjectSnapshot Capture()
        {
            var encoded = MultiplayerTestCodec.Encode(definition.StateCodec, current);
            if (!encoded.TryGetValue(out var bytes))
            {
                throw new InvalidOperationException("Unable to snapshot replicated object '" + Id + "': " + encoded.ErrorMessage);
            }

            return new TestObjectSnapshot(TypeId, Id, OwnerId, bytes, version);
        }

        public TestObjectSnapshot CaptureDespawn()
        {
            var snapshot = Capture();
            return new TestObjectSnapshot(
                snapshot.TypeId,
                snapshot.Id,
                snapshot.OwnerId,
                snapshot.Bytes,
                snapshot.Version,
                isDespawn: true);
        }

        public void ApplyCanonical(TestObjectSnapshot snapshot, bool replayPredictions)
        {
            if (snapshot.Version < confirmedVersion)
            {
                if (replayPredictions) session.ReconcilePendingPredictions();
                return;
            }
            var changed = snapshot.Version > confirmedVersion ||
                !Nullable.Equals(snapshot.OwnerId, OwnerId);
            var decoded = MultiplayerTestCodec.Decode(definition.StateCodec, snapshot.Bytes);
            if (!decoded.TryGetValue(out var value))
            {
                throw new InvalidOperationException("Unable to decode replicated object '" + Id + "': " + decoded.ErrorMessage);
            }

            OwnerId = snapshot.OwnerId;
            confirmed = CloneState(value);
            current = CloneState(value);
            version = snapshot.Version;
            confirmedVersion = snapshot.Version;
            if (replayPredictions) session.ReconcilePendingPredictions();
            if (changed) session.NotifyObjectChanged(this, ReplicatedObjectChangeKind.Changed);
        }

        public void ResetForPredictionReplay()
        {
            current = CloneState(confirmed);
            version = confirmedVersion;
        }

        public IEnumerable<IPendingTestPrediction> GetPendingPredictions() =>
            pending
                .Where(item =>
                    item.Sequence > highestCanonicalConfirmationSequence &&
                    item.WasPredicted &&
                    !item.IsCompleted)
                .Cast<IPendingTestPrediction>();

        public void BeginSynchronization()
        {
            foreach (var item in pending.ToArray()) item.Cancel("The client began reconnect synchronization.");
            pending.Clear();
            current = CloneState(confirmed);
            version = confirmedVersion;
        }

        public void ReleaseDisconnectedOwner(ParticipantId participantId)
        {
            if (disposed || !session.HasServerSide || !OwnerId.HasValue || !OwnerId.Value.Equals(participantId)) return;
            OwnerId = null;
            version++;
            CommitCurrent();
            session.NotifyObjectChanged(this, ReplicatedObjectChangeKind.Changed);
            session.Rig.BroadcastObject(Capture(), session);
        }

        public void PublishChange(MultiplayerTestSession target, ReplicatedObjectChangeKind kind)
        {
            var capturedState = CloneState(current);
            var capturedOwnerId = OwnerId;
            var capturedVersion = version;
            target.DispatchObjectChange(
                TypeId,
                () => new ReplicatedObjectChange<TState, TInput>(
                        kind,
                        Id,
                        capturedOwnerId,
                        CloneState(capturedState),
                        capturedVersion,
                        kind == ReplicatedObjectChangeKind.Despawned ? null : this));
        }

        internal void CommitCurrent()
        {
            confirmed = CloneState(current);
            confirmedVersion = version;
        }

        internal void ProcessCanonicalInput(
            ParticipantId sender,
            ulong sequence,
            TInput input,
            TestReplicatedObject<TState, TInput> senderObject)
        {
            var resultKey = sender.Value + "\u001f" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (canonicalResults.TryGetValue(resultKey, out var cached))
            {
                Action redeliver = () => senderObject.ReceiveCanonicalResult(
                    sequence,
                    cached.ConfirmedAt,
                    cached.Result,
                    cached.Snapshot.Copy(),
                    cached.States.Select(item => item.Copy()).ToArray());
                if (ReferenceEquals(senderObject, this)) redeliver();
                else session.Rig.SendReliable(senderObject.session, redeliver);
                return;
            }

            OperationResult<TState> result;
            IReadOnlyList<BufferedTestPresentation> events = Array.Empty<BufferedTestPresentation>();
            if (!session.Rig.IsParticipant(sender))
            {
                result = OperationResult<TState>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "The authenticated sender is no longer connected to this session.");
            }
            else if (lastCanonicalSequence.TryGetValue(sender, out var last) && sequence <= last)
            {
                result = OperationResult<TState>.Failure(
                    ModErrorCode.Conflict,
                    "The server rejected a stale or duplicate replicated input.");
            }
            else
            {
                lastCanonicalSequence[sender] = sequence;
                var owns = OwnerId.HasValue && OwnerId.Value.Equals(sender);
                if (!TryConsumeRate(sender))
                {
                    result = OperationResult<TState>.Failure(
                        ModErrorCode.RateLimited,
                        "The replicated-object input rate limit was exceeded.");
                }
                else if (OwnerId.HasValue && !owns)
                {
                    result = OperationResult<TState>.Failure(
                        ModErrorCode.NotAuthoritative,
                        "The authenticated sender does not own this replicated object.");
                }
                else
                {
                    var transaction = session.BeginCanonicalStateTransaction();
                    if (!transaction.TryGetValue(out var beforeStates))
                    {
                        result = OperationResult<TState>.Failure(transaction.ErrorCode, transaction.ErrorMessage);
                    }
                    else
                    {
                        var outcome = InvokeHandler(sender, owns, input);
                        result = outcome.Result;
                        TState? currentCopy = null;
                        TState? confirmedCopy = null;
                        TState? responseCopy = null;
                        if (result.TryGetValue(out var next))
                        {
                            var encoded = MultiplayerTestCodec.Encode(definition.StateCodec, next);
                            if (!encoded.TryGetValue(out var bytes))
                            {
                                result = OperationResult<TState>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
                            }
                            else
                            {
                                var nextCurrent = MultiplayerTestCodec.Decode(definition.StateCodec, bytes);
                                var nextConfirmed = MultiplayerTestCodec.Decode(definition.StateCodec, bytes);
                                var nextResponse = MultiplayerTestCodec.Decode(definition.StateCodec, bytes);
                                if (!nextCurrent.TryGetValue(out currentCopy))
                                {
                                    result = OperationResult<TState>.Failure(
                                        nextCurrent.ErrorCode,
                                        nextCurrent.ErrorMessage);
                                }
                                else if (!nextConfirmed.TryGetValue(out confirmedCopy))
                                {
                                    result = OperationResult<TState>.Failure(
                                        nextConfirmed.ErrorCode,
                                        nextConfirmed.ErrorMessage);
                                }
                                else if (!nextResponse.TryGetValue(out responseCopy))
                                {
                                    result = OperationResult<TState>.Failure(
                                        nextResponse.ErrorCode,
                                        nextResponse.ErrorMessage);
                                }
                            }
                        }

                        var completed = session.FinishCanonicalStateTransaction(beforeStates, result.Succeeded);
                        if (!completed.Succeeded)
                        {
                            result = OperationResult<TState>.Failure(completed.ErrorCode, completed.ErrorMessage);
                        }
                        else if (result.Succeeded && currentCopy != null && confirmedCopy != null && responseCopy != null)
                        {
                            current = currentCopy;
                            confirmed = confirmedCopy;
                            version++;
                            confirmedVersion = version;
                            session.NotifyObjectChanged(this, ReplicatedObjectChangeKind.Changed);
                            result = OperationResult<TState>.Success(responseCopy);
                            session.PublishCanonicalStateChanges(beforeStates);
                            events = outcome.Events;
                        }
                    }
                }
            }

            var objectSnapshot = Capture();
            var canonicalStates = session.CaptureCanonicalStates();
            var confirmedAt = session.Snapshot.Tick;
            canonicalResults[resultKey] = new CachedObjectResult(
                confirmedAt,
                result,
                objectSnapshot.Copy(),
                canonicalStates.Select(item => item.Copy()).ToArray());
            Action deliver = () => senderObject.ReceiveCanonicalResult(
                sequence,
                confirmedAt,
                result,
                objectSnapshot.Copy(),
                canonicalStates.Select(item => item.Copy()).ToArray());
            if (ReferenceEquals(senderObject, this)) deliver();
            else session.Rig.SendReliable(senderObject.session, deliver);
            session.Rig.BroadcastObject(objectSnapshot, senderObject.session);
            foreach (var stateSnapshot in canonicalStates)
            {
                session.Rig.BroadcastState(stateSnapshot, senderObject.session);
            }

            if (result.Succeeded)
            {
                foreach (var item in events) session.Rig.DispatchPresentation(item, sender);
            }
        }

        internal void Predict(PendingObjectInput item)
        {
            var transaction = session.BeginPredictedStateTransaction();
            if (!transaction.TryGetValue(out var beforeStates)) return;
            var owns = OwnerId.HasValue
                && session.Snapshot.LocalParticipantId.HasValue
                && OwnerId.Value.Equals(session.Snapshot.LocalParticipantId.Value);
            var outcome = InvokeHandler(
                session.Snapshot.LocalParticipantId ?? default,
                owns,
                item.Input);
            if (!outcome.Result.TryGetValue(out var next))
            {
                session.FinishPredictedStateTransaction(beforeStates, accepted: false);
                return;
            }

            var copied = MultiplayerTestCodec.RoundTrip(definition.StateCodec, next);
            if (!copied.TryGetValue(out var nextCopy))
            {
                session.FinishPredictedStateTransaction(beforeStates, accepted: false);
                return;
            }

            current = nextCopy;
            version++;
            session.FinishPredictedStateTransaction(beforeStates, accepted: true);
        }

        internal void FailMissingCanonicalObject(ulong sequence, NetworkTick confirmedAt)
        {
            var item = pending.FirstOrDefault(candidate => candidate.Sequence == sequence);
            if (item == null) return;
            highestCanonicalConfirmationSequence = Math.Max(highestCanonicalConfirmationSequence, sequence);
            pending.Remove(item);
            session.ReconcilePendingPredictions();
            item.Complete(
                confirmedAt,
                OperationResult<TState>.Failure(
                    ModErrorCode.NotFound,
                    "The canonical server no longer contains this replicated object."));
        }

        private ObjectOutcome InvokeHandler(ParticipantId sender, bool owns, TInput input)
        {
            var copied = MultiplayerTestCodec.RoundTrip(definition.InputCodec, input);
            if (!copied.TryGetValue(out var inputCopy))
            {
                return new ObjectOutcome(
                    OperationResult<TState>.Failure(copied.ErrorCode, copied.ErrorMessage),
                    Array.Empty<BufferedTestPresentation>());
            }

            var state = MultiplayerTestCodec.RoundTrip(definition.StateCodec, current);
            if (!state.TryGetValue(out var stateCopy))
            {
                return new ObjectOutcome(
                    OperationResult<TState>.Failure(state.ErrorCode, state.ErrorMessage),
                    Array.Empty<BufferedTestPresentation>());
            }

            var events = new List<BufferedTestPresentation>();
            var context = new ReplicatedObjectCommandContext(
                sender,
                Id,
                session.Snapshot.Tick,
                owns,
                session.CurrentSessionToken,
                (id, value, audience) =>
                {
                    events.Add(new BufferedTestPresentation(id, value, audience));
                    return OperationResult<bool>.Success(true);
                });
            OperationResult<TState> result;
            try
            {
                result = definition.Handler(context, stateCopy, inputCopy) ??
                    OperationResult<TState>.Failure(
                        ModErrorCode.Unknown,
                        "The replicated-object handler returned no result.");
            }
            catch (Exception exception)
            {
                result = MultiplayerTestCodec.FromException<TState>(
                    exception,
                    "The replicated-object handler failed.");
            }

            return new ObjectOutcome(result, events);
        }

        private bool TryConsumeRate(ParticipantId sender)
        {
            if (!canonicalRateWindows.TryGetValue(sender, out var ticks))
            {
                ticks = new Queue<ulong>();
                canonicalRateWindows.Add(sender, ticks);
            }

            var now = session.Snapshot.Tick.Value;
            while (ticks.Count > 0 && ticks.Peek() + MultiplayerTestRig.TicksPerSecond <= now) ticks.Dequeue();
            if (ticks.Count >= definition.MaximumPerSecond) return false;
            ticks.Enqueue(now);
            return true;
        }

        private void ReceiveCanonicalResult(
            ulong sequence,
            NetworkTick confirmedAt,
            OperationResult<TState> result,
            TestObjectSnapshot snapshot,
            IReadOnlyList<TestStateSnapshot> canonicalStates)
        {
            var item = pending.FirstOrDefault(candidate => candidate.Sequence == sequence);
            if (item == null) return;
            highestCanonicalConfirmationSequence = Math.Max(highestCanonicalConfirmationSequence, sequence);
            pending.Remove(item);
            session.ApplyCanonicalStates(
                canonicalStates,
                TestStateSnapshotScope.Complete,
                replayPredictions: false);
            ApplyCanonical(snapshot, replayPredictions: true);
            item.Complete(confirmedAt, result);
        }

        private void CancelPending(PendingObjectInput item, string message)
        {
            if (!pending.Remove(item)) return;
            item.Cancel(message);
            session.ReconcilePendingPredictions();
        }

        private TState CloneState(TState value)
        {
            var copied = MultiplayerTestCodec.RoundTrip(definition.StateCodec, value);
            if (!copied.TryGetValue(out var clone))
            {
                throw new InvalidOperationException("Unable to clone replicated object '" + Id + "': " + copied.ErrorMessage);
            }

            return clone;
        }

        private OperationResult<TState> CloneResult(OperationResult<TState> result)
        {
            if (!result.TryGetValue(out var value)) return result;
            var copied = MultiplayerTestCodec.RoundTrip(definition.StateCodec, value);
            return copied.TryGetValue(out var clone)
                ? OperationResult<TState>.Success(clone)
                : OperationResult<TState>.Failure(copied.ErrorCode, copied.ErrorMessage);
        }

        private MultiplayerCommandConfirmation<TState> Failure(ModErrorCode code, string message) =>
            new MultiplayerCommandConfirmation<TState>(
                session.Snapshot.Tick,
                session.Snapshot.Tick,
                false,
                OperationResult<TState>.Failure(code, message));

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(TestReplicatedObject<TState, TInput>));
        }

        private sealed class ObjectOutcome
        {
            internal ObjectOutcome(
                OperationResult<TState> result,
                IReadOnlyList<BufferedTestPresentation> events)
            {
                Result = result;
                Events = events;
            }

            internal OperationResult<TState> Result { get; }
            internal IReadOnlyList<BufferedTestPresentation> Events { get; }
        }

        private sealed class CachedObjectResult
        {
            internal CachedObjectResult(
                NetworkTick confirmedAt,
                OperationResult<TState> result,
                TestObjectSnapshot snapshot,
                IReadOnlyList<TestStateSnapshot> states)
            {
                ConfirmedAt = confirmedAt;
                Result = result;
                Snapshot = snapshot;
                States = states;
            }

            internal NetworkTick ConfirmedAt { get; }
            internal OperationResult<TState> Result { get; }
            internal TestObjectSnapshot Snapshot { get; }
            internal IReadOnlyList<TestStateSnapshot> States { get; }
        }

        internal sealed class PendingObjectInput : IPendingTestPrediction
        {
            private readonly TestReplicatedObject<TState, TInput> owner;
            private readonly TaskCompletionSource<MultiplayerCommandConfirmation<TState>> completion =
                new TaskCompletionSource<MultiplayerCommandConfirmation<TState>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            internal PendingObjectInput(
                TestReplicatedObject<TState, TInput> owner,
                ulong sequence,
                ulong predictionOrder,
                NetworkTick submittedAt,
                TInput input,
                bool wasPredicted)
            {
                this.owner = owner;
                Sequence = sequence;
                PredictionOrder = predictionOrder;
                SubmittedAt = submittedAt;
                Input = input;
                WasPredicted = wasPredicted;
            }

            internal ulong Sequence { get; }
            public ulong PredictionOrder { get; }
            internal NetworkTick SubmittedAt { get; }
            internal TInput Input { get; }
            public bool WasPredicted { get; }
            internal Task<MultiplayerCommandConfirmation<TState>> Task => completion.Task;
            public bool IsCompleted => completion.Task.IsCompleted;

            public void Replay() => owner.Predict(this);

            internal void RegisterCancellation(CancellationToken cancellationToken)
            {
                if (!cancellationToken.CanBeCanceled) return;
                cancellationToken.Register(() =>
                    owner.CancelPending(this, "The replicated-object input submission was cancelled."));
            }

            internal void Complete(NetworkTick confirmedAt, OperationResult<TState> result)
            {
                completion.TrySetResult(new MultiplayerCommandConfirmation<TState>(
                    SubmittedAt,
                    confirmedAt,
                    WasPredicted,
                    owner.CloneResult(result)));
            }

            internal void Cancel(string message)
            {
                completion.TrySetResult(new MultiplayerCommandConfirmation<TState>(
                    SubmittedAt,
                    owner.session.Snapshot.Tick,
                    WasPredicted,
                    OperationResult<TState>.Failure(ModErrorCode.Cancelled, message)));
            }
        }
    }

    internal sealed partial class MultiplayerTestSession
    {
        internal void DispatchObjectChange<TState, TInput>(
            string typeId,
            Func<ReplicatedObjectChange<TState, TInput>> createChange)
            where TState : class
            where TInput : class
        {
            if (createChange == null) throw new ArgumentNullException(nameof(createChange));
            if (!objectChangedHandlers.TryGetValue(typeId, out var handlers)) return;
            foreach (var handler in handlers.ToArray())
            {
                if (handler is Action<ReplicatedObjectChange<TState, TInput>> typed)
                {
                    typed(createChange());
                }
                else
                {
                    handler.DynamicInvoke(createChange());
                }
            }
        }

        internal void ProcessCanonicalObjectInput<TState, TInput>(
            NetworkObjectId id,
            ParticipantId sender,
            ulong sequence,
            TInput input,
            TestReplicatedObject<TState, TInput> senderObject)
            where TState : class
            where TInput : class
        {
            if (!objects.TryGetValue(id, out var found) || found is not TestReplicatedObject<TState, TInput> item)
            {
                senderObject.FailMissingCanonicalObject(sequence, snapshot.Tick);
                return;
            }

            item.ProcessCanonicalInput(sender, sequence, input, senderObject);
        }
    }
}
