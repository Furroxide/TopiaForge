using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer
{
    internal interface ILoopbackCommand : IDisposable
    {
        string Id { get; }

        OperationResult<bool> TryAcquire(ParticipantId senderId, long nowMilliseconds);
    }

    internal interface ILoopbackTransactionalState
    {
        OperationResult<bool> BeginTransaction();

        Action? CommitTransaction();

        void RollbackTransaction();
    }

    internal sealed class LoopbackCommand<TRequest, TResponse> : ILoopbackCommand, IMultiplayerCommandRegistration
        where TRequest : class
        where TResponse : class
    {
        private readonly MultiplayerCommandDefinition<TRequest, TResponse> definition;
        private readonly Action<string, ILoopbackCommand> remove;
        private readonly object rateGate = new object();
        private readonly LoopbackRateLimiter rateLimiter;
        private bool active = true;

        internal LoopbackCommand(
            MultiplayerCommandDefinition<TRequest, TResponse> definition,
            Action<string, ILoopbackCommand> remove)
        {
            this.definition = definition;
            this.remove = remove;
            rateLimiter = new LoopbackRateLimiter(definition.MaximumPerSecond);
        }

        public string Id => definition.Id;
        public bool IsActive => active;

        public OperationResult<bool> TryAcquire(ParticipantId senderId, long nowMilliseconds)
        {
            lock (rateGate)
            {
                if (!active)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "The command registration is inactive.");
                }

                return rateLimiter.TryAcquire(senderId, nowMilliseconds);
            }
        }

        internal OperationResult<TResponse> Invoke(MultiplayerCommandContext context, TRequest request)
        {
            if (!active) return OperationResult<TResponse>.Failure(ModErrorCode.InvalidState, "The command registration is inactive.");
            var encodedRequest = definition.RequestCodec.Encode(request);
            if (!encodedRequest.TryGetValue(out var requestBytes))
                return OperationResult<TResponse>.Failure(encodedRequest.ErrorCode, encodedRequest.ErrorMessage);
            if (requestBytes.Length > definition.RequestCodec.MaximumEncodedBytes)
                return OperationResult<TResponse>.Failure(ModErrorCode.InvalidArgument, "The request codec exceeded its declared maximum size.");
            var decodedRequest = definition.RequestCodec.Decode(requestBytes);
            if (!decodedRequest.TryGetValue(out var requestCopy))
                return OperationResult<TResponse>.Failure(decodedRequest.ErrorCode, decodedRequest.ErrorMessage);
            var result = definition.Handler(context, requestCopy);
            if (!result.TryGetValue(out var response)) return result;
            var encodedResponse = definition.ResponseCodec.Encode(response);
            if (!encodedResponse.TryGetValue(out var responseBytes))
                return OperationResult<TResponse>.Failure(encodedResponse.ErrorCode, encodedResponse.ErrorMessage);
            if (responseBytes.Length > definition.ResponseCodec.MaximumEncodedBytes)
                return OperationResult<TResponse>.Failure(ModErrorCode.InvalidArgument, "The response codec exceeded its declared maximum size.");
            return definition.ResponseCodec.Decode(responseBytes);
        }

        public void Dispose()
        {
            lock (rateGate)
            {
                if (!active) return;
                active = false;
                rateLimiter.Clear();
            }
            remove(Id, this);
        }
    }

    internal sealed class LoopbackReplicatedState<T> : IReplicatedState<T>, ILoopbackTransactionalState where T : class
    {
        private readonly object gate = new object();
        private readonly IMultiplayerCodec<T> codec;
        private readonly Action<string, object> remove;
        private readonly List<Action<T>> handlers = new List<Action<T>>();
        private T value;
        private T? transactionValue;
        private ulong transactionVersion;
        private bool transactionActive;
        private bool transactionChanged;
        private bool disposed;

        internal LoopbackReplicatedState(string id, T value, IMultiplayerCodec<T> codec, Action<string, object> remove)
        {
            Id = id;
            this.value = value;
            this.codec = codec;
            this.remove = remove;
        }

        public string Id { get; }
        public T Value
        {
            get
            {
                lock (gate)
                {
                    return RequireCodecClone(value, "Unable to clone the replicated state value.");
                }
            }
        }
        public ulong Version { get; private set; }

        public OperationResult<T> Update(Func<T, OperationResult<T>> updater)
        {
            if (updater == null) throw new ArgumentNullException(nameof(updater));
            Action<T>[] changed;
            T next;
            T response;
            lock (gate)
            {
                if (disposed) return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The replicated state is disposed.");
                var current = CodecRoundTrip(value);
                if (!current.TryGetValue(out var currentCopy)) return current;
                var result = updater(currentCopy);
                if (!result.TryGetValue(out var proposed)) return result;
                var encoded = EncodeBounded(proposed);
                if (!encoded.TryGetValue(out var bytes))
                {
                    return OperationResult<T>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
                }

                var decoded = codec.Decode((byte[])bytes.Clone());
                if (!decoded.TryGetValue(out next!)) return decoded;
                var detached = codec.Decode((byte[])bytes.Clone());
                if (!detached.TryGetValue(out response!)) return detached;
                value = next;
                Version++;
                if (transactionActive)
                {
                    transactionChanged = true;
                    changed = Array.Empty<Action<T>>();
                }
                else
                {
                    changed = handlers.ToArray();
                }
            }

            foreach (var handler in changed)
            {
                handler(RequireCodecClone(next, "Unable to clone a replicated state notification."));
            }
            return OperationResult<T>.Success(response);
        }

        public OperationResult<bool> BeginTransaction()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "The replicated state is disposed.");
                }

                if (transactionActive)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "A replicated-state transaction is already active.");
                }

                var snapshot = CodecRoundTrip(value);
                if (!snapshot.TryGetValue(out transactionValue!))
                {
                    return OperationResult<bool>.Failure(snapshot.ErrorCode, snapshot.ErrorMessage);
                }

                transactionVersion = Version;
                transactionChanged = false;
                transactionActive = true;
                return OperationResult<bool>.Success(true);
            }
        }

        public Action? CommitTransaction()
        {
            lock (gate)
            {
                if (!transactionActive) return null;
                transactionActive = false;
                transactionValue = null;
                if (!transactionChanged)
                {
                    return null;
                }

                transactionChanged = false;
                var committed = value;
                var changed = handlers.ToArray();
                return () =>
                {
                    foreach (var handler in changed)
                    {
                        handler(RequireCodecClone(committed, "Unable to clone a replicated state notification."));
                    }
                };
            }
        }

        public void RollbackTransaction()
        {
            lock (gate)
            {
                if (!transactionActive) return;
                value = transactionValue!;
                Version = transactionVersion;
                transactionValue = null;
                transactionChanged = false;
                transactionActive = false;
            }
        }

        public IDisposable SubscribeChanged(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(LoopbackReplicatedState<T>));
                handlers.Add(handler);
                return new DelegateLease(() => { lock (gate) handlers.Remove(handler); });
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                handlers.Clear();
            }
            remove(Id, this);
        }

        private OperationResult<T> CodecRoundTrip(T candidate)
        {
            var encoded = EncodeBounded(candidate);
            if (!encoded.TryGetValue(out var bytes))
            {
                return OperationResult<T>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            }

            return codec.Decode((byte[])bytes.Clone());
        }

        private OperationResult<byte[]> EncodeBounded(T candidate)
        {
            var encoded = codec.Encode(candidate);
            if (!encoded.TryGetValue(out var bytes))
            {
                return OperationResult<byte[]>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            }

            if (bytes.Length > codec.MaximumEncodedBytes)
            {
                return OperationResult<byte[]>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The state codec exceeded its declared maximum size.");
            }

            return OperationResult<byte[]>.Success((byte[])bytes.Clone());
        }

        private T RequireCodecClone(T candidate, string message)
        {
            var cloned = CodecRoundTrip(candidate);
            if (!cloned.TryGetValue(out var copy))
            {
                throw new InvalidOperationException(message + " " + cloned.ErrorMessage);
            }

            return copy;
        }
    }

    internal sealed class LoopbackReplicatedObject<TState, TInput> :
        IReplicatedObject<TState, TInput>,
        ILoopbackReplicatedObject
        where TState : class
        where TInput : class
    {
        private readonly object gate = new object();
        private readonly LoopbackMultiplayerSession session;
        private readonly LoopbackObjectTypeRegistration<TState, TInput> registration;
        private ParticipantId? ownerId;
        private TState state;
        private ulong version;
        private bool disposed;

        internal LoopbackReplicatedObject(
            LoopbackMultiplayerSession session,
            string ownerModId,
            NetworkObjectId id,
            ParticipantId? ownerId,
            TState state,
            LoopbackObjectTypeRegistration<TState, TInput> registration)
        {
            this.session = session;
            OwnerModId = ownerModId ?? throw new ArgumentNullException(nameof(ownerModId));
            Id = id;
            this.ownerId = ownerId;
            this.state = state;
            this.registration = registration;
        }

        public string TypeId => registration.TypeId;
        public string OwnerModId { get; }
        public NetworkObjectId Id { get; }
        public bool IsSpawned { get { lock (gate) return !disposed; } }
        public ParticipantId? OwnerId { get { lock (gate) return ownerId; } }
        public TState State
        {
            get
            {
                lock (gate)
                {
                    return RequireStateClone(state, "Unable to clone the replicated object state.");
                }
            }
        }
        public ulong Version { get { lock (gate) return version; } }

        public Task<MultiplayerCommandConfirmation<TState>> SubmitInputAsync(
            TInput input,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(LoopbackReplicatedObject<TState, TInput>));
            }

            return Task.FromResult(session.ApplyObjectInput(this, input, cancellationToken));
        }

        public OperationResult<bool> TransferOwnership(ParticipantId? ownerId)
        {
            lock (gate)
            {
                if (disposed) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The replicated object is despawned.");
                if (!session.CanMutateObjectGraph)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "Ownership transfer is not allowed inside a command transaction.");
                }

                if (ownerId.HasValue && !session.IsParticipant(ownerId.Value))
                    return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The new owner is not admitted to this session.");
                this.ownerId = ownerId;
                version++;
            }

            session.NotifyObjectChanged(this);
            return OperationResult<bool>.Success(true);
        }

        internal OperationResult<bool> TryAcquire(ParticipantId senderId, long nowMilliseconds) =>
            registration.TryAcquire(senderId, nowMilliseconds);

        internal OperationResult<TState> Apply(ReplicatedObjectCommandContext context, TInput input)
        {
            lock (gate)
            {
                if (disposed)
                    return OperationResult<TState>.Failure(ModErrorCode.NotFound, "The replicated object is despawned.");
                if (!registration.IsActive)
                    return OperationResult<TState>.Failure(ModErrorCode.InvalidState, "The replicated-object type registration is inactive.");
                if (ownerId.HasValue && !context.SenderOwnsTarget)
                    return OperationResult<TState>.Failure(ModErrorCode.NotAuthoritative, "The sender does not own this replicated object.");

                var definition = registration.Definition;
                var encoded = definition.InputCodec.Encode(input);
                if (!encoded.TryGetValue(out var bytes)) return OperationResult<TState>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
                if (bytes.Length > definition.InputCodec.MaximumEncodedBytes ||
                    bytes.Length > definition.MaximumPayloadBytes)
                {
                    return OperationResult<TState>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The replicated-object input codec exceeded its declared maximum size.");
                }

                var decoded = definition.InputCodec.Decode((byte[])bytes.Clone());
                if (!decoded.TryGetValue(out var copy)) return OperationResult<TState>.Failure(decoded.ErrorCode, decoded.ErrorMessage);
                var currentState = definition.StateCodec.Encode(state);
                if (!currentState.TryGetValue(out var currentStateBytes))
                    return OperationResult<TState>.Failure(currentState.ErrorCode, currentState.ErrorMessage);
                if (currentStateBytes.Length > definition.StateCodec.MaximumEncodedBytes ||
                    currentStateBytes.Length > definition.MaximumPayloadBytes)
                {
                    return OperationResult<TState>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The replicated-object state codec exceeded its declared maximum size.");
                }

                var decodedCurrentState = definition.StateCodec.Decode((byte[])currentStateBytes.Clone());
                if (!decodedCurrentState.TryGetValue(out var currentStateCopy)) return decodedCurrentState;
                var result = definition.Handler(context, currentStateCopy, copy);
                if (!result.TryGetValue(out var next)) return result;
                var stateBytes = definition.StateCodec.Encode(next);
                if (!stateBytes.TryGetValue(out var encodedState)) return OperationResult<TState>.Failure(stateBytes.ErrorCode, stateBytes.ErrorMessage);
                if (encodedState.Length > definition.StateCodec.MaximumEncodedBytes ||
                    encodedState.Length > definition.MaximumPayloadBytes)
                {
                    return OperationResult<TState>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The replicated-object state codec exceeded its declared maximum size.");
                }

                var decodedState = definition.StateCodec.Decode((byte[])encodedState.Clone());
                if (!decodedState.TryGetValue(out var copyState)) return decodedState;
                var detachedState = definition.StateCodec.Decode((byte[])encodedState.Clone());
                if (!detachedState.TryGetValue(out var responseState)) return detachedState;
                state = copyState;
                version++;
                return OperationResult<TState>.Success(responseState);
            }
        }

        public Func<object> CreateChangeFactory(ReplicatedObjectChangeKind kind)
        {
            lock (gate)
            {
                var capturedState = RequireStateClone(state, "Unable to capture a replicated object change.");
                var capturedOwnerId = ownerId;
                var capturedVersion = version;
                return () => new ReplicatedObjectChange<TState, TInput>(
                        kind,
                        Id,
                        capturedOwnerId,
                        RequireStateClone(capturedState, "Unable to clone a replicated object change."),
                        capturedVersion,
                        kind == ReplicatedObjectChangeKind.Despawned ? null : this);
            }
        }

        public void Dispose()
        {
            lock (gate) disposed = true;
        }

        private OperationResult<TState> CloneState(TState candidate)
        {
            var codec = registration.Definition.StateCodec;
            var encoded = codec.Encode(candidate);
            if (!encoded.TryGetValue(out var bytes))
            {
                return OperationResult<TState>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            }

            if (bytes.Length > codec.MaximumEncodedBytes ||
                bytes.Length > registration.Definition.MaximumPayloadBytes)
            {
                return OperationResult<TState>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The replicated-object state codec exceeded its declared maximum size.");
            }

            return codec.Decode((byte[])bytes.Clone());
        }

        private TState RequireStateClone(TState candidate, string message)
        {
            var cloned = CloneState(candidate);
            if (!cloned.TryGetValue(out var copy))
            {
                throw new InvalidOperationException(message + " " + cloned.ErrorMessage);
            }

            return copy;
        }
    }

    internal sealed class DelegateLease : IDisposable
    {
        private Action? dispose;
        internal DelegateLease(Action dispose) { this.dispose = dispose; }
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
