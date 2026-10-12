using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal enum HostConnectionPhase
    {
        AwaitingJoin = 0,
        Rejected = 1,
        Accepted = 2,
        Ready = 3,
        Closed = 4
    }

    /// <summary>The host's side of one client link: join progress, intern tables, ordered outbound work and pending deltas.</summary>
    internal sealed class HostConnection
    {
        internal HostConnection(IEngineLink link)
        {
            Link = link;
        }

        internal IEngineLink Link { get; }
        internal HostConnectionPhase Phase { get; set; } = HostConnectionPhase.AwaitingJoin;
        internal ParticipantRecord? Participant { get; set; }
        internal InternWriter OutIntern { get; } = new InternWriter();
        internal InternReader InIntern { get; } = new InternReader();
        internal List<OutboundItem> Queue { get; } = new List<OutboundItem>();
        internal InsertionOrderedSet<StateRecord> PendingStates { get; } = new InsertionOrderedSet<StateRecord>();
        internal InsertionOrderedSet<ObjectRecord> PendingObjects { get; } = new InsertionOrderedSet<ObjectRecord>();
        internal Dictionary<StateRecord, long> StateSentAt { get; } = new Dictionary<StateRecord, long>();
        internal Dictionary<ObjectRecord, long> ObjectSentAt { get; } = new Dictionary<ObjectRecord, long>();
        internal ulong AcknowledgedSequence { get; set; }
        internal bool ForceFlush { get; set; }
        internal bool CommitNeeded { get; set; }
        internal bool CloseAfterFlush { get; set; }
        internal long NextClockSyncAt { get; set; }

        /// <summary>Gets or sets the path state the host last reported in the participant list.</summary>
        internal EngineLinkState ReportedLinkState { get; set; } = EngineLinkState.Open;

        internal bool IsJoined => Phase == HostConnectionPhase.Accepted || Phase == HostConnectionPhase.Ready;

        internal bool HasOutbound => Queue.Count > 0 || CommitNeeded || CloseAfterFlush;

        internal bool HasPendingDeltas => PendingStates.Count > 0 || PendingObjects.Count > 0;

        internal void Enqueue(OutboundItem item) => Queue.Add(item);

        internal void ClearPendingDeltas()
        {
            PendingStates.Clear();
            PendingObjects.Clear();
        }
    }

    /// <summary>A participant the host knows: its own player, or a remote identity, connected or within its grace period.</summary>
    internal sealed class ParticipantRecord
    {
        internal ParticipantRecord(ParticipantId id, string? identity, string displayName, bool isHost)
        {
            Id = id;
            Identity = identity;
            DisplayName = displayName;
            IsHost = isHost;
        }

        internal ParticipantId Id { get; }

        /// <summary>Gets the authenticated link identity, or null for the host's own player.</summary>
        internal string? Identity { get; }

        internal string DisplayName { get; set; }
        internal bool IsHost { get; }
        internal bool IsConnected { get; set; }
        internal HostConnection? Connection { get; set; }
        internal long DepartedAtMicros { get; set; }
        internal ulong LastSequence { get; set; }
        internal long RttMicros { get; set; } = -1;
        internal string? PlayerKey { get; set; }

        /// <summary>Gets the round trip rounded to 10 ms before it leaves the host, or null when unmeasured.</summary>
        internal int? PingMilliseconds =>
            RttMicros < 0
                ? (int?)null
                : (int)Math.Min(MultiplayerParticipant.MaximumPingMilliseconds, ((RttMicros / 1000) + 5) / 10 * 10);

        internal MultiplayerConnectionQuality Quality
        {
            get
            {
                if (Connection != null && Connection.Link.State == EngineLinkState.Stalled) return MultiplayerConnectionQuality.Poor;
                if (RttMicros < 0) return MultiplayerConnectionQuality.Unknown;
                if (RttMicros < 60_000) return MultiplayerConnectionQuality.Excellent;
                if (RttMicros < 120_000) return MultiplayerConnectionQuality.Good;
                if (RttMicros < 250_000) return MultiplayerConnectionQuality.Fair;
                return MultiplayerConnectionQuality.Poor;
            }
        }

        internal ParticipantEntry ToEntry() =>
            new ParticipantEntry(Id.Value, DisplayName, IsConnected, IsHost, Quality, PingMilliseconds, PlayerKey);
    }

    /// <summary>A set that iterates in insertion order, so flushes are deterministic.</summary>
    internal sealed class InsertionOrderedSet<T> where T : class
    {
        private readonly HashSet<T> members = new HashSet<T>();
        private readonly List<T> order = new List<T>();

        internal int Count => members.Count;

        internal void Add(T item)
        {
            if (members.Add(item)) order.Add(item);
        }

        internal bool Remove(T item)
        {
            if (!members.Remove(item)) return false;
            order.Remove(item);
            return true;
        }

        internal bool Contains(T item) => members.Contains(item);

        internal void Clear()
        {
            members.Clear();
            order.Clear();
        }

        /// <summary>Copies the members, in insertion order, into <paramref name="target"/>.</summary>
        internal void CopyTo(List<T> target)
        {
            target.Clear();
            target.AddRange(order);
        }
    }
}
