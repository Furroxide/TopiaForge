using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// The <see cref="IMultiplayerSession"/> one owner mod sees. Its registrations are keyed by the owner, and
    /// disposing the facade disposes everything registered through it.
    /// </summary>
    internal sealed class EngineSessionFacade : IMultiplayerSession, IDisposable
    {
        private readonly SessionEngine engine;
        private readonly List<IDisposable> owned = new List<IDisposable>();
        private readonly object ownedGate = new object();
        private SnapshotPlayerDirectory? players;
        private bool disposed;

        internal EngineSessionFacade(SessionEngine engine, string ownerModId)
        {
            this.engine = engine;
            OwnerModId = ownerModId;
        }

        internal string OwnerModId { get; }

        internal SessionEngine Engine => engine;

        public MultiplayerSessionSnapshot Snapshot => engine.Snapshot;

        public CancellationToken CurrentSessionToken => engine.CurrentSessionToken;

        public IPlayerDirectory Players => players ??= new SnapshotPlayerDirectory(() => engine.Snapshot);

        public IDisposable SubscribeChanged(Action<MultiplayerSessionSnapshot> handler) => Own(engine.SubscribeSession(handler));

        public OperationResult<IReplicatedState<T>> RegisterState<T>(ReplicatedStateDefinition<T> definition) where T : class =>
            OwnResult(engine.RegisterState(OwnerModId, definition));

        public OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
            where TRequest : class
            where TResponse : class =>
            OwnResult(engine.RegisterCommand(OwnerModId, definition));

        public OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
            where TState : class
            where TInput : class =>
            OwnResult(engine.RegisterObjectType(OwnerModId, definition));

        public Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class =>
            engine.SubmitCommand(OwnerModId, commandType, request, cancellationToken);

        public OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            TState initialState,
            ParticipantId? ownerId = null)
            where TState : class
            where TInput : class =>
            engine.SpawnObject(OwnerModId, type, initialState, ownerId);

        public OperationResult<bool> DespawnObject(NetworkObjectId id) => engine.DespawnObject(OwnerModId, id);

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
            where TInput : class =>
            Own(engine.SubscribeObjects(OwnerModId, type, handler));

        public bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            id = default;
            return false;
        }

        public OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(PresentationEventDefinition<TEvent> definition)
            where TEvent : class =>
            OwnResult(engine.RegisterPresentation(OwnerModId, definition));

        public OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            MultiplayerAudience audience = MultiplayerAudience.Everyone)
            where TEvent : class
        {
            if (!Enum.IsDefined(typeof(MultiplayerAudience), audience)) throw new ArgumentOutOfRangeException(nameof(audience));
            return engine.PublishPresentation(OwnerModId, eventType, value, PresentationTarget.FromAudience(audience));
        }

        public OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            PresentationTarget target)
            where TEvent : class =>
            engine.PublishPresentation(OwnerModId, eventType, value, target);

        public OperationResult<IDisposable> RegisterContract(MultiplayerContractDescriptor descriptor) =>
            OwnResult(engine.RegisterContract(OwnerModId, descriptor));

        public IDisposable SubscribeTick(Action<MultiplayerTickContext> handler) => Own(engine.SubscribeTick(handler));

        public bool TryGetEntity(NetworkObjectId id, out IEntity? entity)
        {
            entity = null;
            return false;
        }

        public bool TryGetAvatar(ParticipantId participantId, out IEntity? avatar)
        {
            avatar = null;
            return false;
        }

        public bool TryGetPose(ParticipantId participantId, out MultiplayerPose pose)
        {
            pose = default;
            return false;
        }

        /// <summary>Disposes every registration and subscription made through this facade.</summary>
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

            for (var index = leases.Length - 1; index >= 0; index--) leases[index].Dispose();
        }

        private OperationResult<T> OwnResult<T>(OperationResult<T> result) where T : notnull
        {
            if (result.TryGetValue(out var value) && value is IDisposable disposable) Own(disposable);
            return result;
        }

        private TDisposable Own<TDisposable>(TDisposable disposable) where TDisposable : IDisposable
        {
            lock (ownedGate)
            {
                if (!disposed)
                {
                    owned.Add(disposable);
                    return disposable;
                }
            }

            disposable.Dispose();
            return disposable;
        }
    }
}
