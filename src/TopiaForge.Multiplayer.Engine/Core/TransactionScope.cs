using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal enum TransactionKind
    {
        /// <summary>Host work whose effects become canonical when it commits.</summary>
        Canonical = 0,

        /// <summary>A client's owner prediction: values change locally, nothing is published.</summary>
        Prediction = 1
    }

    /// <summary>A presentation event buffered until its transaction commits.</summary>
    internal readonly struct BufferedEvent
    {
        internal BufferedEvent(WireKey key, byte[] bytes, PresentationTarget target, ParticipantId sender)
        {
            Key = key;
            Bytes = bytes;
            Target = target;
            Sender = sender;
        }

        internal WireKey Key { get; }
        internal byte[] Bytes { get; }
        internal PresentationTarget Target { get; }
        internal ParticipantId Sender { get; }
    }

    /// <summary>
    /// The undo log and deferred effects of one transaction. The engine owns a single instance and reuses it, because
    /// transactions never nest.
    /// </summary>
    internal sealed class TransactionScope
    {
        private readonly Dictionary<StateRecord, StateBefore> states = new Dictionary<StateRecord, StateBefore>();
        private readonly Dictionary<ObjectRecord, ObjectBefore> objects = new Dictionary<ObjectRecord, ObjectBefore>();
        private readonly List<StateRecord> stateOrder = new List<StateRecord>();
        private readonly List<ObjectRecord> objectOrder = new List<ObjectRecord>();

        internal TransactionKind Kind { get; private set; }
        internal ParticipantId Sender { get; private set; }
        internal bool IsOpen { get; private set; }

        /// <summary>Gets a counter that changes with every transaction, so a stale context cannot emit into a later one.</summary>
        internal ulong Generation { get; private set; }
        internal List<ObjectRecord> Spawned { get; } = new List<ObjectRecord>();
        internal List<ObjectRecord> Despawned { get; } = new List<ObjectRecord>();
        internal List<BufferedEvent> Events { get; } = new List<BufferedEvent>();
        // Concrete list types keep foreach allocation-free on the per-step path.
        internal List<StateRecord> TouchedStates => stateOrder;
        internal List<ObjectRecord> TouchedObjects => objectOrder;

        internal void Open(TransactionKind kind, ParticipantId sender)
        {
            if (IsOpen) throw new InvalidOperationException("Engine transactions cannot nest.");
            Kind = kind;
            Sender = sender;
            IsOpen = true;
            Generation++;
        }

        internal void Close()
        {
            states.Clear();
            objects.Clear();
            stateOrder.Clear();
            objectOrder.Clear();
            Spawned.Clear();
            Despawned.Clear();
            Events.Clear();
            Sender = default;
            IsOpen = false;
        }

        internal void TouchState(StateRecord record)
        {
            if (states.ContainsKey(record)) return;
            states.Add(record, new StateBefore(record.CurrentBytes, record.CurrentVersion));
            stateOrder.Add(record);
        }

        internal void TouchObject(ObjectRecord record)
        {
            if (objects.ContainsKey(record)) return;
            objects.Add(record, new ObjectBefore(record.CurrentBytes, record.CurrentVersion, record.CurrentOwner, record.Life));
            objectOrder.Add(record);
        }

        internal bool StateChanged(StateRecord record) =>
            states.TryGetValue(record, out var before) &&
            (before.Version != record.CurrentVersion || !ReferenceEquals(before.Bytes, record.CurrentBytes));

        internal bool ObjectChanged(ObjectRecord record) =>
            objects.TryGetValue(record, out var before) &&
            (before.Version != record.CurrentVersion ||
             !ReferenceEquals(before.Bytes, record.CurrentBytes) ||
             !Nullable.Equals(before.Owner, record.CurrentOwner));

        /// <summary>Restores every touched value and object lifecycle to its state when the transaction began.</summary>
        internal void RestoreTouched()
        {
            foreach (var record in stateOrder)
            {
                var before = states[record];
                record.CurrentBytes = before.Bytes;
                record.CurrentVersion = before.Version;
            }

            foreach (var record in objectOrder)
            {
                var before = objects[record];
                record.CurrentBytes = before.Bytes;
                record.CurrentVersion = before.Version;
                record.CurrentOwner = before.Owner;
                record.Life = before.Life;
            }
        }

        /// <summary>Creates the event sink a command context buffers into. Predictions drop their events.</summary>
        internal Func<string, byte[], PresentationTarget, OperationResult<bool>> CreateEmitter(string owner, ParticipantId sender)
        {
            var generation = Generation;
            return (eventId, bytes, target) =>
            {
                if (!IsOpen || Generation != generation)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidState,
                        "Presentation events can be emitted only while the command runs.");
                }

                if (Kind == TransactionKind.Canonical) Events.Add(new BufferedEvent(new WireKey(owner, eventId), bytes, target, sender));
                return OperationResult<bool>.Success(true);
            };
        }

        private readonly struct StateBefore
        {
            internal StateBefore(byte[] bytes, ulong version)
            {
                Bytes = bytes;
                Version = version;
            }

            internal byte[] Bytes { get; }
            internal ulong Version { get; }
        }

        private readonly struct ObjectBefore
        {
            internal ObjectBefore(byte[] bytes, ulong version, ParticipantId? owner, ObjectLife life)
            {
                Bytes = bytes;
                Version = version;
                Owner = owner;
                Life = life;
            }

            internal byte[] Bytes { get; }
            internal ulong Version { get; }
            internal ParticipantId? Owner { get; }
            internal ObjectLife Life { get; }
        }
    }
}
