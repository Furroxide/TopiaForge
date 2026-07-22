using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer
{
    internal sealed class LoopbackMultiplayerSession :
        IMultiplayerSession,
        IOwnerBoundExtensionFactory,
        IDisposable
    {
        // Calls made directly against the provider (primarily its own tests and
        // bootstrap code) live in a partition no mod facade can request.
        private const string ProviderOwnerModId = "\0topiaforge.loopback.provider";
        private readonly object gate = new object();
        private readonly object commandExecutionGate = new object();
        private readonly CancellationTokenSource ending = new CancellationTokenSource();
        private readonly Dictionary<LoopbackOwnerScopedId, object> states =
            new Dictionary<LoopbackOwnerScopedId, object>();
        private readonly Dictionary<LoopbackOwnerScopedId, ILoopbackCommand> commands =
            new Dictionary<LoopbackOwnerScopedId, ILoopbackCommand>();
        private readonly Dictionary<LoopbackOwnerScopedId, ILoopbackObjectTypeRegistration> objectTypes =
            new Dictionary<LoopbackOwnerScopedId, ILoopbackObjectTypeRegistration>();
        private readonly Dictionary<NetworkObjectId, ILoopbackReplicatedObject> objects =
            new Dictionary<NetworkObjectId, ILoopbackReplicatedObject>();
        private readonly Dictionary<LoopbackOwnerScopedId, List<Delegate>> objectHandlers =
            new Dictionary<LoopbackOwnerScopedId, List<Delegate>>();
        private readonly Dictionary<LoopbackOwnerScopedId, ILoopbackPresentationRegistration> presentations =
            new Dictionary<LoopbackOwnerScopedId, ILoopbackPresentationRegistration>();
        private readonly List<Action<MultiplayerSessionSnapshot>> changedHandlers = new List<Action<MultiplayerSessionSnapshot>>();
        private readonly ParticipantId localParticipantId;
        private readonly Func<long> timeMilliseconds;
        private MultiplayerSessionSnapshot snapshot;
        private ulong nextObjectId;
        private bool commandTransactionActive;
        private bool sessionObserverDispatchActive;
        private int sessionObserverDispatchThreadId;
        private bool disposed;

        internal LoopbackMultiplayerSession(string providerId)
            : this(providerId, MonotonicMilliseconds)
        {
        }

        internal LoopbackMultiplayerSession(string providerId, Func<long> timeMilliseconds)
        {
            this.timeMilliseconds = timeMilliseconds ?? throw new ArgumentNullException(nameof(timeMilliseconds));
            localParticipantId = new ParticipantId("local");
            snapshot = new MultiplayerSessionSnapshot(
                new MultiplayerSessionId("loopback:" + providerId),
                MultiplayerSessionState.Ready,
                MultiplayerProcessKind.Interactive,
                MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server,
                localParticipantId,
                new[] { new MultiplayerParticipant(localParticipantId, "Local player", true, true) },
                new NetworkTick(0),
                new SessionSeed(0x544f504941464f52UL));
        }

        public MultiplayerSessionSnapshot Snapshot
        {
            get { lock (gate) return snapshot; }
        }

        public CancellationToken CurrentSessionToken => ending.Token;

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime) =>
            CreateOwnerFacade(contractType, ownerModId, lifetime);

        internal object CreateOwnerFacade(Type contractType, string ownerModId, IModLifetime lifetime)
        {
            if (contractType == null) throw new ArgumentNullException(nameof(contractType));
            if (lifetime == null) throw new ArgumentNullException(nameof(lifetime));
            if (contractType != typeof(IMultiplayerSession))
            {
                throw new ArgumentException(
                    "The loopback multiplayer provider only supports owner facades for IMultiplayerSession.",
                    nameof(contractType));
            }

            if (string.IsNullOrWhiteSpace(ownerModId) || ownerModId.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("A valid owner mod id is required.", nameof(ownerModId));
            }
            return new LoopbackOwnerMultiplayerSession(this, ownerModId, lifetime);
        }

        public IDisposable SubscribeChanged(Action<MultiplayerSessionSnapshot> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (gate)
            {
                ThrowIfDisposed();
                changedHandlers.Add(handler);
                return new DelegateLease(() => { lock (gate) changedHandlers.Remove(handler); });
            }
        }

        public OperationResult<IReplicatedState<T>> RegisterState<T>(ReplicatedStateDefinition<T> definition)
            where T : class =>
            RegisterState(ProviderOwnerModId, definition);

        internal OperationResult<IReplicatedState<T>> RegisterState<T>(
            string ownerModId,
            ReplicatedStateDefinition<T> definition) where T : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var key = Scope(ownerModId, definition.Id);
            lock (gate)
            {
                ThrowIfDisposed();
                if (commandTransactionActive)
                {
                    return OperationResult<IReplicatedState<T>>.Failure(
                        ModErrorCode.InvalidState,
                        "Replicated-state registration is not allowed inside a command transaction.");
                }

                if (states.ContainsKey(key))
                {
                    return OperationResult<IReplicatedState<T>>.Failure(
                        ModErrorCode.Conflict,
                        "Replicated state '" + definition.Id + "' is already registered.");
                }

                var cloned = CodecRoundTrip(definition.Codec, definition.InitialValue);
                if (!cloned.TryGetValue(out var initial))
                {
                    return OperationResult<IReplicatedState<T>>.Failure(cloned.ErrorCode, cloned.ErrorMessage);
                }

                var state = new LoopbackReplicatedState<T>(
                    definition.Id,
                    initial,
                    definition.Codec,
                    (id, value) => RemoveState(ownerModId, id, value));
                states.Add(key, state);
                return OperationResult<IReplicatedState<T>>.Success(state);
            }
        }

        public OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
            where TRequest : class
            where TResponse : class =>
            RegisterCommand(ProviderOwnerModId, definition);

        internal OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
            string ownerModId,
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
            where TRequest : class
            where TResponse : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var key = Scope(ownerModId, definition.Id);
            lock (gate)
            {
                ThrowIfDisposed();
                if (commandTransactionActive)
                {
                    return OperationResult<IMultiplayerCommandRegistration>.Failure(
                        ModErrorCode.InvalidState,
                        "Command registration is not allowed inside a command transaction.");
                }

                if (commands.ContainsKey(key))
                {
                    return OperationResult<IMultiplayerCommandRegistration>.Failure(
                        ModErrorCode.Conflict,
                        "Multiplayer command '" + definition.Id + "' is already registered.");
                }

                var registration = new LoopbackCommand<TRequest, TResponse>(
                    definition,
                    (id, value) => RemoveCommand(ownerModId, id, value));
                commands.Add(key, registration);
                return OperationResult<IMultiplayerCommandRegistration>.Success(registration);
            }
        }

        public OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
            where TState : class
            where TInput : class =>
            RegisterObjectType(ProviderOwnerModId, definition);

        internal OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
            string ownerModId,
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
            where TState : class
            where TInput : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var key = Scope(ownerModId, definition.TypeId);
            lock (gate)
            {
                ThrowIfDisposed();
                if (commandTransactionActive)
                {
                    return OperationResult<IReplicatedObjectTypeRegistration>.Failure(
                        ModErrorCode.InvalidState,
                        "Replicated-object type registration is not allowed inside a command transaction.");
                }

                if (objectTypes.ContainsKey(key))
                {
                    return OperationResult<IReplicatedObjectTypeRegistration>.Failure(
                        ModErrorCode.Conflict,
                        "Replicated-object type '" + definition.TypeId + "' is already registered.");
                }

                var registration = new LoopbackObjectTypeRegistration<TState, TInput>(
                    definition,
                    (id, value) => RemoveObjectType(ownerModId, id, value));
                objectTypes.Add(key, registration);
                return OperationResult<IReplicatedObjectTypeRegistration>.Success(registration);
            }
        }

        public Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class =>
            SubmitAsync(ProviderOwnerModId, commandType, request, cancellationToken);

        internal Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(
            string ownerModId,
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (commandType == null) throw new ArgumentNullException(nameof(commandType));
            if (request == null) throw new ArgumentNullException(nameof(request));
            var key = Scope(ownerModId, commandType.Id);
            SessionChangeNotification sessionChange;
            MultiplayerCommandConfirmation<TResponse> confirmation;
            lock (commandExecutionGate)
            {
                if (!WaitForSessionObserverDispatchLocked())
                {
                    var currentTick = Snapshot.Tick;
                    return Task.FromResult(new MultiplayerCommandConfirmation<TResponse>(
                        currentTick,
                        currentTick,
                        false,
                        OperationResult<TResponse>.Failure(
                            ModErrorCode.InvalidState,
                            "Commands cannot be submitted synchronously from a session-change observer.")));
                }

                ILoopbackCommand command;
                NetworkTick submittedAt;
                lock (gate)
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (commandTransactionActive)
                    {
                        return Task.FromResult(new MultiplayerCommandConfirmation<TResponse>(
                            snapshot.Tick,
                            snapshot.Tick,
                            false,
                            OperationResult<TResponse>.Failure(
                                ModErrorCode.InvalidState,
                                "Nested command submission is not allowed inside a command transaction.")));
                    }

                    if (!commands.TryGetValue(key, out command!))
                    {
                        return Task.FromResult(new MultiplayerCommandConfirmation<TResponse>(
                            snapshot.Tick,
                            snapshot.Tick,
                            false,
                            OperationResult<TResponse>.Failure(ModErrorCode.NotFound, "Multiplayer command '" + commandType.Id + "' is not registered.")));
                    }

                    submittedAt = snapshot.Tick;
                }

                if (command is not LoopbackCommand<TRequest, TResponse> typed)
                {
                    return Task.FromResult(new MultiplayerCommandConfirmation<TResponse>(
                        submittedAt,
                        Snapshot.Tick,
                        false,
                        OperationResult<TResponse>.Failure(ModErrorCode.InvalidArgument, "The command payload type does not match its generated contract.")));
                }

                var acquired = typed.TryAcquire(localParticipantId, timeMilliseconds());
                if (!acquired.Succeeded)
                {
                    return Task.FromResult(new MultiplayerCommandConfirmation<TResponse>(
                        submittedAt,
                        Snapshot.Tick,
                        false,
                        OperationResult<TResponse>.Failure(acquired.ErrorCode, acquired.ErrorMessage)));
                }

                NetworkTick confirmedAt;
                lock (gate)
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!commands.TryGetValue(key, out var current) || !ReferenceEquals(current, typed))
                    {
                        return Task.FromResult(new MultiplayerCommandConfirmation<TResponse>(
                            snapshot.Tick,
                            snapshot.Tick,
                            false,
                            OperationResult<TResponse>.Failure(
                                ModErrorCode.NotFound,
                                "Multiplayer command '" + commandType.Id + "' is no longer registered.")));
                    }

                    commandTransactionActive = true;
                    sessionChange = AdvanceTickLocked();
                    confirmedAt = sessionChange.Snapshot.Tick;
                }

                var transactions = BeginStateTransaction(ownerModId);
                if (!transactions.TryGetValue(out var transactionalStates))
                {
                    lock (gate) commandTransactionActive = false;
                    confirmation = new MultiplayerCommandConfirmation<TResponse>(
                        submittedAt,
                        confirmedAt,
                        false,
                        OperationResult<TResponse>.Failure(transactions.ErrorCode, transactions.ErrorMessage));
                }
                else
                {
                    var events = new List<BufferedPresentation>();
                    var context = new MultiplayerCommandContext(
                        localParticipantId,
                        confirmedAt,
                        cancellationToken,
                        (id, bytes, audience) => BufferPresentation(ownerModId, events, id, bytes, audience));
                    OperationResult<TResponse> result;
                    try
                    {
                        result = typed.Invoke(context, request);
                    }
                    catch (Exception exception)
                    {
                        result = CommandFailure<TResponse>(exception, cancellationToken);
                    }
                    finally
                    {
                        lock (gate) commandTransactionActive = false;
                    }

                    if (result.Succeeded)
                    {
                        CommitStateTransaction(transactionalStates);
                        foreach (var item in events)
                        {
                            DispatchPresentation(ownerModId, item.Id, item.Bytes, item.Audience, localParticipantId);
                        }
                    }
                    else
                    {
                        RollbackStateTransaction(transactionalStates);
                    }

                    confirmation = new MultiplayerCommandConfirmation<TResponse>(
                        submittedAt,
                        confirmedAt,
                        false,
                        result);
                }

                BeginSessionObserverDispatch(sessionChange);
            }

            DispatchSessionChange(sessionChange);
            return Task.FromResult(confirmation);
        }

        public OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            TState initialState,
            ParticipantId? ownerId = null)
            where TState : class
            where TInput : class =>
            SpawnObject(ProviderOwnerModId, type, initialState, ownerId);

        internal OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
            string ownerModId,
            ReplicatedObjectType<TState, TInput> type,
            TState initialState,
            ParticipantId? ownerId = null)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (initialState == null) throw new ArgumentNullException(nameof(initialState));
            var typeKey = Scope(ownerModId, type.Id);
            LoopbackReplicatedObject<TState, TInput> item;
            lock (gate)
            {
                ThrowIfDisposed();
                if (commandTransactionActive)
                {
                    return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                        ModErrorCode.InvalidState,
                        "Replicated-object spawning is not allowed inside a command transaction.");
                }

                if (ownerId.HasValue && !ownerId.Value.Equals(localParticipantId))
                {
                    return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                        ModErrorCode.NotFound,
                        "The requested owner is not an admitted loopback participant.");
                }

                if (!objectTypes.TryGetValue(typeKey, out var registered))
                {
                    return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                        ModErrorCode.NotFound,
                        "Replicated-object type '" + type.Id + "' is not registered.");
                }

                if (registered is not LoopbackObjectTypeRegistration<TState, TInput> registration)
                {
                    return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The replicated-object type does not match its registered generated contract.");
                }

                var cloned = CodecRoundTrip(registration.Definition.StateCodec, initialState);
                if (!cloned.TryGetValue(out var state))
                {
                    return OperationResult<IReplicatedObject<TState, TInput>>.Failure(cloned.ErrorCode, cloned.ErrorMessage);
                }

                item = new LoopbackReplicatedObject<TState, TInput>(
                    this,
                    ownerModId,
                    new NetworkObjectId("loopback-object-" + (++nextObjectId).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ownerId,
                    state,
                    registration);
                objects.Add(item.Id, item);
            }

            DispatchObjectChange(item, ReplicatedObjectChangeKind.Spawned);
            return OperationResult<IReplicatedObject<TState, TInput>>.Success(item);
        }

        public OperationResult<bool> DespawnObject(NetworkObjectId id) =>
            DespawnObject(ProviderOwnerModId, id);

        internal OperationResult<bool> DespawnObject(string ownerModId, NetworkObjectId id)
        {
            ILoopbackReplicatedObject item;
            lock (gate)
            {
                ThrowIfDisposed();
                if (commandTransactionActive)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "Replicated-object despawning is not allowed inside a command transaction.");
                }

                if (!objects.TryGetValue(id, out item!) ||
                    !string.Equals(item.OwnerModId, ownerModId, StringComparison.Ordinal))
                {
                    return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The replicated object was not found.");
                }

                objects.Remove(id);
            }

            var createChange = item.CreateChangeFactory(ReplicatedObjectChangeKind.Despawned);
            item.Dispose();
            DispatchObjectChange(
                ownerModId,
                item.TypeId,
                createChange);
            return OperationResult<bool>.Success(true);
        }

        public IReadOnlyList<IReplicatedObject<TState, TInput>> GetObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type)
            where TState : class
            where TInput : class =>
            GetObjects(ProviderOwnerModId, type);

        internal IReadOnlyList<IReplicatedObject<TState, TInput>> GetObjects<TState, TInput>(
            string ownerModId,
            ReplicatedObjectType<TState, TInput> type)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            lock (gate)
            {
                ThrowIfDisposed();
                return objects.Values
                    .Where(item =>
                        string.Equals(item.OwnerModId, ownerModId, StringComparison.Ordinal) &&
                        string.Equals(item.TypeId, type.Id, StringComparison.Ordinal))
                    .OfType<IReplicatedObject<TState, TInput>>()
                    .ToArray();
            }
        }

        public bool TryGetObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            NetworkObjectId id,
            out IReplicatedObject<TState, TInput>? replicatedObject)
            where TState : class
            where TInput : class =>
            TryGetObject(ProviderOwnerModId, type, id, out replicatedObject);

        internal bool TryGetObject<TState, TInput>(
            string ownerModId,
            ReplicatedObjectType<TState, TInput> type,
            NetworkObjectId id,
            out IReplicatedObject<TState, TInput>? replicatedObject)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            lock (gate)
            {
                ThrowIfDisposed();
                if (objects.TryGetValue(id, out var candidate) &&
                    string.Equals(candidate.OwnerModId, ownerModId, StringComparison.Ordinal) &&
                    string.Equals(candidate.TypeId, type.Id, StringComparison.Ordinal) &&
                    candidate is IReplicatedObject<TState, TInput> typed)
                {
                    replicatedObject = typed;
                    return true;
                }

                replicatedObject = null;
                return false;
            }
        }

        public IDisposable SubscribeObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            Action<ReplicatedObjectChange<TState, TInput>> handler)
            where TState : class
            where TInput : class =>
            SubscribeObjects(ProviderOwnerModId, type, handler);

        internal IDisposable SubscribeObjects<TState, TInput>(
            string ownerModId,
            ReplicatedObjectType<TState, TInput> type,
            Action<ReplicatedObjectChange<TState, TInput>> handler)
            where TState : class
            where TInput : class
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var key = Scope(ownerModId, type.Id);
            lock (gate)
            {
                ThrowIfDisposed();
                if (!objectHandlers.TryGetValue(key, out var handlers))
                {
                    handlers = new List<Delegate>();
                    objectHandlers.Add(key, handlers);
                }

                handlers.Add(handler);
                return new DelegateLease(() =>
                {
                    lock (gate)
                    {
                        if (!objectHandlers.TryGetValue(key, out var current)) return;
                        current.Remove(handler);
                        if (current.Count == 0) objectHandlers.Remove(key);
                    }
                });
            }
        }

        public bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id)
            => TryGetNetworkObjectId(ProviderOwnerModId, entity, out id);

        internal bool TryGetNetworkObjectId(string ownerModId, IEntity entity, out NetworkObjectId id)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            id = default;
            return false;
        }

        public OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(
            PresentationEventDefinition<TEvent> definition) where TEvent : class =>
            RegisterPresentation(ProviderOwnerModId, definition);

        internal OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(
            string ownerModId,
            PresentationEventDefinition<TEvent> definition) where TEvent : class
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            var key = Scope(ownerModId, definition.Type.Id);
            lock (gate)
            {
                ThrowIfDisposed();
                if (commandTransactionActive)
                {
                    return OperationResult<IPresentationEventRegistration>.Failure(
                        ModErrorCode.InvalidState,
                        "Presentation-event registration is not allowed inside a command transaction.");
                }

                if (presentations.ContainsKey(key))
                {
                    return OperationResult<IPresentationEventRegistration>.Failure(
                        ModErrorCode.Conflict,
                        "Presentation event '" + definition.Type.Id + "' is already registered.");
                }

                var registration = new LoopbackPresentationRegistration<TEvent>(
                    definition,
                    (id, value) => RemovePresentation(ownerModId, id, value));
                presentations.Add(key, registration);
                return OperationResult<IPresentationEventRegistration>.Success(registration);
            }
        }

        public OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            MultiplayerAudience audience = MultiplayerAudience.Everyone) where TEvent : class
            => PublishPresentation(ProviderOwnerModId, eventType, value, audience);

        internal OperationResult<bool> PublishPresentation<TEvent>(
            string ownerModId,
            PresentationEventType<TEvent> eventType,
            TEvent value,
            MultiplayerAudience audience = MultiplayerAudience.Everyone) where TEvent : class
        {
            if (eventType == null) throw new ArgumentNullException(nameof(eventType));
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (!Enum.IsDefined(typeof(MultiplayerAudience), audience)) throw new ArgumentOutOfRangeException(nameof(audience));
            lock (gate)
            {
                ThrowIfDisposed();
                if (commandTransactionActive)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "Direct presentation publishing is not allowed inside a command transaction; use the command context.");
                }
            }

            try
            {
                var encoded = eventType.Codec.Encode(value);
                if (!encoded.TryGetValue(out var bytes))
                    return OperationResult<bool>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
                if (bytes.Length > eventType.Codec.MaximumEncodedBytes)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The presentation-event codec exceeded its declared maximum size.");
                }

                return DispatchPresentation(ownerModId, eventType.Id, bytes, audience, localParticipantId);
            }
            catch (Exception exception)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.Unknown,
                    "Presentation-event processing threw " + exception.GetType().Name + ".");
            }
        }

        public void Dispose()
        {
            List<IDisposable> owned;
            Action<MultiplayerSessionSnapshot>[] changed;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                ending.Cancel();
                snapshot = new MultiplayerSessionSnapshot(
                    snapshot.Id,
                    MultiplayerSessionState.Ended,
                    snapshot.ProcessKind,
                    snapshot.ExecutionSides,
                    snapshot.LocalParticipantId,
                    snapshot.Participants,
                    snapshot.Tick,
                    snapshot.Seed);
                changed = changedHandlers.ToArray();
                changedHandlers.Clear();
                owned = states.Values.OfType<IDisposable>()
                    .Concat(commands.Values.OfType<IDisposable>())
                    .Concat(objectTypes.Values.OfType<IDisposable>())
                    .Concat(objects.Values.OfType<IDisposable>())
                    .Concat(presentations.Values.OfType<IDisposable>())
                    .ToList();
                states.Clear();
                commands.Clear();
                objectTypes.Clear();
                objects.Clear();
                objectHandlers.Clear();
                presentations.Clear();
            }

            foreach (var handler in changed) handler(snapshot);
            for (var index = owned.Count - 1; index >= 0; index--) owned[index].Dispose();
            ending.Dispose();
        }

        internal MultiplayerCommandConfirmation<TState> ApplyObjectInput<TState, TInput>(
            LoopbackReplicatedObject<TState, TInput> item,
            TInput input,
            CancellationToken cancellationToken)
            where TState : class
            where TInput : class
        {
            SessionChangeNotification sessionChange;
            MultiplayerCommandConfirmation<TState> confirmation;
            lock (commandExecutionGate)
            {
                if (!WaitForSessionObserverDispatchLocked())
                {
                    var currentTick = Snapshot.Tick;
                    return new MultiplayerCommandConfirmation<TState>(
                        currentTick,
                        currentTick,
                        false,
                        OperationResult<TState>.Failure(
                            ModErrorCode.InvalidState,
                            "Replicated-object inputs cannot be submitted synchronously from a session-change observer."));
                }

                NetworkTick submitted;
                lock (gate)
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (commandTransactionActive)
                    {
                        return new MultiplayerCommandConfirmation<TState>(
                            snapshot.Tick,
                            snapshot.Tick,
                            false,
                            OperationResult<TState>.Failure(
                                ModErrorCode.InvalidState,
                                "Nested object input is not allowed inside a command transaction."));
                    }

                    submitted = snapshot.Tick;
                }

                var acquired = item.TryAcquire(localParticipantId, timeMilliseconds());
                if (!acquired.Succeeded)
                {
                    return new MultiplayerCommandConfirmation<TState>(
                        submitted,
                        Snapshot.Tick,
                        false,
                        OperationResult<TState>.Failure(acquired.ErrorCode, acquired.ErrorMessage));
                }

                NetworkTick confirmedAt;
                lock (gate)
                {
                    ThrowIfDisposed();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!objects.TryGetValue(item.Id, out var current) || !ReferenceEquals(current, item))
                    {
                        return new MultiplayerCommandConfirmation<TState>(
                            snapshot.Tick,
                            snapshot.Tick,
                            false,
                            OperationResult<TState>.Failure(
                                ModErrorCode.NotFound,
                                "The replicated object is no longer spawned."));
                    }

                    commandTransactionActive = true;
                    sessionChange = AdvanceTickLocked();
                    confirmedAt = sessionChange.Snapshot.Tick;
                }

                var transactions = BeginStateTransaction(item.OwnerModId);
                if (!transactions.TryGetValue(out var transactionalStates))
                {
                    lock (gate) commandTransactionActive = false;
                    confirmation = new MultiplayerCommandConfirmation<TState>(
                        submitted,
                        confirmedAt,
                        false,
                        OperationResult<TState>.Failure(transactions.ErrorCode, transactions.ErrorMessage));
                }
                else
                {
                    var events = new List<BufferedPresentation>();
                    var context = new ReplicatedObjectCommandContext(
                        localParticipantId,
                        item.Id,
                        confirmedAt,
                        item.OwnerId.HasValue && item.OwnerId.Value.Equals(localParticipantId),
                        cancellationToken,
                        (id, bytes, audience) => BufferPresentation(item.OwnerModId, events, id, bytes, audience));
                    OperationResult<TState> result;
                    try
                    {
                        result = item.Apply(context, input);
                    }
                    catch (Exception exception)
                    {
                        result = CommandFailure<TState>(exception, cancellationToken);
                    }
                    finally
                    {
                        lock (gate) commandTransactionActive = false;
                    }

                    if (result.Succeeded)
                    {
                        CommitStateTransaction(transactionalStates);
                        NotifyObjectChanged(item);
                        foreach (var presentation in events)
                        {
                            DispatchPresentation(
                                item.OwnerModId,
                                presentation.Id,
                                presentation.Bytes,
                                presentation.Audience,
                                localParticipantId);
                        }
                    }
                    else
                    {
                        RollbackStateTransaction(transactionalStates);
                    }

                    confirmation = new MultiplayerCommandConfirmation<TState>(submitted, confirmedAt, false, result);
                }

                BeginSessionObserverDispatch(sessionChange);
            }

            DispatchSessionChange(sessionChange);
            return confirmation;
        }

        internal bool IsParticipant(ParticipantId id) => id.Equals(localParticipantId);

        internal bool CanMutateObjectGraph
        {
            get { lock (gate) return !commandTransactionActive && !disposed; }
        }

        internal void NotifyObjectChanged<TState, TInput>(LoopbackReplicatedObject<TState, TInput> item)
            where TState : class
            where TInput : class =>
            DispatchObjectChange(item, ReplicatedObjectChangeKind.Changed);

        // The caller holds gate. Advancing canonical time and selecting the observers are atomic, but user callbacks
        // are deliberately deferred until the accepted command has committed or rolled back and all canonical locks
        // have been released.
        private SessionChangeNotification AdvanceTickLocked()
        {
            snapshot = new MultiplayerSessionSnapshot(
                snapshot.Id,
                snapshot.State,
                snapshot.ProcessKind,
                snapshot.ExecutionSides,
                snapshot.LocalParticipantId,
                snapshot.Participants,
                new NetworkTick(snapshot.Tick.Value + 1),
                snapshot.Seed);
            return new SessionChangeNotification(snapshot, changedHandlers.ToArray());
        }

        // Session observers are a post-settlement notification boundary. Cross-thread submissions wait for that
        // boundary to finish; a synchronous submission from the observer itself is rejected so it cannot reenter
        // command processing or make later observers see a different canonical state for the same tick.
        private bool WaitForSessionObserverDispatchLocked()
        {
            var currentThreadId = Thread.CurrentThread.ManagedThreadId;
            if (sessionObserverDispatchActive && sessionObserverDispatchThreadId == currentThreadId)
            {
                return false;
            }

            while (sessionObserverDispatchActive)
            {
                Monitor.Wait(commandExecutionGate);
            }

            return true;
        }

        private void BeginSessionObserverDispatch(SessionChangeNotification change)
        {
            if (change.Handlers.Length == 0) return;
            sessionObserverDispatchActive = true;
            sessionObserverDispatchThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        private void DispatchSessionChange(SessionChangeNotification change)
        {
            if (change.Handlers.Length == 0) return;
            try
            {
                foreach (var handler in change.Handlers)
                {
                    try
                    {
                        handler(change.Snapshot);
                    }
                    catch (Exception)
                    {
                        // Session observers cannot interrupt an already settled canonical command.
                    }
                }
            }
            finally
            {
                lock (commandExecutionGate)
                {
                    sessionObserverDispatchActive = false;
                    sessionObserverDispatchThreadId = 0;
                    Monitor.PulseAll(commandExecutionGate);
                }
            }
        }

        private OperationResult<bool> BufferPresentation(
            string ownerModId,
            List<BufferedPresentation> buffer,
            string id,
            byte[] bytes,
            MultiplayerAudience audience)
        {
            var key = Scope(ownerModId, id);
            lock (gate)
            {
                if (!presentations.ContainsKey(key))
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.NotFound,
                        "Presentation event '" + id + "' is not registered.");
                }
            }

            buffer.Add(new BufferedPresentation(id, (byte[])bytes.Clone(), audience));
            return OperationResult<bool>.Success(true);
        }

        private OperationResult<bool> DispatchPresentation(
            string ownerModId,
            string id,
            byte[] bytes,
            MultiplayerAudience audience,
            ParticipantId sender)
        {
            if (audience == MultiplayerAudience.Others) return OperationResult<bool>.Success(true);
            var key = Scope(ownerModId, id);
            ILoopbackPresentationRegistration registration;
            lock (gate)
            {
                if (!presentations.TryGetValue(key, out registration!))
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.NotFound,
                        "Presentation event '" + id + "' is not registered.");
                }
            }

            try
            {
                return registration.Dispatch(bytes);
            }
            catch (Exception exception)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.Unknown,
                    "Presentation-event processing threw " + exception.GetType().Name + ".");
            }
        }

        private void DispatchObjectChange<TState, TInput>(
            LoopbackReplicatedObject<TState, TInput> item,
            ReplicatedObjectChangeKind kind)
            where TState : class
            where TInput : class =>
            DispatchObjectChange(item.OwnerModId, item.TypeId, item.CreateChangeFactory(kind));

        private void DispatchObjectChange(string ownerModId, string typeId, Func<object> createChange)
        {
            if (createChange == null) throw new ArgumentNullException(nameof(createChange));
            var key = Scope(ownerModId, typeId);
            Delegate[] handlers;
            lock (gate)
            {
                if (!objectHandlers.TryGetValue(key, out var registered)) return;
                handlers = registered.ToArray();
            }

            foreach (var handler in handlers)
            {
                try
                {
                    handler.DynamicInvoke(createChange());
                }
                catch (Exception)
                {
                    // Discovery observer failures cannot affect canonical object state.
                }
            }
        }

        private void RemoveState(string ownerModId, string id, object state)
        {
            var key = Scope(ownerModId, id);
            lock (gate)
            {
                if (states.TryGetValue(key, out var current) && ReferenceEquals(current, state)) states.Remove(key);
            }
        }

        private void RemoveCommand(string ownerModId, string id, ILoopbackCommand command)
        {
            var key = Scope(ownerModId, id);
            lock (gate)
            {
                if (commands.TryGetValue(key, out var current) && ReferenceEquals(current, command)) commands.Remove(key);
            }
        }

        private void RemoveObjectType(
            string ownerModId,
            string typeId,
            ILoopbackObjectTypeRegistration registration)
        {
            var key = Scope(ownerModId, typeId);
            ILoopbackReplicatedObject[] removed;
            lock (gate)
            {
                if (!objectTypes.TryGetValue(key, out var current) || !ReferenceEquals(current, registration))
                {
                    return;
                }

                objectTypes.Remove(key);
                removed = objects.Values
                    .Where(item =>
                        string.Equals(item.OwnerModId, ownerModId, StringComparison.Ordinal) &&
                        string.Equals(item.TypeId, typeId, StringComparison.Ordinal))
                    .ToArray();
                foreach (var item in removed) objects.Remove(item.Id);
            }

            foreach (var item in removed)
            {
                var createChange = item.CreateChangeFactory(ReplicatedObjectChangeKind.Despawned);
                item.Dispose();
                DispatchObjectChange(
                    ownerModId,
                    typeId,
                    createChange);
            }
        }

        private void RemovePresentation(
            string ownerModId,
            string id,
            ILoopbackPresentationRegistration registration)
        {
            var key = Scope(ownerModId, id);
            lock (gate)
            {
                if (presentations.TryGetValue(key, out var current) && ReferenceEquals(current, registration))
                {
                    presentations.Remove(key);
                }
            }
        }

        private static OperationResult<T> CodecRoundTrip<T>(IMultiplayerCodec<T> codec, T value) where T : class
        {
            var encoded = codec.Encode(value);
            if (!encoded.TryGetValue(out var bytes)) return OperationResult<T>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            if (bytes.Length > codec.MaximumEncodedBytes)
                return OperationResult<T>.Failure(ModErrorCode.InvalidArgument, "The codec exceeded its declared maximum size.");
            return codec.Decode((byte[])bytes.Clone());
        }

        private static LoopbackOwnerScopedId Scope(string ownerModId, string publicId) =>
            new LoopbackOwnerScopedId(ownerModId, publicId);

        private OperationResult<ILoopbackTransactionalState[]> BeginStateTransaction(string ownerModId)
        {
            ILoopbackTransactionalState[] transactionalStates;
            lock (gate)
            {
                transactionalStates = states
                    .Where(pair => string.Equals(pair.Key.OwnerModId, ownerModId, StringComparison.Ordinal))
                    .Select(pair => pair.Value)
                    .OfType<ILoopbackTransactionalState>()
                    .ToArray();
            }

            var begun = 0;
            try
            {
                for (; begun < transactionalStates.Length; begun++)
                {
                    var result = transactionalStates[begun].BeginTransaction();
                    if (result.Succeeded) continue;
                    RollbackStateTransaction(transactionalStates, begun);
                    return OperationResult<ILoopbackTransactionalState[]>.Failure(
                        result.ErrorCode,
                        result.ErrorMessage);
                }
            }
            catch (Exception exception)
            {
                RollbackStateTransaction(transactionalStates, begun);
                return OperationResult<ILoopbackTransactionalState[]>.Failure(
                    ModErrorCode.Unknown,
                    "A replicated-state transaction could not start: " + exception.GetType().Name + ".");
            }

            return OperationResult<ILoopbackTransactionalState[]>.Success(transactionalStates);
        }

        private static void CommitStateTransaction(ILoopbackTransactionalState[] transactionalStates)
        {
            var notifications = new List<Action>(transactionalStates.Length);
            foreach (var state in transactionalStates)
            {
                var notification = state.CommitTransaction();
                if (notification != null) notifications.Add(notification);
            }

            foreach (var notification in notifications)
            {
                try
                {
                    notification();
                }
                catch (Exception)
                {
                    // Subscriber failures cannot reject an already committed canonical command.
                }
            }
        }

        private static void RollbackStateTransaction(
            ILoopbackTransactionalState[] transactionalStates,
            int count = -1)
        {
            var length = count < 0 ? transactionalStates.Length : count;
            for (var index = length - 1; index >= 0; index--)
            {
                transactionalStates[index].RollbackTransaction();
            }
        }

        private static OperationResult<T> CommandFailure<T>(Exception exception, CancellationToken cancellationToken)
            where T : class
        {
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                return OperationResult<T>.Failure(ModErrorCode.Cancelled, "The multiplayer command was cancelled.");
            }

            return OperationResult<T>.Failure(
                ModErrorCode.Unknown,
                "Multiplayer command processing threw " + exception.GetType().Name + ".");
        }

        private static long MonotonicMilliseconds() =>
            (long)(Stopwatch.GetTimestamp() * (1000d / Stopwatch.Frequency));

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LoopbackMultiplayerSession));
        }

        private sealed class SessionChangeNotification
        {
            internal SessionChangeNotification(
                MultiplayerSessionSnapshot snapshot,
                Action<MultiplayerSessionSnapshot>[] handlers)
            {
                Snapshot = snapshot;
                Handlers = handlers;
            }

            internal MultiplayerSessionSnapshot Snapshot { get; }
            internal Action<MultiplayerSessionSnapshot>[] Handlers { get; }
        }

        private sealed class BufferedPresentation
        {
            internal BufferedPresentation(string id, byte[] bytes, MultiplayerAudience audience)
            {
                Id = id;
                Bytes = bytes;
                Audience = audience;
            }

            internal string Id { get; }
            internal byte[] Bytes { get; }
            internal MultiplayerAudience Audience { get; }
        }
    }
}
