using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>Per-flush scratch shared by the items a host writes for one connection.</summary>
    internal sealed class FlushContext
    {
        internal ProtocolWriter Frame { get; } = new ProtocolWriter(4096);
        internal ProtocolWriter Body { get; } = new ProtocolWriter(1024);
        internal HashSet<ObjectRecord> SkippedSpawns { get; } = new HashSet<ObjectRecord>();
        internal List<StateRecord> States { get; } = new List<StateRecord>();
        internal List<ObjectRecord> Objects { get; } = new List<ObjectRecord>();
        internal bool WroteMessage { get; set; }

        /// <summary>Sends the frame built so far when it has grown past the soft size, so large flushes split cleanly.</summary>
        internal System.Action? SplitIfLarge { get; set; }
    }

    /// <summary>One ordered message a host owes a connection. Items capture their data when queued.</summary>
    internal abstract class OutboundItem
    {
        internal abstract void Write(HostConnection connection, FlushContext context);
    }

    internal sealed class JoinAcceptedItem : OutboundItem
    {
        private readonly JoinAcceptedMessage message;

        internal JoinAcceptedItem(JoinAcceptedMessage message)
        {
            this.message = message;
        }

        internal override void Write(HostConnection connection, FlushContext context) =>
            JoinMessages.WriteJoinAccepted(context.Frame, context.Body, message);
    }

    internal sealed class JoinRejectedItem : OutboundItem
    {
        private readonly JoinRejectedMessage message;

        internal JoinRejectedItem(JoinRejectedMessage message)
        {
            this.message = message;
        }

        internal override void Write(HostConnection connection, FlushContext context) =>
            JoinMessages.WriteJoinRejected(context.Frame, context.Body, message);
    }

    internal sealed class SessionEndedItem : OutboundItem
    {
        private readonly MultiplayerSessionEndReason reason;
        private readonly string message;

        internal SessionEndedItem(MultiplayerSessionEndReason reason, string message)
        {
            this.reason = reason;
            this.message = message;
        }

        internal override void Write(HostConnection connection, FlushContext context) =>
            SessionMessages.WriteSessionEnded(context.Frame, context.Body, reason, message);
    }

    internal sealed class SessionReplacedItem : OutboundItem
    {
        private readonly SessionReplacedMessage message;

        internal SessionReplacedItem(SessionReplacedMessage message)
        {
            this.message = message;
        }

        internal override void Write(HostConnection connection, FlushContext context) =>
            SessionMessages.WriteSessionReplaced(context.Frame, context.Body, message);
    }

    /// <summary>Every canonical state and object at the moment a connection joined or the session was replaced.</summary>
    internal sealed class SnapshotItem : OutboundItem
    {
        private readonly ulong epoch;
        private readonly List<StagedState> states;
        private readonly List<ObjectWireMessage> objects;

        internal SnapshotItem(ulong epoch, List<StagedState> states, List<ObjectWireMessage> objects)
        {
            this.epoch = epoch;
            this.states = states;
            this.objects = objects;
        }

        internal override void Write(HostConnection connection, FlushContext context)
        {
            SessionMessages.WriteEpochMessage(context.Frame, context.Body, MessageType.SnapshotBegin, epoch);
            foreach (var state in states)
            {
                ReplicationMessages.WriteStateDelta(
                    context.Frame,
                    context.Body,
                    connection.OutIntern,
                    state.Key.Owner,
                    state.Key.Id,
                    state.Version,
                    state.Bytes);
                context.SplitIfLarge?.Invoke();
            }

            foreach (var item in objects)
            {
                ReplicationMessages.WriteObjectSpawned(context.Frame, context.Body, connection.OutIntern, item);
                context.SplitIfLarge?.Invoke();
            }

            SessionMessages.WriteEmpty(context.Frame, context.Body, MessageType.SnapshotEnd);
        }
    }

    internal sealed class ParticipantsItem : OutboundItem
    {
        private readonly List<ParticipantEntry> entries;

        internal ParticipantsItem(List<ParticipantEntry> entries)
        {
            this.entries = entries;
        }

        internal override void Write(HostConnection connection, FlushContext context) =>
            SessionMessages.WriteParticipants(context.Frame, context.Body, entries);
    }

    internal sealed class ResultItem : OutboundItem
    {
        private readonly byte type;
        private readonly ulong sequence;
        private readonly ulong confirmedAt;
        private readonly WireOutcome outcome;

        internal ResultItem(byte type, ulong sequence, ulong confirmedAt, WireOutcome outcome)
        {
            this.type = type;
            this.sequence = sequence;
            this.confirmedAt = confirmedAt;
            this.outcome = outcome;
        }

        internal override void Write(HostConnection connection, FlushContext context) =>
            ReplicationMessages.WriteResult(context.Frame, context.Body, type, sequence, confirmedAt, outcome);
    }

    /// <summary>A spawn captured at commit. If the object is already gone when the flush runs, neither message is sent.</summary>
    internal sealed class SpawnItem : OutboundItem
    {
        private readonly ObjectRecord record;
        private readonly ObjectWireMessage message;

        internal SpawnItem(ObjectRecord record, ObjectWireMessage message)
        {
            this.record = record;
            this.message = message;
        }

        internal override void Write(HostConnection connection, FlushContext context)
        {
            if (record.Life == ObjectLife.Despawned)
            {
                context.SkippedSpawns.Add(record);
                return;
            }

            ReplicationMessages.WriteObjectSpawned(context.Frame, context.Body, connection.OutIntern, message);
        }
    }

    internal sealed class DespawnItem : OutboundItem
    {
        private readonly ObjectRecord record;
        private readonly ulong version;
        private readonly byte[] bytes;

        internal DespawnItem(ObjectRecord record)
        {
            this.record = record;
            version = record.ConfirmedVersion;
            bytes = record.ConfirmedBytes;
        }

        internal override void Write(HostConnection connection, FlushContext context)
        {
            connection.PendingObjects.Remove(record);
            if (context.SkippedSpawns.Remove(record)) return;
            ReplicationMessages.WriteObjectDespawned(context.Frame, context.Body, record.Id.Value, version, bytes);
        }
    }

    internal sealed class PresentationItem : OutboundItem
    {
        private readonly WireKey key;
        private readonly byte[] bytes;

        internal PresentationItem(WireKey key, byte[] bytes)
        {
            this.key = key;
            this.bytes = bytes;
        }

        internal override void Write(HostConnection connection, FlushContext context) =>
            ReplicationMessages.WritePresentation(context.Frame, context.Body, connection.OutIntern, key.Owner, key.Id, bytes);
    }

    internal sealed class ClockSyncItem : OutboundItem
    {
        private readonly long hostMicros;
        private readonly ulong tick;
        private readonly long rttMicros;

        internal ClockSyncItem(long hostMicros, ulong tick, long rttMicros)
        {
            this.hostMicros = hostMicros;
            this.tick = tick;
            this.rttMicros = rttMicros;
        }

        internal override void Write(HostConnection connection, FlushContext context) =>
            SessionMessages.WriteClockSync(context.Frame, context.Body, hostMicros, tick, rttMicros);
    }
}
