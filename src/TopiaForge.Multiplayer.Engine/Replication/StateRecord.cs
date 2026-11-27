using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// One registered replicated state. Encoded bytes are the source of truth, so every read decodes a fresh,
    /// detached value. A host keeps confirmed and current equal outside a transaction; a client's current value
    /// carries its pending predictions on top of the last confirmed value.
    /// </summary>
    internal abstract class StateRecord
    {
        protected StateRecord(
            SessionEngine engine,
            WireKey key,
            byte[] initialBytes,
            int maximumBytes,
            MultiplayerDelivery delivery,
            int maximumUpdatesPerSecond)
        {
            Engine = engine;
            Key = key;
            InitialBytes = initialBytes;
            MaximumBytes = maximumBytes;
            Delivery = delivery;
            MaximumUpdatesPerSecond = maximumUpdatesPerSecond;
            ConfirmedBytes = initialBytes;
            CurrentBytes = initialBytes;
            NotifiedBytes = initialBytes;
        }

        internal ParticipantId? PrivatePlayer { get; set; }
        internal Action<byte[]>? PrivateNotification { get; set; }
        internal SessionEngine Engine { get; }
        internal WireKey Key { get; }
        internal byte[] InitialBytes { get; }
        internal int MaximumBytes { get; }
        internal MultiplayerDelivery Delivery { get; }
        internal int MaximumUpdatesPerSecond { get; }
        internal byte[] ConfirmedBytes { get; set; }
        internal ulong ConfirmedVersion { get; set; }
        internal byte[] CurrentBytes { get; set; }
        internal ulong CurrentVersion { get; set; }
        internal byte[] NotifiedBytes { get; set; }
        internal ulong NotifiedVersion { get; set; }
        internal bool IsActive { get; set; } = true;
        internal bool NotificationQueued { get; set; }

        internal bool CurrentMatchesConfirmed =>
            CurrentVersion == ConfirmedVersion && EngineCodec.BytesEqual(CurrentBytes, ConfirmedBytes);

        internal bool VisibleValueChanged =>
            CurrentVersion != NotifiedVersion || !EngineCodec.BytesEqual(CurrentBytes, NotifiedBytes);

        internal void ResetTo(byte[] bytes, ulong version)
        {
            ConfirmedBytes = bytes;
            ConfirmedVersion = version;
            CurrentBytes = bytes;
            CurrentVersion = version;
        }

        /// <summary>Checks that canonical bytes from the wire decode with the local codec.</summary>
        internal abstract bool CanDecode(byte[] bytes);

        /// <summary>Notifies subscribers of <paramref name="reported"/>, each with its own decoded copy.</summary>
        internal abstract void NotifySubscribers(byte[] reported);
    }

    internal sealed class StateRecord<T> : StateRecord, IReplicatedState<T> where T : class
    {
        private readonly IMultiplayerCodec<T> codec;
        private readonly List<Action<T>> subscribers = new List<Action<T>>();

        internal StateRecord(SessionEngine engine, WireKey key, ReplicatedStateDefinition<T> definition, byte[] initialBytes)
            : base(
                engine,
                key,
                initialBytes,
                definition.Codec.MaximumEncodedBytes,
                definition.Delivery,
                definition.MaximumUpdatesPerSecond)
        {
            codec = definition.Codec;
        }

        public string Id => Key.Id;

        public T Value => EngineCodec.DecodeTrusted(codec, CurrentBytes, "replicated state '" + Key + "'");

        public ulong Version => CurrentVersion;

        public OperationResult<T> Update(Func<T, OperationResult<T>> updater)
        {
            if (updater == null) throw new ArgumentNullException(nameof(updater));
            return Engine.MutateState(this, () => ApplyUpdate(updater));
        }

        public IDisposable SubscribeChanged(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (!IsActive) throw new ObjectDisposedException(nameof(IReplicatedState<T>));
            Engine.RequireOwnerThread();
            subscribers.Add(handler);
            return new EngineLease(() => Engine.Post(() => subscribers.Remove(handler)));
        }

        public void Dispose() => Engine.UnregisterState(this);

        internal override bool CanDecode(byte[] bytes) => bytes.Length <= MaximumBytes && EngineCodec.Decode(codec, bytes).Succeeded;

        internal override void NotifySubscribers(byte[] reported)
        {
            if (PrivatePlayer.HasValue)
            {
                // The engine selects the local player when queuing. A disconnect reset still needs to
                // notify after the local participant id has gone, so the presentation clears immediately.
                PrivateNotification?.Invoke(reported);
                return;
            }
            if (subscribers.Count == 0) return;
            // Every observer in this round sees the value being reported, even if an earlier observer changes the
            // state again; that change is reported in a later round.
            foreach (var subscriber in subscribers.ToArray())
            {
                var copy = EngineCodec.Decode(codec, reported);
                if (!copy.TryGetValue(out var value))
                {
                    Engine.ReportCodecFailure("replicated state '" + Key + "'", copy.ErrorMessage);
                    return;
                }

                Engine.InvokeCallback(subscriber, value, "replicated state observer for '" + Key + "'");
            }
        }

        private OperationResult<T> ApplyUpdate(Func<T, OperationResult<T>> updater)
        {
            var detached = EngineCodec.Decode(codec, CurrentBytes);
            if (!detached.TryGetValue(out var current)) return detached;
            var proposed = updater(current) ??
                OperationResult<T>.Failure(ModErrorCode.Unknown, "The replicated-state updater returned no result.");
            if (!proposed.TryGetValue(out var next)) return proposed;
            var encoded = EngineCodec.Encode(codec, next);
            if (!encoded.TryGetValue(out var bytes)) return OperationResult<T>.Failure(encoded.ErrorCode, encoded.ErrorMessage);
            var response = EngineCodec.Decode(codec, bytes);
            if (!response.Succeeded) return response;
            Engine.WriteState(this, bytes);
            return response;
        }
    }
}
