using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal enum ObjectLife
    {
        /// <summary>Spawned inside an open transaction; visible to that transaction only.</summary>
        PendingSpawn = 0,

        /// <summary>Canonically spawned and discoverable.</summary>
        Live = 1,

        /// <summary>Despawned inside an open transaction; restored if it aborts.</summary>
        PendingDespawn = 2,

        /// <summary>Gone for good; the handle reports <c>IsSpawned == false</c>.</summary>
        Despawned = 3
    }

    /// <summary>One registered replicated-object type: codecs, input handler, bounds and delivery knobs.</summary>
    internal abstract class ObjectTypeBinding : IReplicatedObjectTypeRegistration
    {
        protected ObjectTypeBinding(
            SessionEngine engine,
            WireKey key,
            PredictionMode prediction,
            int maximumPerSecond,
            int maximumPayloadBytes,
            MultiplayerDelivery delivery,
            int maximumUpdatesPerSecond)
        {
            Engine = engine;
            Key = key;
            Prediction = prediction;
            MaximumPerSecond = maximumPerSecond;
            MaximumPayloadBytes = maximumPayloadBytes;
            Delivery = delivery;
            MaximumUpdatesPerSecond = maximumUpdatesPerSecond;
        }

        internal SessionEngine Engine { get; }
        internal WireKey Key { get; }
        internal PredictionMode Prediction { get; }
        internal int MaximumPerSecond { get; }
        internal int MaximumPayloadBytes { get; }
        internal MultiplayerDelivery Delivery { get; }
        internal int MaximumUpdatesPerSecond { get; }

        public string TypeId => Key.Id;

        public bool IsActive { get; internal set; } = true;

        public void Dispose() => Engine.UnregisterObjectType(this);

        internal abstract ObjectRecord CreateRecord(NetworkObjectId id, ParticipantId? owner, byte[] bytes, ulong version);

        internal abstract bool CanDecodeState(byte[] bytes);
    }

    internal sealed class ObjectTypeBinding<TState, TInput> : ObjectTypeBinding
        where TState : class
        where TInput : class
    {
        internal ObjectTypeBinding(SessionEngine engine, WireKey key, ReplicatedObjectTypeDefinition<TState, TInput> definition)
            : base(
                engine,
                key,
                definition.Prediction,
                definition.MaximumPerSecond,
                definition.MaximumPayloadBytes,
                definition.Delivery,
                definition.MaximumUpdatesPerSecond)
        {
            Definition = definition;
        }

        internal ReplicatedObjectTypeDefinition<TState, TInput> Definition { get; }

        internal override ObjectRecord CreateRecord(NetworkObjectId id, ParticipantId? owner, byte[] bytes, ulong version) =>
            new ObjectRecord<TState, TInput>(Engine, this, id, owner, bytes, version);

        internal override bool CanDecodeState(byte[] bytes) =>
            bytes.Length <= Definition.StateCodec.MaximumEncodedBytes && EngineCodec.Decode(Definition.StateCodec, bytes).Succeeded;
    }

    /// <summary>A canonical object change captured when it happened and dispatched at the next settle point.</summary>
    internal sealed class ObjectChangeEvent
    {
        internal ObjectChangeEvent(ObjectRecord record, ReplicatedObjectChangeKind kind, ParticipantId? owner, byte[] bytes, ulong version)
        {
            Record = record;
            Kind = kind;
            Owner = owner;
            Bytes = bytes;
            Version = version;
        }

        internal ObjectRecord Record { get; }
        internal ReplicatedObjectChangeKind Kind { get; }
        internal ParticipantId? Owner { get; }
        internal byte[] Bytes { get; }
        internal ulong Version { get; }
    }

    /// <summary>
    /// One replicated object; it is also the handle mods hold. A client's current state carries pending owner
    /// predictions on top of the confirmed state; ownership is never predicted.
    /// </summary>
    internal abstract class ObjectRecord
    {
        private Dictionary<ParticipantId, SlidingWindowRateLimiter>? limiters;

        protected ObjectRecord(SessionEngine engine, ObjectTypeBinding type, NetworkObjectId id, ParticipantId? owner, byte[] bytes, ulong version)
        {
            Engine = engine;
            Type = type;
            Id = id;
            ConfirmedBytes = bytes;
            CurrentBytes = bytes;
            ConfirmedVersion = version;
            CurrentVersion = version;
            ConfirmedOwner = owner;
            CurrentOwner = owner;
        }

        internal SessionEngine Engine { get; }
        internal ObjectTypeBinding Type { get; }
        public NetworkObjectId Id { get; }
        internal byte[] ConfirmedBytes { get; set; }
        internal ulong ConfirmedVersion { get; set; }
        internal ParticipantId? ConfirmedOwner { get; set; }
        internal byte[] CurrentBytes { get; set; }
        internal ulong CurrentVersion { get; set; }
        internal ParticipantId? CurrentOwner { get; set; }
        internal ObjectLife Life { get; set; } = ObjectLife.Live;

        /// <summary>
        /// Gets or sets the facade that spawned this object on the host, so closing that facade despawns it and no other
        /// facade's. Null for a joined player's copy of the host's object.
        /// </summary>
        internal object? Spawner { get; set; }

        /// <summary>Gets how many senders have a rate limiter on this object.</summary>
        internal int LimiterCount => limiters?.Count ?? 0;

        internal void Confirm()
        {
            ConfirmedBytes = CurrentBytes;
            ConfirmedVersion = CurrentVersion;
            ConfirmedOwner = CurrentOwner;
        }

        internal void RevertToConfirmed()
        {
            CurrentBytes = ConfirmedBytes;
            CurrentVersion = ConfirmedVersion;
            CurrentOwner = ConfirmedOwner;
        }

        internal bool TryConsumeRate(ParticipantId sender, long nowMicros)
        {
            limiters ??= new Dictionary<ParticipantId, SlidingWindowRateLimiter>();
            if (!limiters.TryGetValue(sender, out var limiter))
            {
                limiter = new SlidingWindowRateLimiter(Type.MaximumPerSecond, 1_000_000);
                limiters.Add(sender, limiter);
            }

            return limiter.TryAcquire(nowMicros);
        }

        /// <summary>Drops the limiter of a sender the host no longer knows.</summary>
        internal void ForgetSender(ParticipantId sender) => limiters?.Remove(sender);

        /// <summary>Runs the type's input handler against the current state inside <paramref name="scope"/>.</summary>
        internal abstract WireOutcome InvokeInput(ParticipantId sender, ulong sequence, bool senderOwns, byte[] inputBytes, TransactionScope scope);

        internal abstract void DispatchChange(ObjectChangeEvent change, Delegate subscriber);
    }

    internal sealed class ObjectRecord<TState, TInput> : ObjectRecord, IReplicatedObject<TState, TInput>
        where TState : class
        where TInput : class
    {
        internal ObjectRecord(
            SessionEngine engine,
            ObjectTypeBinding<TState, TInput> type,
            NetworkObjectId id,
            ParticipantId? owner,
            byte[] bytes,
            ulong version)
            : base(engine, type, id, owner, bytes, version)
        {
            TypedType = type;
        }

        internal ObjectTypeBinding<TState, TInput> TypedType { get; }

        public string TypeId => Type.Key.Id;

        public bool IsSpawned => Life == ObjectLife.Live || Life == ObjectLife.PendingSpawn;

        public ParticipantId? OwnerId => CurrentOwner;

        public TState State => EngineCodec.DecodeTrusted(TypedType.Definition.StateCodec, CurrentBytes, "replicated object '" + Id + "'");

        public ulong Version => CurrentVersion;

        public Task<MultiplayerCommandConfirmation<TState>> SubmitInputAsync(TInput input, CancellationToken cancellationToken = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSpawned) throw new ObjectDisposedException(nameof(IReplicatedObject<TState, TInput>));
            return Engine.SubmitObjectInput(this, input, cancellationToken);
        }

        public OperationResult<bool> TransferOwnership(ParticipantId? ownerId) => Engine.TransferOwnership(this, ownerId);

        public OperationResult<TState> Apply(Func<TState, OperationResult<TState>> updater)
        {
            if (updater == null) throw new ArgumentNullException(nameof(updater));
            return Engine.MutateObject(this, () => ApplyUpdate(updater));
        }

        internal OperationResult<TState> DecodeOutcome(WireOutcome outcome)
        {
            if (!outcome.Succeeded) return OperationResult<TState>.Failure(outcome.Code, outcome.Message);
            return EngineCodec.Decode(TypedType.Definition.StateCodec, outcome.Payload!);
        }

        internal override WireOutcome InvokeInput(ParticipantId sender, ulong sequence, bool senderOwns, byte[] inputBytes, TransactionScope scope)
        {
            var definition = TypedType.Definition;
            var input = EngineCodec.Decode(definition.InputCodec, inputBytes);
            if (!input.TryGetValue(out var inputValue)) return WireOutcome.Failure(input.ErrorCode, input.ErrorMessage);
            var state = EngineCodec.Decode(definition.StateCodec, CurrentBytes);
            if (!state.TryGetValue(out var stateValue)) return WireOutcome.Failure(state.ErrorCode, state.ErrorMessage);
            scope.TouchObject(this);
            var context = new ReplicatedObjectCommandContext(
                sender,
                Id,
                Engine.CurrentTick,
                sequence,
                Engine.Seed,
                senderOwns,
                Engine.CurrentSessionToken,
                scope.CreateEmitter(Type.Key.Owner, sender));
            OperationResult<TState> result;
            try
            {
                result = definition.Handler(context, stateValue, inputValue) ??
                    OperationResult<TState>.Failure(ModErrorCode.Unknown, "The replicated-object handler returned no result.");
            }
            catch (Exception exception)
            {
                result = EngineCodec.FromException<TState>(exception, "The replicated-object handler failed.");
            }

            if (!result.TryGetValue(out var next)) return WireOutcome.Failure(result.ErrorCode, result.ErrorMessage);
            var encoded = EngineCodec.Encode(definition.StateCodec, next);
            if (!encoded.TryGetValue(out var bytes)) return WireOutcome.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            var check = EngineCodec.Decode(definition.StateCodec, bytes);
            if (!check.Succeeded) return WireOutcome.Failure(check.ErrorCode, check.ErrorMessage);
            CurrentBytes = bytes;
            CurrentVersion++;
            return WireOutcome.Success(bytes);
        }

        internal override void DispatchChange(ObjectChangeEvent change, Delegate subscriber)
        {
            if (subscriber is not Action<ReplicatedObjectChange<TState, TInput>> typed) return;
            var state = EngineCodec.Decode(TypedType.Definition.StateCodec, change.Bytes);
            if (!state.TryGetValue(out var value))
            {
                // The type is this game's own registration; on a joined player the object's id is the host's choice.
                Engine.ReportCodecFailure("a replicated object of type '" + Type.Key + "'", state.ErrorMessage);
                return;
            }

            var notification = new ReplicatedObjectChange<TState, TInput>(
                change.Kind,
                Id,
                change.Owner,
                value,
                change.Version,
                change.Kind == ReplicatedObjectChangeKind.Despawned ? null : this);
            Engine.InvokeCallback(typed, notification, "replicated object observer for '" + Type.Key + "'");
        }

        private OperationResult<TState> ApplyUpdate(Func<TState, OperationResult<TState>> updater)
        {
            var codec = TypedType.Definition.StateCodec;
            var detached = EngineCodec.Decode(codec, CurrentBytes);
            if (!detached.TryGetValue(out var current)) return detached;
            var proposed = updater(current) ??
                OperationResult<TState>.Failure(ModErrorCode.Unknown, "The replicated-object updater returned no result.");
            if (!proposed.TryGetValue(out var next)) return proposed;
            var encoded = EngineCodec.Encode(codec, next);
            if (!encoded.TryGetValue(out var bytes)) return OperationResult<TState>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            var response = EngineCodec.Decode(codec, bytes);
            if (!response.Succeeded) return response;
            CurrentBytes = bytes;
            CurrentVersion++;
            return response;
        }
    }
}
