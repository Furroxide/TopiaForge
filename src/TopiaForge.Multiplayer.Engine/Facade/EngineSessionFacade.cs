using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// The <see cref="IMultiplayerSession"/> one owner mod sees from one of its contexts: the mod's own, or a scope of it
    /// that resolves the session, such as a gamemode's server or a player's participant controller. Its registrations are
    /// keyed by the owner, so every facade of a mod sees the same states, commands and objects. Disposing the facade, or
    /// stopping the lifetime it is bound to, disposes everything registered through it and retires what it made: the
    /// objects it spawned and the commands it submitted that still wait. The owner's last facade retires the owner as a
    /// whole (see <see cref="SessionEngine.CloseFacade"/>). A closing facade refuses new work with
    /// <see cref="ModErrorCode.Cancelled"/>.
    /// </summary>
    internal sealed class EngineSessionFacade : IMultiplayerSession, IInternalAnswerDispatch, IDisposable
    {
        private const string ClosingMessage = "The mod is stopping and cannot change its multiplayer session.";
        private readonly SessionEngine engine;
        private readonly IModLifetime? lifetime;
        private readonly List<IDisposable> owned = new List<IDisposable>();
        private readonly object ownedGate = new object();
        private SnapshotPlayerDirectory? players;
        private int nextPrune = 32;
        private bool disposed;

        internal EngineSessionFacade(SessionEngine engine, string ownerModId, IModLifetime? lifetime = null)
        {
            this.engine = engine;
            this.lifetime = lifetime;
            OwnerModId = ownerModId;
            engine.OpenFacade(ownerModId);
            lifetime?.Track(this);
        }

        internal string OwnerModId { get; }

        internal SessionEngine Engine => engine;

        public MultiplayerSessionSnapshot Snapshot => engine.Snapshot;

        public CancellationToken CurrentSessionToken => engine.CurrentSessionToken;

        public IPlayerDirectory Players => players ??= new SnapshotPlayerDirectory(() => engine.Snapshot);

        /// <summary>Gets whether the facade refuses new work: it was disposed, or the mod lifetime it is bound to is stopping.</summary>
        internal bool IsClosing => disposed || (lifetime?.IsStopping ?? false);

        public IDisposable SubscribeChanged(Action<MultiplayerSessionSnapshot> handler)
        {
            ThrowIfClosing();
            return OwnSubscription(engine.SubscribeSession(handler));
        }

        public OperationResult<IReplicatedState<T>> RegisterState<T>(ReplicatedStateDefinition<T> definition) where T : class =>
            IsClosing ? Closing<IReplicatedState<T>>() : OwnResult(engine.RegisterState(OwnerModId, definition));

        public OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
            where TRequest : class
            where TResponse : class =>
            IsClosing ? Closing<IMultiplayerCommandRegistration>() : OwnResult(engine.RegisterCommand(OwnerModId, definition));

        public OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
            where TState : class
            where TInput : class =>
            IsClosing ? Closing<IReplicatedObjectTypeRegistration>() : OwnResult(engine.RegisterObjectType(OwnerModId, definition));

        public Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            if (!IsClosing) return engine.SubmitCommand(OwnerModId, commandType, request, cancellationToken, this);
            var tick = engine.CurrentTick;
            return Task.FromResult(new MultiplayerCommandConfirmation<TResponse>(
                tick,
                tick,
                false,
                OperationResult<TResponse>.Failure(ModErrorCode.Cancelled, ClosingMessage)));
        }

        /// <summary>
        /// Submits a command whose answer runs at the settle point that completes it, on the owner thread. Nothing is
        /// submitted, and nothing answers, once the mod is stopping, the binding is released or the caller has withdrawn.
        /// </summary>
        void IInternalAnswerDispatch.Submit<TRequest, TResponse>(
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            Action<MultiplayerCommandConfirmation<TResponse>> answered,
            CancellationToken ownerStopping,
            CancellationToken cancellationToken)
        {
            if (commandType == null) throw new ArgumentNullException(nameof(commandType));
            if (answered == null) throw new ArgumentNullException(nameof(answered));
            var answer = new CommandAnswer<TResponse>(this, answered, ownerStopping, cancellationToken, commandType.Id);
            if (!answer.IsWanted) return;
            engine.SubmitCommand(OwnerModId, commandType, request, cancellationToken, submitter: this, answer: answer);
        }

        public OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            TState initialState,
            ParticipantId? ownerId = null)
            where TState : class
            where TInput : class =>
            IsClosing ? Closing<IReplicatedObject<TState, TInput>>() : engine.SpawnObject(OwnerModId, type, initialState, ownerId, this);

        public OperationResult<bool> DespawnObject(NetworkObjectId id) =>
            IsClosing ? Closing<bool>() : engine.DespawnObject(OwnerModId, id);

        public IReadOnlyList<IReplicatedObject<TState, TInput>> GetObjects<TState, TInput>(ReplicatedObjectType<TState, TInput> type)
            where TState : class
            where TInput : class =>
            engine.GetObjects(OwnerModId, type);

        public bool TryGetObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            NetworkObjectId id,
            out IReplicatedObject<TState, TInput>? replicatedObject)
            where TState : class
            where TInput : class =>
            engine.TryGetObject(OwnerModId, type, id, out replicatedObject);

        public IDisposable SubscribeObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            Action<ReplicatedObjectChange<TState, TInput>> handler)
            where TState : class
            where TInput : class
        {
            ThrowIfClosing();
            return OwnSubscription(engine.SubscribeObjects(OwnerModId, type, handler));
        }

        public bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            return engine.TryGetNetworkObjectId(entity, out id);
        }

        public OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(PresentationEventDefinition<TEvent> definition)
            where TEvent : class =>
            IsClosing ? Closing<IPresentationEventRegistration>() : OwnResult(engine.RegisterPresentation(OwnerModId, definition));

        public OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            MultiplayerAudience audience = MultiplayerAudience.Everyone)
            where TEvent : class
        {
            if (!Enum.IsDefined(typeof(MultiplayerAudience), audience)) throw new ArgumentOutOfRangeException(nameof(audience));
            return PublishPresentation(eventType, value, PresentationTarget.FromAudience(audience));
        }

        public OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            PresentationTarget target)
            where TEvent : class =>
            IsClosing ? Closing<bool>() : engine.PublishPresentation(OwnerModId, eventType, value, target);

        public OperationResult<IDisposable> RegisterContract(MultiplayerContractDescriptor descriptor) =>
            IsClosing ? Closing<IDisposable>() : OwnResult(engine.RegisterContract(OwnerModId, descriptor));

        public IDisposable SubscribeTick(Action<MultiplayerTickContext> handler)
        {
            ThrowIfClosing();
            return OwnSubscription(engine.SubscribeTick(handler));
        }

        // Entities and avatars come from the runtime's sources (IInternalMultiplayerControl); until one is set, none exists.
        public bool TryGetEntity(NetworkObjectId id, out IEntity? entity) => engine.TryGetEntity(id, out entity);

        public bool TryGetAvatar(ParticipantId participantId, out IEntity? avatar) => engine.TryGetAvatar(participantId, out avatar);

        public bool TryGetPose(ParticipantId participantId, out MultiplayerPose pose) => engine.TryGetPose(participantId, out pose);

        /// <summary>
        /// Retires what this facade made, or the whole owner when it is the owner's last facade, and disposes every
        /// registration and subscription made through it.
        /// </summary>
        public void Dispose()
        {
            IDisposable[] leases;
            lock (ownedGate)
            {
                if (disposed) return;
                disposed = true;
                leases = owned.ToArray();
                owned.Clear();
            }

            engine.CloseFacade(this);
            for (var index = leases.Length - 1; index >= 0; index--) leases[index].Dispose();
        }

        private static OperationResult<T> Closing<T>() where T : notnull =>
            OperationResult<T>.Failure(ModErrorCode.Cancelled, ClosingMessage);

        private void ThrowIfClosing()
        {
            if (IsClosing) throw new ObjectDisposedException(nameof(IMultiplayerSession), ClosingMessage);
        }

        /// <summary>
        /// Takes ownership of a new registration. If the facade closed while it was being made, the registration is
        /// disposed and the caller gets <see cref="ModErrorCode.Cancelled"/> instead of a handle that is already dead.
        /// </summary>
        private OperationResult<T> OwnResult<T>(OperationResult<T> result) where T : notnull
        {
            if (result.TryGetValue(out var value) && value is IDisposable disposable && !TryOwn(disposable)) return Closing<T>();
            return result;
        }

        private IDisposable OwnSubscription(IDisposable subscription)
        {
            if (!TryOwn(subscription)) throw new ObjectDisposedException(nameof(IMultiplayerSession), ClosingMessage);
            return subscription;
        }

        private bool TryOwn(IDisposable disposable)
        {
            lock (ownedGate)
            {
                if (!disposed)
                {
                    // Registrations disposed one by one stay in the list until the next prune, so a mod that
                    // subscribes and unsubscribes for a whole session cannot grow it without bound.
                    if (owned.Count >= nextPrune)
                    {
                        owned.RemoveAll(IsRetired);
                        nextPrune = Math.Max(32, owned.Count * 2);
                    }

                    owned.Add(disposable);
                    return true;
                }
            }

            disposable.Dispose();
            return false;
        }

        private static bool IsRetired(IDisposable item) => item switch
        {
            EngineLease lease => lease.IsDisposed,
            StateRecord state => !state.IsActive,
            CommandBinding command => !command.IsActive,
            ObjectTypeBinding objectType => !objectType.IsActive,
            PresentationBinding presentation => !presentation.IsActive,
            _ => false
        };
    }
}
