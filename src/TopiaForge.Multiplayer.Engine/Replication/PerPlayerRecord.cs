using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    // These records never enter the shared state inventory, snapshot, intern table or delta queue.
    internal abstract class PerPlayerRecord : IDisposable
    {
        protected PerPlayerRecord(SessionEngine engine, WireKey key, byte[] initial)
        { Engine = engine; Key = key; Initial = initial; }

        protected SessionEngine Engine { get; }
        internal WireKey Key { get; }
        internal byte[] Initial { get; }
        internal bool Active { get; private set; } = true;
        internal Dictionary<ParticipantId, StateRecord> Values { get; } = new Dictionary<ParticipantId, StateRecord>();
        internal abstract StateRecord Slot(ParticipantId player);
        protected abstract void ReleaseSubscribers();

        internal void Reset()
        {
            foreach (var slot in Values.Values)
            {
                slot.ResetTo(Initial, 0);
                Engine.PrivateValueChanged(slot);
            }
        }

        internal void Forget(ParticipantId player)
        {
            if (!Values.TryGetValue(player, out var slot)) return;
            slot.IsActive = false;
            Values.Remove(player);
        }

        public void Dispose() => Engine.Post(() =>
        {
            if (!Active) return;
            Active = false;
            foreach (var slot in Values.Values) slot.IsActive = false;
            Values.Clear();
            ReleaseSubscribers();
            Engine.RemovePrivateState(this);
        });
    }

    internal sealed class PerPlayerRecord<T> : PerPlayerRecord, IPerPlayerState<T> where T : class
    {
        private readonly ReplicatedStateDefinition<T> definition;
        private readonly List<Action<T>> subscribers = new List<Action<T>>();

        protected override void ReleaseSubscribers() => subscribers.Clear();

        internal PerPlayerRecord(SessionEngine engine, WireKey key, ReplicatedStateDefinition<T> definition, byte[] initial)
            : base(engine, key, initial) => this.definition = definition;

        internal override StateRecord Slot(ParticipantId player)
        {
            if (Values.TryGetValue(player, out var slot)) return slot;
            slot = new StateRecord<T>(Engine, Key, definition, Initial)
            {
                PrivatePlayer = player,
                PrivateNotification = Notify
            };
            Values.Add(player, slot);
            return slot;
        }

        public OperationResult<T> Get(ParticipantId player)
        {
            var refusal = Check(player, false);
            if (refusal != null) return refusal;
            return EngineCodec.Decode(definition.Codec, Slot(player).CurrentBytes);
        }

        public OperationResult<T> Set(ParticipantId player, T value)
        {
            var refusal = Check(player, true);
            if (refusal != null) return refusal;
            if (value == null) return OperationResult<T>.Failure(ModErrorCode.InvalidArgument, "A private value is required.");
            return ((IReplicatedState<T>)Slot(player)).Update(_ => OperationResult<T>.Success(value));
        }

        public IDisposable SubscribeChanged(Action<T> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            Engine.RequireOwnerThread();
            if (!Active) throw new ObjectDisposedException(nameof(IPerPlayerState<T>));
            subscribers.Add(handler);
            return new EngineLease(() => Engine.Post(() => subscribers.Remove(handler)));
        }

        private OperationResult<T>? Check(ParticipantId player, bool write)
        {
            Engine.RequireOwnerThread();
            if (!Active || Engine.IsEnded) return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The private state is no longer active.");
            if (!Engine.HasServerSide && (write || !Engine.LocalParticipantId.HasValue || !Engine.LocalParticipantId.Value.Equals(player)))
                return OperationResult<T>.Failure(ModErrorCode.NotAuthoritative, "Only the host writes private state; a joined player reads only itself.");
            if (!Engine.HasPrivatePlayer(player)) return OperationResult<T>.Failure(ModErrorCode.NotFound, "The player is not admitted to this session.");
            return null;
        }

        private void Notify(byte[] bytes)
        {
            if (!Active) return;
            foreach (var handler in subscribers.ToArray())
            {
                var copy = EngineCodec.Decode(definition.Codec, bytes);
                if (copy.TryGetValue(out var value)) Engine.InvokeCallback(handler, value, "private state observer");
            }
        }
    }
}
