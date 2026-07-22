using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace TopiaForge.Mods
{
    /// <summary>Generator/provider SPI exposing one generated descriptor and its bounded codecs.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IGeneratedMultiplayerContract
    {
        /// <summary>Gets the immutable generated wire-contract inventory.</summary>
        MultiplayerContractDescriptor MultiplayerContractDescriptor { get; }

        /// <summary>
        /// Gets the generated bounded codec for a DTO discovered from the contract or explicitly anchored through
        /// <see cref="MultiplayerContractAttribute"/>.
        /// </summary>
        IMultiplayerCodec<T> GetCodec<T>() where T : class;
    }

    /// <summary>
    /// Author-facing handle for one generated, snapshot-backed value. Declare this as a field marked with
    /// <see cref="ReplicatedStateAttribute"/>; generated registration connects it to the active session.
    /// </summary>
    public sealed class ReplicatedState<T> : IReplicatedState<T> where T : class
    {
        private readonly object gate = new object();
        private IReplicatedState<T>? connected;
        private T? uncapturedInitialValue;
        private byte[]? capturedInitialBytes;
        private IMultiplayerCodec<T>? capturedInitialCodec;
        private bool hasBound;
        private bool disposed;

        /// <summary>Creates an unconnected replicated value with its first-session default.</summary>
        public ReplicatedState(T initialValue)
        {
            uncapturedInitialValue = initialValue ?? throw new ArgumentNullException(nameof(initialValue));
        }

        /// <inheritdoc/>
        public string Id
        {
            get
            {
                lock (gate)
                {
                    return connected?.Id ?? string.Empty;
                }
            }
        }

        /// <inheritdoc/>
        public T Value
        {
            get
            {
                lock (gate)
                {
                    if (disposed) throw new ObjectDisposedException(nameof(ReplicatedState<T>));
                    if (connected != null)
                    {
                        return CloneOrThrow(
                            connected.Value,
                            capturedInitialCodec!,
                            "Unable to detach the connected replicated state value.");
                    }

                    if (!hasBound || capturedInitialBytes == null || capturedInitialCodec == null)
                    {
                        throw new InvalidOperationException(
                            "BindMultiplayer must succeed before a generated replicated state value is read.");
                    }

                    return DecodeOrThrow(
                        capturedInitialCodec,
                        capturedInitialBytes,
                        "Unable to restore the detached new-session default.");
                }
            }
        }

        /// <inheritdoc/>
        public ulong Version
        {
            get
            {
                lock (gate)
                {
                    return connected?.Version ?? 0;
                }
            }
        }

        /// <summary>
        /// Captures the declared default through its generated codec, registers a detached copy with the provider, and
        /// connects this author-facing handle for the generated binding lifetime. Calling this while another live
        /// binding is connected fails with <see cref="ModErrorCode.Conflict"/>; disposing the returned lease allows a
        /// later generated binding to reconnect the handle with the same frozen new-session default.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public OperationResult<IDisposable> Bind(
            IMultiplayerSession session,
            string id,
            IMultiplayerCodec<T> codec)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A replicated state id is required.", nameof(id));
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            lock (gate)
            {
                if (disposed)
                {
                    return OperationResult<IDisposable>.Failure(ModErrorCode.InvalidState, "The replicated state handle is disposed.");
                }

                if (connected != null)
                {
                    return OperationResult<IDisposable>.Failure(ModErrorCode.Conflict, "The replicated state handle is already connected.");
                }

                var initial = GetDetachedInitialValue(codec);
                if (!initial.TryGetValue(out var providerInitial))
                {
                    return OperationResult<IDisposable>.Failure(initial.ErrorCode, initial.ErrorMessage);
                }

                var registered = session.RegisterState(new ReplicatedStateDefinition<T>(id, providerInitial, codec));
                if (!registered.TryGetValue(out var state))
                {
                    return OperationResult<IDisposable>.Failure(registered.ErrorCode, registered.ErrorMessage);
                }

                connected = state;
                hasBound = true;
                return OperationResult<IDisposable>.Success(new Connection(this, state));
            }
        }

        private void Disconnect(IReplicatedState<T> state)
        {
            lock (gate)
            {
                if (!ReferenceEquals(connected, state)) return;
                connected = null;
            }

            state.Dispose();
        }

        /// <inheritdoc/>
        public OperationResult<T> Update(Func<T, OperationResult<T>> updater)
        {
            if (updater == null) throw new ArgumentNullException(nameof(updater));
            lock (gate)
            {
                if (disposed)
                {
                    return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The replicated state handle is disposed.");
                }

                if (connected == null)
                {
                    return OperationResult<T>.Failure(
                        ModErrorCode.InvalidState,
                        "Generated multiplayer registration has not connected this state yet.");
                }

                return connected.Update(updater);
            }
        }

        /// <inheritdoc/>
        public IDisposable SubscribeChanged(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(ReplicatedState<T>));
                if (connected == null)
                {
                    throw new InvalidOperationException("Generated multiplayer registration has not connected this state yet.");
                }

                return connected.SubscribeChanged(handler);
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            IReplicatedState<T>? state;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                state = connected;
                connected = null;
                uncapturedInitialValue = null;
                capturedInitialBytes = null;
                capturedInitialCodec = null;
            }

            state?.Dispose();
        }

        private OperationResult<T> GetDetachedInitialValue(IMultiplayerCodec<T> codec)
        {
            if (capturedInitialBytes != null && capturedInitialCodec != null)
            {
                return Decode(codec: capturedInitialCodec, capturedInitialBytes);
            }

            var source = uncapturedInitialValue!;
            var encoded = Encode(codec, source);
            if (!encoded.TryGetValue(out var bytes))
            {
                return OperationResult<T>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            }

            var decoded = Decode(codec, bytes);
            if (!decoded.Succeeded) return decoded;
            capturedInitialBytes = (byte[])bytes.Clone();
            capturedInitialCodec = codec;
            uncapturedInitialValue = null;
            return decoded;
        }

        private static T CloneOrThrow(T value, IMultiplayerCodec<T> codec, string message)
        {
            var encoded = Encode(codec, value);
            if (!encoded.TryGetValue(out var bytes))
            {
                throw new InvalidOperationException(message + " " + encoded.ErrorMessage);
            }

            return DecodeOrThrow(codec, bytes, message);
        }

        private static T DecodeOrThrow(IMultiplayerCodec<T> codec, byte[] bytes, string message)
        {
            var decoded = Decode(codec, bytes);
            if (!decoded.TryGetValue(out var value))
            {
                throw new InvalidOperationException(message + " " + decoded.ErrorMessage);
            }

            return value;
        }

        private static OperationResult<byte[]> Encode(IMultiplayerCodec<T> codec, T value)
        {
            try
            {
                var encoded = codec.Encode(value);
                if (encoded == null)
                {
                    return OperationResult<byte[]>.Failure(
                        ModErrorCode.Unknown,
                        "The generated state codec returned no encode result.");
                }

                if (!encoded.TryGetValue(out var bytes))
                {
                    return OperationResult<byte[]>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
                }

                if (bytes == null)
                {
                    return OperationResult<byte[]>.Failure(
                        ModErrorCode.Unknown,
                        "The generated state codec returned null bytes.");
                }

                if (bytes.Length > codec.MaximumEncodedBytes)
                {
                    return OperationResult<byte[]>.Failure(
                        ModErrorCode.InvalidArgument,
                        "The generated state codec exceeded its declared maximum size.");
                }

                return OperationResult<byte[]>.Success((byte[])bytes.Clone());
            }
            catch (Exception exception)
            {
                return OperationResult<byte[]>.Failure(
                    ModErrorCode.Unknown,
                    "The generated state codec failed while capturing the declared default: " +
                    exception.GetType().Name + ".");
            }
        }

        private static OperationResult<T> Decode(IMultiplayerCodec<T> codec, byte[] bytes)
        {
            try
            {
                var decoded = codec.Decode((byte[])bytes.Clone());
                return decoded ?? OperationResult<T>.Failure(
                    ModErrorCode.Unknown,
                    "The generated state codec returned no decode result.");
            }
            catch (Exception exception)
            {
                return OperationResult<T>.Failure(
                    ModErrorCode.Unknown,
                    "The generated state codec failed while restoring the declared default: " +
                    exception.GetType().Name + ".");
            }
        }

        private sealed class Connection : IDisposable
        {
            private ReplicatedState<T>? owner;
            private IReplicatedState<T>? state;

            internal Connection(ReplicatedState<T> owner, IReplicatedState<T> state)
            {
                this.owner = owner;
                this.state = state;
            }

            public void Dispose()
            {
                var capturedOwner = owner;
                var capturedState = state;
                owner = null;
                state = null;
                if (capturedOwner != null && capturedState != null)
                {
                    capturedOwner.Disconnect(capturedState);
                }
            }
        }
    }

    /// <summary>Generator/provider SPI for one bounded data codec. Ordinary mods use generated proxies.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IMultiplayerCodec<T> where T : class
    {
        /// <summary>Gets the maximum encoded byte count.</summary>
        int MaximumEncodedBytes { get; }

        /// <summary>Encodes a value into a bounded byte array.</summary>
        OperationResult<byte[]> Encode(T value);

        /// <summary>Decodes a complete bounded byte array.</summary>
        OperationResult<T> Decode(byte[] bytes);
    }

    /// <summary>Generator/provider SPI defining one snapshot-backed replicated value.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class ReplicatedStateDefinition<T> where T : class
    {
        /// <summary>Creates a replicated-state definition.</summary>
        public ReplicatedStateDefinition(string id, T initialValue, IMultiplayerCodec<T> codec)
        {
            Id = MultiplayerIdentityValidation.Require(id, nameof(id));
            InitialValue = initialValue ?? throw new ArgumentNullException(nameof(initialValue));
            Codec = codec ?? throw new ArgumentNullException(nameof(codec));
            if (codec.MaximumEncodedBytes < 0 || codec.MaximumEncodedBytes > 1024 * 1024)
                throw new ArgumentException("Replicated-state codec bounds must be between zero and 1 MiB.", nameof(codec));
        }

        /// <summary>Gets the stable mod-local state id.</summary>
        public string Id { get; }

        /// <summary>Gets the initial canonical value.</summary>
        public T InitialValue { get; }

        /// <summary>Gets the generated bounded codec.</summary>
        public IMultiplayerCodec<T> Codec { get; }
    }

    /// <summary>Provider SPI for one canonical value plus ordered snapshot and delta changes.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IReplicatedState<T> : IDisposable where T : class
    {
        /// <summary>Gets the stable mod-local state id.</summary>
        string Id { get; }

        /// <summary>
        /// Gets a codec-detached copy of the latest confirmed value. Mutating the returned DTO never mutates
        /// replicated storage or advances <see cref="Version"/>.
        /// </summary>
        T Value { get; }

        /// <summary>Gets the monotonically increasing confirmed version.</summary>
        ulong Version { get; }

        /// <summary>
        /// Applies a canonical or transactional predicted update. The updater receives a codec-detached copy, and a
        /// successful result is validated and copied before it becomes stored state.
        /// </summary>
        OperationResult<T> Update(Func<T, OperationResult<T>> updater);

        /// <summary>Subscribes to confirmed value replacements with a separate codec-detached DTO per callback.</summary>
        IDisposable SubscribeChanged(Action<T> handler);
    }

    /// <summary>Generator/provider SPI identifying one stable replicated-object wire type.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class ReplicatedObjectType<TState, TInput>
        where TState : class
        where TInput : class
    {
        /// <summary>Creates a replicated-object type reference.</summary>
        public ReplicatedObjectType(string id)
        {
            Id = MultiplayerIdentityValidation.Require(id, nameof(id));
        }

        /// <summary>Gets the stable namespaced wire type id.</summary>
        public string Id { get; }
    }

    /// <summary>
    /// Registers the local codecs and handler for one replicated-object wire type. Each logical peer constructs its
    /// own definition; providers exchange only <see cref="ReplicatedObjectType{TState,TInput}.Id"/> and bounded bytes.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class ReplicatedObjectTypeDefinition<TState, TInput>
        where TState : class
        where TInput : class
    {
        /// <summary>Creates a replicated-object type definition.</summary>
        public ReplicatedObjectTypeDefinition(
            ReplicatedObjectType<TState, TInput> type,
            IMultiplayerCodec<TState> stateCodec,
            IMultiplayerCodec<TInput> inputCodec,
            Func<ReplicatedObjectCommandContext, TState, TInput, OperationResult<TState>> handler,
            PredictionMode prediction = PredictionMode.None,
            int maximumPerSecond = 30,
            int maximumPayloadBytes = 16 * 1024)
        {
            Type = type ?? throw new ArgumentNullException(nameof(type));
            StateCodec = stateCodec ?? throw new ArgumentNullException(nameof(stateCodec));
            InputCodec = inputCodec ?? throw new ArgumentNullException(nameof(inputCodec));
            Handler = handler ?? throw new ArgumentNullException(nameof(handler));
            if (!Enum.IsDefined(typeof(PredictionMode), prediction)) throw new ArgumentOutOfRangeException(nameof(prediction));
            if (maximumPerSecond < 1 || maximumPerSecond > 1000) throw new ArgumentOutOfRangeException(nameof(maximumPerSecond));
            if (maximumPayloadBytes < 64 || maximumPayloadBytes > 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
            if (stateCodec.MaximumEncodedBytes < 0 || inputCodec.MaximumEncodedBytes < 0)
                throw new ArgumentException("Codec bounds cannot be negative.", nameof(stateCodec));
            if (stateCodec.MaximumEncodedBytes > maximumPayloadBytes || inputCodec.MaximumEncodedBytes > maximumPayloadBytes)
                throw new ArgumentException(
                    "Generated state and input codec bounds must not exceed the replicated-object payload limit.",
                    nameof(maximumPayloadBytes));
            Prediction = prediction;
            MaximumPerSecond = maximumPerSecond;
            MaximumPayloadBytes = maximumPayloadBytes;
        }

        /// <summary>Gets the generated typed wire reference.</summary>
        public ReplicatedObjectType<TState, TInput> Type { get; }

        /// <summary>Gets the stable namespaced replicated type id.</summary>
        public string TypeId => Type.Id;

        /// <summary>Gets the generated state codec.</summary>
        public IMultiplayerCodec<TState> StateCodec { get; }

        /// <summary>Gets the generated input codec.</summary>
        public IMultiplayerCodec<TInput> InputCodec { get; }

        /// <summary>Gets the canonical transactional input handler.</summary>
        public Func<ReplicatedObjectCommandContext, TState, TInput, OperationResult<TState>> Handler { get; }

        /// <summary>Gets whether an owner may predict inputs.</summary>
        public PredictionMode Prediction { get; }

        /// <summary>Gets the per-sender input rate limit.</summary>
        public int MaximumPerSecond { get; }

        /// <summary>Gets the maximum encoded state or input payload size.</summary>
        public int MaximumPayloadBytes { get; }
    }

    /// <summary>Provider SPI for a lifetime-owned local replicated-object type registration.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IReplicatedObjectTypeRegistration : IDisposable
    {
        /// <summary>Gets the stable namespaced wire type id.</summary>
        string TypeId { get; }

        /// <summary>Gets whether the local type definition remains registered.</summary>
        bool IsActive { get; }
    }

    /// <summary>Describes why a replicated-object discovery subscription was notified.</summary>
    public enum ReplicatedObjectChangeKind
    {
        /// <summary>A canonical object became discoverable on this peer.</summary>
        Spawned = 0,

        /// <summary>The canonical state or ownership changed.</summary>
        Changed = 1,

        /// <summary>The canonical server despawned the object.</summary>
        Despawned = 2
    }

    /// <summary>One typed canonical replicated-object discovery or change notification.</summary>
    public sealed class ReplicatedObjectChange<TState, TInput>
        where TState : class
        where TInput : class
    {
        /// <summary>Creates a replicated-object notification.</summary>
        public ReplicatedObjectChange(
            ReplicatedObjectChangeKind kind,
            NetworkObjectId id,
            ParticipantId? ownerId,
            TState state,
            ulong version,
            IReplicatedObject<TState, TInput>? replicatedObject)
        {
            if (!Enum.IsDefined(typeof(ReplicatedObjectChangeKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!id.IsValid)
                throw new ArgumentException("A replicated-object change requires a valid object identity.", nameof(id));
            if (ownerId.HasValue && !ownerId.Value.IsValid)
                throw new ArgumentException("A replicated-object owner identity must be valid when present.", nameof(ownerId));
            Kind = kind;
            Id = id;
            OwnerId = ownerId;
            State = state ?? throw new ArgumentNullException(nameof(state));
            Version = version;
            Object = replicatedObject;
            if (kind != ReplicatedObjectChangeKind.Despawned && replicatedObject == null)
                throw new ArgumentNullException(nameof(replicatedObject));
            if (kind == ReplicatedObjectChangeKind.Despawned && replicatedObject != null)
                throw new ArgumentException("A despawn notification cannot expose a live object handle.", nameof(replicatedObject));
            if (replicatedObject != null && !replicatedObject.Id.Equals(id))
                throw new ArgumentException("The live object handle must match the notification identity.", nameof(replicatedObject));
        }

        /// <summary>Gets why the subscription was notified.</summary>
        public ReplicatedObjectChangeKind Kind { get; }

        /// <summary>Gets the canonical object identity.</summary>
        public NetworkObjectId Id { get; }

        /// <summary>Gets the latest canonical predictive owner.</summary>
        public ParticipantId? OwnerId { get; }

        /// <summary>
        /// Gets a codec-detached snapshot of the latest canonical state, including the final state for a despawn.
        /// Mutating it cannot affect the object or another observer.
        /// </summary>
        public TState State { get; }

        /// <summary>Gets the latest canonical version.</summary>
        public ulong Version { get; }

        /// <summary>Gets the live object, or null after a despawn.</summary>
        public IReplicatedObject<TState, TInput>? Object { get; }
    }

    /// <summary>Represents one server-created object with optional owner prediction.</summary>
    public interface IReplicatedObject<TState, TInput>
        where TState : class
        where TInput : class
    {
        /// <summary>Gets the registered stable namespaced wire type id.</summary>
        string TypeId { get; }

        /// <summary>Gets the server-assigned instance identity.</summary>
        NetworkObjectId Id { get; }

        /// <summary>Gets whether this handle still represents a canonically spawned session object.</summary>
        bool IsSpawned { get; }

        /// <summary>Gets the participant allowed to predict, or null for server-only ownership.</summary>
        ParticipantId? OwnerId { get; }

        /// <summary>
        /// Gets a codec-detached copy of the latest confirmed object state. Mutating the returned DTO never mutates
        /// the object or advances <see cref="Version"/>.
        /// </summary>
        TState State { get; }

        /// <summary>Gets the monotonically increasing canonical object version.</summary>
        ulong Version { get; }

        /// <summary>Submits one bounded input for server validation and optional local prediction.</summary>
        System.Threading.Tasks.Task<MultiplayerCommandConfirmation<TState>> SubmitInputAsync(
            TInput input,
            System.Threading.CancellationToken cancellationToken = default);

        /// <summary>Transfers predictive ownership; only canonical server code may succeed.</summary>
        OperationResult<bool> TransferOwnership(ParticipantId? ownerId);
    }

    /// <summary>Generator/provider SPI carrying immutable wire-contract inventory used during admission.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class MultiplayerContractDescriptor
    {
        /// <summary>Creates a generated contract descriptor.</summary>
        public MultiplayerContractDescriptor(
            string id,
            int wireFormatRevision,
            string schemaSha256,
            IReadOnlyList<string> stateIds,
            IReadOnlyList<string> commandIds,
            IReadOnlyList<string> objectTypeIds,
            IReadOnlyList<string> eventIds)
        {
            Id = MultiplayerIdentityValidation.Require(id, nameof(id));
            if (wireFormatRevision < 1)
                throw new ArgumentOutOfRangeException(nameof(wireFormatRevision));
            if (string.IsNullOrWhiteSpace(schemaSha256) || schemaSha256.Length != 64 ||
                !IsHexSha256(schemaSha256))
            {
                throw new ArgumentException("A lowercase or uppercase SHA-256 string is required.", nameof(schemaSha256));
            }

            WireFormatRevision = wireFormatRevision;
            SchemaSha256 = schemaSha256;
            StateIds = CopyIds(stateIds, nameof(stateIds));
            CommandIds = CopyIds(commandIds, nameof(commandIds));
            ObjectTypeIds = CopyIds(objectTypeIds, nameof(objectTypeIds));
            EventIds = CopyIds(eventIds, nameof(eventIds));
        }

        /// <summary>Gets the stable contract id.</summary>
        public string Id { get; }

        /// <summary>Gets the generator-owned byte-layout revision included in the schema and contract lock.</summary>
        public int WireFormatRevision { get; }

        /// <summary>Gets the deterministic generated schema digest.</summary>
        public string SchemaSha256 { get; }

        /// <summary>Gets state ids in ordinal order.</summary>
        public IReadOnlyList<string> StateIds { get; }

        /// <summary>Gets command ids in ordinal order.</summary>
        public IReadOnlyList<string> CommandIds { get; }

        /// <summary>Gets replicated-object type ids in ordinal order.</summary>
        public IReadOnlyList<string> ObjectTypeIds { get; }

        /// <summary>Gets presentation-event ids in ordinal order.</summary>
        public IReadOnlyList<string> EventIds { get; }

        private static IReadOnlyList<string> CopyIds(IReadOnlyList<string> ids, string parameterName)
        {
            if (ids == null) throw new ArgumentNullException(parameterName);
            var copy = new List<string>(ids.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var valid = MultiplayerIdentityValidation.Require(id, parameterName);
                if (!seen.Add(valid)) throw new ArgumentException("Generated contract ids must be unique.", parameterName);
                copy.Add(valid);
            }

            copy.Sort(StringComparer.Ordinal);
            return copy.AsReadOnly();
        }

        private static bool IsHexSha256(string value)
        {
            foreach (var character in value)
            {
                if (character >= '0' && character <= '9' || character >= 'a' && character <= 'f' ||
                    character >= 'A' && character <= 'F')
                {
                    continue;
                }

                return false;
            }

            return true;
        }
    }
}
