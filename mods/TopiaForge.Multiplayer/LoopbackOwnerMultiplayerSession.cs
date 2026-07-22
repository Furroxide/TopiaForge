using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer
{
    /// <summary>Keeps every consumer registration and subscription inside that mod's runtime lifetime.</summary>
    internal sealed class LoopbackOwnerMultiplayerSession : IMultiplayerSession
    {
        private readonly LoopbackMultiplayerSession session;
        private readonly string ownerModId;
        private readonly IModLifetime lifetime;

        internal LoopbackOwnerMultiplayerSession(
            LoopbackMultiplayerSession session,
            string ownerModId,
            IModLifetime lifetime)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.ownerModId = ownerModId ?? throw new ArgumentNullException(nameof(ownerModId));
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        public MultiplayerSessionSnapshot Snapshot => session.Snapshot;

        public CancellationToken CurrentSessionToken => session.CurrentSessionToken;

        public IDisposable SubscribeChanged(Action<MultiplayerSessionSnapshot> handler) =>
            TrackSubscription(() => session.SubscribeChanged(handler));

        public OperationResult<IReplicatedState<T>> RegisterState<T>(ReplicatedStateDefinition<T> definition)
            where T : class =>
            TrackResult(
                () => session.RegisterState(ownerModId, definition),
                (resource, lease) => new OwnedReplicatedState<T>(resource, lease, lifetime),
                "The mod stopped before its replicated state could be registered.");

        public OperationResult<IMultiplayerCommandRegistration> RegisterCommand<TRequest, TResponse>(
            MultiplayerCommandDefinition<TRequest, TResponse> definition)
            where TRequest : class
            where TResponse : class =>
            TrackResult(
                () => session.RegisterCommand(ownerModId, definition),
                (resource, lease) => new OwnedCommandRegistration(resource, lease),
                "The mod stopped before its multiplayer command could be registered.");

        public OperationResult<IReplicatedObjectTypeRegistration> RegisterObjectType<TState, TInput>(
            ReplicatedObjectTypeDefinition<TState, TInput> definition)
            where TState : class
            where TInput : class =>
            TrackResult(
                () => session.RegisterObjectType(ownerModId, definition),
                (resource, lease) => new OwnedObjectTypeRegistration(resource, lease),
                "The mod stopped before its replicated-object type could be registered.");

        public async Task<MultiplayerCommandConfirmation<TResponse>> SubmitAsync<TRequest, TResponse>(
            MultiplayerCommandType<TRequest, TResponse> commandType,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            var before = session.Snapshot.Tick;
            if (lifetime.IsStopping)
            {
                return Cancelled<TResponse>(before, "The mod is stopping and cannot submit multiplayer commands.");
            }

            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetime.StoppingToken))
            {
                try
                {
                    return await session.SubmitAsync<TRequest, TResponse>(
                        ownerModId,
                        commandType,
                        request,
                        linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    return Cancelled<TResponse>(before, "The multiplayer command was cancelled.");
                }
            }
        }

        public OperationResult<IReplicatedObject<TState, TInput>> SpawnObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            TState initialState,
            ParticipantId? ownerId = null)
            where TState : class
            where TInput : class
        {
            if (lifetime.IsStopping)
            {
                return OperationResult<IReplicatedObject<TState, TInput>>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot spawn replicated objects.");
            }

            var spawned = session.SpawnObject(ownerModId, type, initialState, ownerId);
            if (!spawned.TryGetValue(out var replicatedObject)) return spawned;
            return OperationResult<IReplicatedObject<TState, TInput>>.Success(
                new OwnedReplicatedObject<TState, TInput>(replicatedObject, session, lifetime));
        }

        public OperationResult<bool> DespawnObject(NetworkObjectId id) =>
            lifetime.IsStopping
                ? OperationResult<bool>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot despawn replicated objects.")
                : session.DespawnObject(ownerModId, id);

        public IReadOnlyList<IReplicatedObject<TState, TInput>> GetObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type)
            where TState : class
            where TInput : class =>
            WrapObjects(type);

        public bool TryGetObject<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            NetworkObjectId id,
            out IReplicatedObject<TState, TInput>? replicatedObject)
            where TState : class
            where TInput : class
        {
            if (lifetime.IsStopping)
            {
                replicatedObject = null;
                return false;
            }

            if (!session.TryGetObject(ownerModId, type, id, out var found) || found == null)
            {
                replicatedObject = null;
                return false;
            }

            replicatedObject = new OwnedReplicatedObject<TState, TInput>(found, session, lifetime);
            return true;
        }

        public IDisposable SubscribeObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type,
            Action<ReplicatedObjectChange<TState, TInput>> handler)
            where TState : class
            where TInput : class =>
            TrackSubscription(() => session.SubscribeObjects(ownerModId, type, change =>
            {
                var ownedObject = change.Object == null
                    ? null
                    : new OwnedReplicatedObject<TState, TInput>(change.Object, session, lifetime);
                handler(new ReplicatedObjectChange<TState, TInput>(
                    change.Kind,
                    change.Id,
                    change.OwnerId,
                    change.State,
                    change.Version,
                    ownedObject));
            }));

        public bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id)
        {
            if (lifetime.IsStopping)
            {
                id = default;
                return false;
            }

            return session.TryGetNetworkObjectId(ownerModId, entity, out id);
        }

        public OperationResult<IPresentationEventRegistration> RegisterPresentation<TEvent>(
            PresentationEventDefinition<TEvent> definition) where TEvent : class =>
            TrackResult(
                () => session.RegisterPresentation(ownerModId, definition),
                (resource, lease) => new OwnedPresentationRegistration(resource, lease),
                "The mod stopped before its presentation event could be registered.");

        public OperationResult<bool> PublishPresentation<TEvent>(
            PresentationEventType<TEvent> eventType,
            TEvent value,
            MultiplayerAudience audience = MultiplayerAudience.Everyone) where TEvent : class =>
            lifetime.IsStopping
                ? OperationResult<bool>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot publish presentation events.")
                : session.PublishPresentation(ownerModId, eventType, value, audience);

        private OperationResult<T> TrackResult<T>(
            Func<OperationResult<T>> register,
            Func<T, IDisposable, T> wrap,
            string cancelledMessage) where T : class, IDisposable
        {
            if (lifetime.IsStopping)
            {
                return OperationResult<T>.Failure(ModErrorCode.Cancelled, cancelledMessage);
            }

            var result = register();
            if (!result.TryGetValue(out var resource)) return result;
            IDisposable? lifetimeLease = null;
            try
            {
                lifetimeLease = lifetime.Track(resource);
                var owned = wrap(resource, lifetimeLease);
                lifetimeLease = null;
                return OperationResult<T>.Success(owned);
            }
            catch (ObjectDisposedException)
            {
                lifetimeLease?.Dispose();
                return OperationResult<T>.Failure(ModErrorCode.Cancelled, cancelledMessage);
            }
            catch
            {
                lifetimeLease?.Dispose();
                throw;
            }
        }

        private IDisposable TrackSubscription(Func<IDisposable> subscribe)
        {
            if (lifetime.IsStopping) throw new ObjectDisposedException(nameof(IModLifetime));
            var subscription = subscribe();
            try
            {
                return lifetime.Track(subscription);
            }
            catch
            {
                subscription.Dispose();
                throw;
            }
        }

        private IReadOnlyList<IReplicatedObject<TState, TInput>> WrapObjects<TState, TInput>(
            ReplicatedObjectType<TState, TInput> type)
            where TState : class
            where TInput : class
        {
            if (lifetime.IsStopping) return Array.Empty<IReplicatedObject<TState, TInput>>();
            var source = session.GetObjects(ownerModId, type);
            var result = new IReplicatedObject<TState, TInput>[source.Count];
            for (var index = 0; index < source.Count; index++)
            {
                result[index] = new OwnedReplicatedObject<TState, TInput>(source[index], session, lifetime);
            }

            return result;
        }

        private MultiplayerCommandConfirmation<T> Cancelled<T>(NetworkTick submittedAt, string message)
            where T : class =>
            new MultiplayerCommandConfirmation<T>(
                submittedAt,
                session.Snapshot.Tick,
                false,
                OperationResult<T>.Failure(ModErrorCode.Cancelled, message));

        private abstract class OwnedResource : IDisposable
        {
            private IDisposable? lifetimeLease;

            protected OwnedResource(IDisposable lifetimeLease)
            {
                this.lifetimeLease = lifetimeLease ?? throw new ArgumentNullException(nameof(lifetimeLease));
            }

            protected bool IsOwned => lifetimeLease != null;

            public void Dispose()
            {
                Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }

        private sealed class OwnedReplicatedState<T> : OwnedResource, IReplicatedState<T> where T : class
        {
            private readonly IReplicatedState<T> state;
            private readonly IModLifetime lifetime;

            internal OwnedReplicatedState(
                IReplicatedState<T> state,
                IDisposable lifetimeLease,
                IModLifetime lifetime) : base(lifetimeLease)
            {
                this.state = state;
                this.lifetime = lifetime;
            }

            public string Id => state.Id;
            public T Value => state.Value;
            public ulong Version => state.Version;

            public OperationResult<T> Update(Func<T, OperationResult<T>> updater) => state.Update(updater);

            public IDisposable SubscribeChanged(Action<T> handler)
            {
                if (!IsOwned || lifetime.IsStopping)
                    throw new ObjectDisposedException(nameof(OwnedReplicatedState<T>));
                var subscription = state.SubscribeChanged(handler);
                try
                {
                    return lifetime.Track(subscription);
                }
                catch
                {
                    subscription.Dispose();
                    throw;
                }
            }
        }

        private sealed class OwnedCommandRegistration : OwnedResource, IMultiplayerCommandRegistration
        {
            private readonly IMultiplayerCommandRegistration registration;

            internal OwnedCommandRegistration(
                IMultiplayerCommandRegistration registration,
                IDisposable lifetimeLease) : base(lifetimeLease) =>
                this.registration = registration;

            public string Id => registration.Id;
            public bool IsActive => IsOwned && registration.IsActive;
        }

        private sealed class OwnedObjectTypeRegistration : OwnedResource, IReplicatedObjectTypeRegistration
        {
            private readonly IReplicatedObjectTypeRegistration registration;

            internal OwnedObjectTypeRegistration(
                IReplicatedObjectTypeRegistration registration,
                IDisposable lifetimeLease) : base(lifetimeLease) =>
                this.registration = registration;

            public string TypeId => registration.TypeId;
            public bool IsActive => IsOwned && registration.IsActive;
        }

        private sealed class OwnedPresentationRegistration : OwnedResource, IPresentationEventRegistration
        {
            private readonly IPresentationEventRegistration registration;

            internal OwnedPresentationRegistration(
                IPresentationEventRegistration registration,
                IDisposable lifetimeLease) : base(lifetimeLease) =>
                this.registration = registration;

            public string Id => registration.Id;
            public bool IsActive => IsOwned && registration.IsActive;
        }

        private sealed class OwnedReplicatedObject<TState, TInput> : IReplicatedObject<TState, TInput>
            where TState : class
            where TInput : class
        {
            private readonly IReplicatedObject<TState, TInput> replicatedObject;
            private readonly IMultiplayerSession session;
            private readonly IModLifetime lifetime;

            internal OwnedReplicatedObject(
                IReplicatedObject<TState, TInput> replicatedObject,
                IMultiplayerSession session,
                IModLifetime lifetime)
            {
                this.replicatedObject = replicatedObject;
                this.session = session;
                this.lifetime = lifetime;
            }

            public string TypeId => replicatedObject.TypeId;
            public NetworkObjectId Id => replicatedObject.Id;
            public bool IsSpawned => !lifetime.IsStopping && replicatedObject.IsSpawned;
            public ParticipantId? OwnerId => replicatedObject.OwnerId;
            public TState State => replicatedObject.State;
            public ulong Version => replicatedObject.Version;

            public async Task<MultiplayerCommandConfirmation<TState>> SubmitInputAsync(
                TInput input,
                CancellationToken cancellationToken = default)
            {
                var before = SnapshotTick();
                if (lifetime.IsStopping)
                {
                    return Cancelled(before, "The mod is stopping and cannot submit replicated-object input.");
                }

                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.StoppingToken))
                {
                    try
                    {
                        return await replicatedObject.SubmitInputAsync(input, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        return Cancelled(before, "The replicated-object input was cancelled.");
                    }
                }
            }

            public OperationResult<bool> TransferOwnership(ParticipantId? ownerId) =>
                lifetime.IsStopping
                    ? OperationResult<bool>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot transfer replicated-object ownership.")
                    : replicatedObject.TransferOwnership(ownerId);

            private NetworkTick SnapshotTick() => session.Snapshot.Tick;

            private static MultiplayerCommandConfirmation<TState> Cancelled(
                NetworkTick tick,
                string message) =>
                new MultiplayerCommandConfirmation<TState>(
                    tick,
                    tick,
                    false,
                    OperationResult<TState>.Failure(ModErrorCode.Cancelled, message));
        }
    }
}
