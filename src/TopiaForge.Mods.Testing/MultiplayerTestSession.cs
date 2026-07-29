using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class MultiplayerTestSession : IMultiplayerSession
    {
        private readonly MultiplayerTestRig rig;
        private readonly ParticipantId? assignedLocalParticipantId;
        private CancellationTokenSource currentSession = new CancellationTokenSource();
        private readonly Dictionary<string, ITestState> states =
            new Dictionary<string, ITestState>(StringComparer.Ordinal);
        private readonly Dictionary<string, TestStateSnapshot> pendingStateSnapshots =
            new Dictionary<string, TestStateSnapshot>(StringComparer.Ordinal);
        private readonly Dictionary<string, ITestCommand> commands =
            new Dictionary<string, ITestCommand>(StringComparer.Ordinal);
        private readonly Dictionary<string, ITestObjectType> objectTypes =
            new Dictionary<string, ITestObjectType>(StringComparer.Ordinal);
        private readonly Dictionary<NetworkObjectId, ITestObject> objects =
            new Dictionary<NetworkObjectId, ITestObject>();
        private readonly Dictionary<NetworkObjectId, TestObjectSnapshot> pendingObjectSnapshots =
            new Dictionary<NetworkObjectId, TestObjectSnapshot>();
        private readonly HashSet<NetworkObjectId> objectTombstones = new HashSet<NetworkObjectId>();
        private readonly Dictionary<string, List<Delegate>> objectChangedHandlers =
            new Dictionary<string, List<Delegate>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ITestPresentation> presentations =
            new Dictionary<string, ITestPresentation>(StringComparer.Ordinal);
        private readonly HashSet<ulong> deliveredPresentationSequences = new HashSet<ulong>();
        private readonly List<Action<MultiplayerSessionSnapshot>> changedHandlers =
            new List<Action<MultiplayerSessionSnapshot>>();
        private readonly List<IPendingTestCommand> pendingCommands = new List<IPendingTestCommand>();
        private readonly Dictionary<string, ulong> lastCanonicalCommandSequence =
            new Dictionary<string, ulong>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> canonicalCommandResults =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private MultiplayerSessionSnapshot snapshot;
        private ulong nextCommandSequence;
        private ulong nextPredictionOrder;
        private bool predictionActive;
        private bool canonicalTransactionActive;
        private ulong connectionGeneration;
        private bool transportConnected = true;
        private bool ended;

        internal MultiplayerTestSession(
            MultiplayerTestRig rig,
            ParticipantId? localParticipantId,
            MultiplayerProcessKind processKind,
            MultiplayerExecutionSide executionSides,
            MultiplayerSessionState state,
            IReadOnlyList<MultiplayerParticipant> participants)
        {
            this.rig = rig;
            assignedLocalParticipantId = localParticipantId;
            snapshot = new MultiplayerSessionSnapshot(
                new MultiplayerSessionId("topiaforge-test-session"),
                state,
                processKind,
                executionSides,
                localParticipantId,
                participants ?? throw new ArgumentNullException(nameof(participants)),
                new NetworkTick(0),
                new SessionSeed(0x544f504941544553UL));
        }

        public MultiplayerSessionSnapshot Snapshot => snapshot;
        public CancellationToken CurrentSessionToken => currentSession.Token;

        internal bool HasClientSide =>
            (snapshot.ExecutionSides & MultiplayerExecutionSide.Client) != 0;

        internal bool HasServerSide =>
            (snapshot.ExecutionSides & MultiplayerExecutionSide.Server) != 0;

        internal bool CanMutateState => HasServerSide || predictionActive;

        internal bool DefersCanonicalStateNotifications => canonicalTransactionActive;

        internal MultiplayerTestRig Rig => rig;

        internal ParticipantId? AssignedLocalParticipantId => assignedLocalParticipantId;

        internal ulong ConnectionGeneration => connectionGeneration;

        internal bool CanReceiveTransport(ulong generation) =>
            !ended && transportConnected && generation == connectionGeneration;

        public IDisposable SubscribeChanged(Action<MultiplayerSessionSnapshot> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ThrowIfEnded();
            changedHandlers.Add(handler);
            return new TestLease(() => changedHandlers.Remove(handler));
        }

        public OperationResult<IReplicatedState<T>> RegisterState<T>(ReplicatedStateDefinition<T> definition)
            where T : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (ended)
            {
                return OperationResult<IReplicatedState<T>>.Failure(
                    ModErrorCode.InvalidState,
                    "The multiplayer test session has ended.");
            }

            if (states.ContainsKey(definition.Id))
            {
                return OperationResult<IReplicatedState<T>>.Failure(
                    ModErrorCode.Conflict,
                    "Replicated state '" + definition.Id + "' is already registered on this node.");
            }

            var cloned = MultiplayerTestCodec.RoundTrip(definition.Codec, definition.InitialValue);
            if (!cloned.TryGetValue(out var initial))
            {
                return OperationResult<IReplicatedState<T>>.Failure(cloned.ErrorCode, cloned.ErrorMessage);
            }

            var state = new TestReplicatedState<T>(this, definition, initial);
            states.Add(definition.Id, state);
            if (pendingStateSnapshots.TryGetValue(definition.Id, out var pending))
            {
                state.ApplyCanonical(pending);
                pendingStateSnapshots.Remove(definition.Id);
            }
            else
            {
                state.CommitCurrent();
            }

            if (HasServerSide) rig.BroadcastState(state.CaptureCurrent(), this);
            return OperationResult<IReplicatedState<T>>.Success(state);
        }

        public OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
            where TRequest : class
            where TResponse : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (ended)
            {
                return OperationResult<IMultiplayerCommandRegistration>.Failure(
                    ModErrorCode.InvalidState,
                    "The multiplayer test session has ended.");
            }

            if (commands.ContainsKey(definition.Id))
            {
                return OperationResult<IMultiplayerCommandRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "Command '" + definition.Id + "' is already registered on this node.");
            }

            var registration = new TestCommand<TRequest, TResponse>(this, definition);
            commands.Add(definition.Id, registration);
            return OperationResult<IMultiplayerCommandRegistration>.Success(registration);
        }

        public Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (commandType == null) throw new ArgumentNullException(nameof(commandType));
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfEnded();
            if (snapshot.State != MultiplayerSessionState.Ready)
            {
                return Task.FromResult(FailedConfirmation<TResponse>(
                    ModErrorCode.InvalidState,
                    "Commands cannot be submitted until the canonical snapshot is ready."));
            }

            if (!snapshot.LocalParticipantId.HasValue)
            {
                return Task.FromResult(FailedConfirmation<TResponse>(
                    ModErrorCode.NotAuthoritative,
                    "A headless server has no authenticated local participant."));
            }

            if (!commands.TryGetValue(commandType.Id, out var found) || found is not TestCommand<TRequest, TResponse> command)
            {
                return Task.FromResult(FailedConfirmation<TResponse>(
                    ModErrorCode.NotFound,
                    "Command '" + commandType.Id + "' is not registered with the requested generated types."));
            }

            var requestCopy = command.CloneRequest(request);
            if (!requestCopy.TryGetValue(out var copy))
            {
                return Task.FromResult(FailedConfirmation<TResponse>(requestCopy.ErrorCode, requestCopy.ErrorMessage));
            }

            var sender = snapshot.LocalParticipantId.Value;
            var sequence = ++nextCommandSequence;
            var submittedAt = snapshot.Tick;
            var predicted = command.Prediction == PredictionMode.Owner && !HasServerSide;
            var pending = new PendingTestCommand<TRequest, TResponse>(
                this,
                command,
                sequence,
                predicted ? AllocatePredictionOrder() : 0,
                submittedAt,
                copy,
                predicted);
            pendingCommands.Add(pending);
            if (predicted) PredictCommand(pending);
            pending.RegisterCancellation(cancellationToken);

            if (HasServerSide)
            {
                ProcessCanonicalCommand<TRequest, TResponse>(sender, sequence, commandType.Id, copy, this);
            }
            else
            {
                var sourceGeneration = connectionGeneration;
                rig.SendReliable(rig.ServerSession, () =>
                {
                    if (!pending.IsCompleted && CanReceiveTransport(sourceGeneration))
                    {
                        rig.ServerSession.ProcessCanonicalCommand<TRequest, TResponse>(
                            sender,
                            sequence,
                            commandType.Id,
                            copy,
                            this);
                    }
                });
            }

            return pending.Task;
        }

        public OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
            where TState : class
            where TInput : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (ended)
                return OperationResult<IReplicatedObjectTypeRegistration>.Failure(
                    ModErrorCode.InvalidState,
                    "The multiplayer test session has ended.");
            if (objectTypes.ContainsKey(definition.TypeId))
                return OperationResult<IReplicatedObjectTypeRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "Replicated-object type '" + definition.TypeId + "' is already registered on this node.");

            var registration = new TestObjectType<TState, TInput>(this, definition);
            objectTypes.Add(definition.TypeId, registration);
            foreach (var pending in pendingObjectSnapshots.Values
                         .Where(item => string.Equals(item.TypeId, definition.TypeId, StringComparison.Ordinal))
                         .ToArray())
            {
                ApplyObjectSnapshot(pending, replayPredictions: false);
                pendingObjectSnapshots.Remove(pending.Id);
            }

            return OperationResult<IReplicatedObjectTypeRegistration>.Success(registration);
        }

        public OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            TState initialState,
            ParticipantId? ownerId = null)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (initialState == null) throw new ArgumentNullException(nameof(initialState));
            if (!HasServerSide)
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only the canonical logical server may spawn replicated objects.");
            }

            if (ownerId.HasValue && !rig.IsParticipant(ownerId.Value))
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                    ModErrorCode.NotFound,
                    "The requested owner is not an admitted participant.");
            }

            if (!objectTypes.TryGetValue(type.Id, out var found) || found is not TestObjectType<TState, TInput> registered)
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                    ModErrorCode.NotFound,
                    "Replicated-object type '" + type.Id + "' is not registered with the requested generated types.");
            }

            var initial = MultiplayerTestCodec.RoundTrip(registered.Definition.StateCodec, initialState);
            if (!initial.TryGetValue(out var value))
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(initial.ErrorCode, initial.ErrorMessage);
            }

            var item = new TestReplicatedObject<TState, TInput>(
                this,
                rig.AllocateObjectId(),
                ownerId,
                value,
                registered.Definition,
                0);
            objects.Add(item.Id, item);
            item.CommitCurrent();
            NotifyObjectChanged(item, ReplicatedObjectChangeKind.Spawned);
            rig.BroadcastObject(item.Capture(), this);
            return OperationResult<IReplicatedObject<TState, TInput>>.Success(item);
        }

        public OperationResult<bool> DespawnObject(NetworkObjectId id)
        {
            if (!HasServerSide)
                return OperationResult<bool>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only the canonical logical server may despawn replicated objects.");
            if (!objects.TryGetValue(id, out var item))
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The replicated object was not found.");

            var tombstone = item.CaptureDespawn();
            objects.Remove(id);
            NotifyObjectDespawned(item, tombstone);
            item.Dispose();
            rig.BroadcastObject(tombstone, this);
            return OperationResult<bool>.Success(true);
        }

        public IReadOnlyList<IReplicatedObject<TState, TInput>> GetObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            ThrowIfEnded();
            return objects.Values.OfType<TestReplicatedObject<TState, TInput>>()
                .Where(item => string.Equals(item.TypeId, type.Id, StringComparison.Ordinal))
                .OrderBy(item => item.Id.Value, StringComparer.Ordinal)
                .Cast<IReplicatedObject<TState, TInput>>()
                .ToArray();
        }

        public bool TryGetObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            NetworkObjectId id,
            out IReplicatedObject<TState, TInput>? replicatedObject)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            ThrowIfEnded();
            if (objects.TryGetValue(id, out var found) && found is TestReplicatedObject<TState, TInput> typed &&
                string.Equals(typed.TypeId, type.Id, StringComparison.Ordinal))
            {
                replicatedObject = typed;
                return true;
            }

            replicatedObject = null;
            return false;
        }

        public IDisposable SubscribeObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            Action<ReplicatedObjectChange<TState, TInput>> handler)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ThrowIfEnded();
            if (!objectChangedHandlers.TryGetValue(type.Id, out var handlers))
            {
                handlers = new List<Delegate>();
                objectChangedHandlers.Add(type.Id, handlers);
            }

            handlers.Add(handler);
            return new TestLease(() => handlers.Remove(handler));
        }

        public bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            id = default;
            return false;
        }

        public OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(
            PresentationEventDefinition<TEvent> definition)
            where TEvent : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (ended)
                return OperationResult<IPresentationEventRegistration>.Failure(
                    ModErrorCode.InvalidState,
                    "The multiplayer test session has ended.");
            if (!snapshot.HasPresentation && definition.Handler != null)
                return OperationResult<IPresentationEventRegistration>.Failure(
                    ModErrorCode.Unavailable,
                    "Presentation handlers are unavailable on a headless multiplayer test node.");
            if (presentations.ContainsKey(definition.Type.Id))
                return OperationResult<IPresentationEventRegistration>.Failure(
                    ModErrorCode.Conflict,
                    "Presentation event '" + definition.Type.Id + "' is already registered on this node.");

            var registration = new TestPresentation<TEvent>(this, definition);
            presentations.Add(definition.Type.Id, registration);
            return OperationResult<IPresentationEventRegistration>.Success(registration);
        }

        public OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            MultiplayerAudience audience = MultiplayerAudience.Everyone)
            where TEvent : class
        {
            if (eventType == null) throw new ArgumentNullException(nameof(eventType));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (!HasServerSide)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only the canonical logical server may publish a presentation event.");
            }

            if (!presentations.TryGetValue(eventType.Id, out var found) || found is not TestPresentation<TEvent>)
                return OperationResult<bool>.Failure(
                    ModErrorCode.NotFound,
                    "Presentation event '" + eventType.Id + "' is not registered with the requested generated type.");
            var encoded = MultiplayerTestCodec.Encode(eventType.Codec, value);
            if (!encoded.TryGetValue(out var bytes))
                return OperationResult<bool>.Failure(encoded.ErrorCode, encoded.ErrorMessage);

            var sender = snapshot.LocalParticipantId ?? default;
            rig.DispatchPresentation(new BufferedTestPresentation(eventType.Id, bytes, audience), sender);
            return OperationResult<bool>.Success(true);
        }

        internal void SetParticipants(IReadOnlyList<MultiplayerParticipant> participants) =>
            ReplaceSnapshot(participants: participants);

        internal void SetTick(NetworkTick tick) => ReplaceSnapshot(tick: tick);

        internal void DisconnectTransport(IReadOnlyList<MultiplayerParticipant> participants)
        {
            transportConnected = false;
            connectionGeneration++;
            CancelPendingWork("The remote client disconnected from the multiplayer test session.");
            ReplaceConnectionSnapshot(null, participants, MultiplayerSessionState.Connecting);
        }

        internal void BeginNewSession(
            MultiplayerSessionId id,
            ParticipantId? localParticipantId,
            IReadOnlyList<MultiplayerParticipant> participants,
            MultiplayerSessionState state)
        {
            var previousSession = currentSession;
            var replacementSession = new CancellationTokenSource();
            var replacementSnapshot = new MultiplayerSessionSnapshot(
                id,
                state,
                snapshot.ProcessKind,
                snapshot.ExecutionSides,
                localParticipantId,
                participants,
                snapshot.Tick,
                new SessionSeed(snapshot.Seed.Value + 1));

            previousSession.Cancel();
            currentSession = replacementSession;
            snapshot = replacementSnapshot;
            previousSession.Dispose();
            connectionGeneration++;
            transportConnected = state != MultiplayerSessionState.Connecting;

            foreach (var pending in pendingCommands.ToArray()) pending.Cancel("The multiplayer test session was replaced.");
            pendingCommands.Clear();
            foreach (var item in objects.Values.ToArray()) item.Dispose();
            objects.Clear();
            pendingObjectSnapshots.Clear();
            objectTombstones.Clear();
            objectChangedHandlers.Clear();
            pendingStateSnapshots.Clear();
            deliveredPresentationSequences.Clear();
            lastCanonicalCommandSequence.Clear();
            canonicalCommandResults.Clear();
            nextCommandSequence = 0;
            nextPredictionOrder = 0;
            predictionActive = false;
            canonicalTransactionActive = false;
            foreach (var command in commands.Values) command.ResetForNewSession();
            foreach (var replicatedState in states.Values) replicatedState.ResetForNewSession();

            foreach (var handler in changedHandlers.ToArray()) handler(snapshot);
        }

        internal void BeginSynchronization(IReadOnlyList<MultiplayerParticipant> participants)
        {
            transportConnected = true;
            connectionGeneration++;
            CancelPendingWork("The client began reconnect synchronization.");
            var local = participants.FirstOrDefault(item => item.IsLocal)?.Id;
            ReplaceConnectionSnapshot(local, participants, MultiplayerSessionState.Synchronizing);
        }

        internal void ApplySynchronization(
            IReadOnlyList<TestStateSnapshot> stateSnapshots,
            IReadOnlyList<TestObjectSnapshot> objectSnapshots)
        {
            if (ended) return;
            ApplyCanonicalStates(
                stateSnapshots,
                TestStateSnapshotScope.Complete,
                replayPredictions: false);
            pendingObjectSnapshots.Clear();
            objectTombstones.Clear();
            var canonicalObjectIds = new HashSet<NetworkObjectId>(objectSnapshots.Select(item => item.Id));
            foreach (var stale in objects.Values.Where(item => !canonicalObjectIds.Contains(item.Id)).ToArray())
            {
                objects.Remove(stale.Id);
                NotifyObjectChanged(stale, ReplicatedObjectChangeKind.Despawned);
                stale.Dispose();
            }
            foreach (var item in objectSnapshots) ApplyObjectSnapshot(item, replayPredictions: false);
            ReplaceSnapshot(state: MultiplayerSessionState.Ready);
        }

        internal IReadOnlyList<TestStateSnapshot> CaptureCanonicalStates() =>
            states.Values.Select(item => item.CaptureCurrent().Copy()).ToArray();

        internal IReadOnlyList<TestObjectSnapshot> CaptureCanonicalObjects() =>
            objects.Values.Select(item => item.Capture().Copy()).ToArray();

        internal void ApplyCanonicalStates(
            IReadOnlyList<TestStateSnapshot> snapshots,
            TestStateSnapshotScope scope,
            bool replayPredictions = true)
        {
            if (ended) return;
            if (scope == TestStateSnapshotScope.Complete)
            {
                var incomingIds = new HashSet<string>(snapshots.Select(item => item.Id), StringComparer.Ordinal);
                foreach (var state in states.Values.Where(item => !incomingIds.Contains(item.Id)))
                    state.ResetToConfirmed(notify: false);
                foreach (var pendingId in pendingStateSnapshots.Keys.Where(id => !incomingIds.Contains(id)).ToArray())
                    pendingStateSnapshots.Remove(pendingId);
            }
            foreach (var incoming in snapshots)
            {
                if (states.TryGetValue(incoming.Id, out var state)) state.ApplyCanonical(incoming);
                else pendingStateSnapshots[incoming.Id] = incoming.Copy();
            }

            if (replayPredictions) ReconcilePendingPredictions();
        }

        internal IReplicatedObject<TState, TInput> GetObject<TState, TInput>(NetworkObjectId id)
            where TState : class
            where TInput : class
        {
            if (!objects.TryGetValue(id, out var found) || found is not TestReplicatedObject<TState, TInput> typed)
            {
                throw new KeyNotFoundException("Replicated object '" + id + "' is not present with the requested generated types.");
            }

            return typed;
        }

        internal void ApplyObjectSnapshot(TestObjectSnapshot snapshot, bool replayPredictions = true)
        {
            if (ended) return;
            if (snapshot.IsDespawn)
            {
                objectTombstones.Add(snapshot.Id);
                pendingObjectSnapshots.Remove(snapshot.Id);
                if (!objects.TryGetValue(snapshot.Id, out var removed)) return;
                objects.Remove(snapshot.Id);
                removed.ApplyCanonical(snapshot, replayPredictions: false);
                NotifyObjectDespawned(removed, snapshot);
                removed.Dispose();
                if (replayPredictions) ReconcilePendingPredictions();
                return;
            }

            if (objectTombstones.Contains(snapshot.Id)) return;

            if (!objects.TryGetValue(snapshot.Id, out var item))
            {
                if (!objectTypes.TryGetValue(snapshot.TypeId, out var objectType))
                {
                    pendingObjectSnapshots[snapshot.Id] = snapshot.Copy();
                    return;
                }

                item = objectType.CreateRemote(this, snapshot);
                objects.Add(snapshot.Id, item);
                NotifyObjectChanged(item, ReplicatedObjectChangeKind.Spawned);
            }
            else
            {
                if (!string.Equals(item.TypeId, snapshot.TypeId, StringComparison.Ordinal))
                    throw new InvalidOperationException("A canonical object changed its registered wire type.");
                item.ApplyCanonical(snapshot, replayPredictions);
            }
        }

        internal void DeliverPresentation(BufferedTestPresentation item)
        {
            if (snapshot.State != MultiplayerSessionState.Ready ||
                !snapshot.HasPresentation ||
                item.Sequence == 0 ||
                !deliveredPresentationSequences.Add(item.Sequence) ||
                !presentations.TryGetValue(item.Id, out var registration)) return;
            registration.Deliver(item.Bytes);
        }

        internal void ReleaseParticipantOwnership(ParticipantId participantId)
        {
            foreach (var item in objects.Values.ToArray()) item.ReleaseDisconnectedOwner(participantId);
        }

        internal ulong AllocatePredictionOrder() => ++nextPredictionOrder;

        internal void ReconcilePendingPredictions()
        {
            foreach (var state in states.Values) state.ResetToConfirmed(notify: false);
            foreach (var item in objects.Values) item.ResetForPredictionReplay();

            var predictions = pendingCommands
                .Cast<IPendingTestPrediction>()
                .Concat(objects.Values.SelectMany(item => item.GetPendingPredictions()))
                .Where(item => item.WasPredicted && !item.IsCompleted)
                .OrderBy(item => item.PredictionOrder)
                .ToArray();
            foreach (var prediction in predictions) prediction.Replay();
        }

        internal void OnStateMutated(ITestState state)
        {
            if (!HasServerSide || canonicalTransactionActive) return;
            state.CommitCurrent();
            rig.BroadcastState(state.CaptureCurrent(), this);
        }

        internal OperationResult<TestStateSnapshot[]> BeginPredictedStateTransaction()
        {
            var captured = CaptureStateTransaction();
            if (captured.Succeeded) predictionActive = true;
            return captured;
        }

        internal void FinishPredictedStateTransaction(
            IReadOnlyList<TestStateSnapshot> before,
            bool accepted)
        {
            predictionActive = false;
            if (!accepted) RestoreCurrentStates(before, notify: true);
        }

        internal OperationResult<TestStateSnapshot[]> BeginCanonicalStateTransaction()
        {
            var captured = CaptureStateTransaction();
            if (captured.Succeeded) canonicalTransactionActive = true;
            return captured;
        }

        internal OperationResult<TestStateSnapshot[]> FinishCanonicalStateTransaction(
            IReadOnlyList<TestStateSnapshot> before,
            bool accepted)
        {
            canonicalTransactionActive = false;
            try
            {
                if (accepted)
                {
                    foreach (var state in states.Values)
                    {
                        var previous = before.FirstOrDefault(item => string.Equals(item.Id, state.Id, StringComparison.Ordinal));
                        if (previous != null && previous.Version != state.Version) state.CommitCurrent();
                    }
                }
                else
                {
                    RestoreCurrentStates(before, notify: false);
                }

                return OperationResult<TestStateSnapshot[]>.Success(
                    states.Values.Select(item => item.CaptureCurrent().Copy()).ToArray());
            }
            catch (Exception exception)
            {
                try
                {
                    RestoreCurrentStates(before, notify: false);
                }
                catch
                {
                    // A deliberately faulty test codec may also reject rollback. Preserve the original structured failure.
                }

                return MultiplayerTestCodec.FromException<TestStateSnapshot[]>(
                    exception,
                    "The replicated-state transaction could not be completed.");
            }
        }

        internal void PublishCanonicalStateChanges(IReadOnlyList<TestStateSnapshot> before)
        {
            foreach (var state in states.Values)
            {
                var previous = before.FirstOrDefault(item => string.Equals(item.Id, state.Id, StringComparison.Ordinal));
                if (previous != null && previous.Version != state.Version) state.PublishCurrent();
            }
        }

        private OperationResult<TestStateSnapshot[]> CaptureStateTransaction()
        {
            try
            {
                return OperationResult<TestStateSnapshot[]>.Success(
                    states.Values.Select(item => item.CaptureCurrent().Copy()).ToArray());
            }
            catch (Exception exception)
            {
                return MultiplayerTestCodec.FromException<TestStateSnapshot[]>(
                    exception,
                    "The replicated-state transaction could not be captured.");
            }
        }

        internal void RemoveState(string id, ITestState state)
        {
            if (states.TryGetValue(id, out var current) && ReferenceEquals(current, state)) states.Remove(id);
        }

        internal void RemoveCommand(string id, ITestCommand command)
        {
            if (commands.TryGetValue(id, out var current) && ReferenceEquals(current, command)) commands.Remove(id);
        }

        internal void RemoveObjectType(string id, ITestObjectType objectType)
        {
            if (objectTypes.TryGetValue(id, out var current) && ReferenceEquals(current, objectType)) objectTypes.Remove(id);
        }

        internal void RemovePresentation(string id, ITestPresentation presentation)
        {
            if (presentations.TryGetValue(id, out var current) && ReferenceEquals(current, presentation)) presentations.Remove(id);
        }

        internal void DispatchObjectChange(string typeId, object change)
        {
            if (!objectChangedHandlers.TryGetValue(typeId, out var handlers)) return;
            foreach (var handler in handlers.ToArray()) handler.DynamicInvoke(change);
        }

        internal void NotifyObjectChanged(ITestObject item, ReplicatedObjectChangeKind kind) =>
            item.PublishChange(this, kind);

        private void NotifyObjectDespawned(ITestObject item, TestObjectSnapshot snapshot) =>
            item.PublishChange(this, ReplicatedObjectChangeKind.Despawned);

        internal void RemoveObject(NetworkObjectId id, ITestObject item)
        {
            if (objects.TryGetValue(id, out var current) && ReferenceEquals(current, item)) objects.Remove(id);
        }

        internal void End()
        {
            if (ended) return;
            ended = true;
            currentSession.Cancel();
            foreach (var pending in pendingCommands.ToArray()) pending.Cancel("The multiplayer test session ended.");
            pendingCommands.Clear();
            ReplaceSnapshot(state: MultiplayerSessionState.Ended);
            foreach (var item in states.Values.ToArray()) item.Dispose();
            foreach (var item in commands.Values.ToArray()) item.Dispose();
            foreach (var item in objects.Values.ToArray()) item.Dispose();
            foreach (var item in objectTypes.Values.ToArray()) item.Dispose();
            foreach (var item in presentations.Values.ToArray()) item.Dispose();
            pendingObjectSnapshots.Clear();
            objectTombstones.Clear();
            objectChangedHandlers.Clear();
            changedHandlers.Clear();
            currentSession.Dispose();
        }

        private MultiplayerCommandConfirmation<T> FailedConfirmation<T>(ModErrorCode code, string message)
            where T : class =>
            new MultiplayerCommandConfirmation<T>(snapshot.Tick, snapshot.Tick, false, OperationResult<T>.Failure(code, message));

        private void ReplaceSnapshot(
            MultiplayerSessionState? state = null,
            IReadOnlyList<MultiplayerParticipant>? participants = null,
            NetworkTick? tick = null)
        {
            snapshot = new MultiplayerSessionSnapshot(
                snapshot.Id,
                state ?? snapshot.State,
                snapshot.ProcessKind,
                snapshot.ExecutionSides,
                snapshot.LocalParticipantId,
                participants ?? snapshot.Participants,
                tick ?? snapshot.Tick,
                snapshot.Seed);
            foreach (var handler in changedHandlers.ToArray()) handler(snapshot);
        }

        private void ReplaceConnectionSnapshot(
            ParticipantId? localParticipantId,
            IReadOnlyList<MultiplayerParticipant> participants,
            MultiplayerSessionState state)
        {
            snapshot = new MultiplayerSessionSnapshot(
                snapshot.Id,
                state,
                snapshot.ProcessKind,
                snapshot.ExecutionSides,
                localParticipantId,
                participants,
                snapshot.Tick,
                snapshot.Seed);
            foreach (var handler in changedHandlers.ToArray()) handler(snapshot);
        }

        private void CancelPendingWork(string message)
        {
            foreach (var pending in pendingCommands.ToArray()) pending.Cancel(message);
            pendingCommands.Clear();
            foreach (var item in states.Values) item.ResetToConfirmed();
            foreach (var item in objects.Values) item.BeginSynchronization();
        }

        private void ThrowIfEnded()
        {
            if (ended) throw new ObjectDisposedException(nameof(MultiplayerTestSession));
        }
    }
}
