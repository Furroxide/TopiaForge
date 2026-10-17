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

        /// <summary>Gets or sets when the host accepted the link, which bounds how long it may wait to ask to join.</summary>
        internal long AcceptedAtMicros { get; set; }
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

        /// <summary>Gets or sets whether a clock probe awaits its reply. Only the latest probe's echo is accepted.</summary>
        internal bool ProbePending { get; set; }

        /// <summary>Gets or sets the host time the pending clock probe carried.</summary>
        internal long ProbeMicros { get; set; }

        /// <summary>Gets or sets the path state the host last reported in the participant list.</summary>
        internal EngineLinkState ReportedLinkState { get; set; } = EngineLinkState.Open;

        /// <summary>Gets or sets the join generation this connection was accepted with, stamped on its system-channel traffic.</summary>
        internal ulong JoinGeneration { get; set; }

        /// <summary>Gets or sets the generation the system channels report for this connection's streams; it changes on every restart.</summary>
        internal ulong StreamGeneration { get; set; }

        /// <summary>Gets this connection's system-channel streams.</summary>
        internal SystemStreamSet Streams { get; } = new SystemStreamSet();

        /// <summary>Gets or sets the rate at which the player may send system messages, enforced as they arrive.</summary>
        internal TokenBucket? SystemIngress { get; set; }

        /// <summary>Gets or sets the allowance within which the host counts the player's stale system messages.</summary>
        internal TokenBucket? StaleSystemIngress { get; set; }

        /// <summary>
        /// Gets or sets when the host began waiting for this connection to acknowledge a snapshot and become Ready: its
        /// acceptance, or the replacement that took it out of Ready, or the player's report that this replacement reached
        /// it. A replacement while the player is still joining starts nothing over.
        /// </summary>
        internal long SnapshotSentAtMicros { get; set; }

        /// <summary>
        /// Gets or sets the epoch of the replacement that took this player out of Ready, while the host still waits for the
        /// player's report that it arrived; zero otherwise.
        /// </summary>
        internal ulong AwaitedReceiptEpoch { get; set; }

        /// <summary>Gets or sets when the player last acknowledged a snapshot and became Ready.</summary>
        internal long ReadyAtMicros { get; set; }

        internal bool IsJoined => Phase == HostConnectionPhase.Accepted || Phase == HostConnectionPhase.Ready;

        /// <summary>Gets or sets the speakers this client asked not to have relayed, by participant id.</summary>
        internal HashSet<string> VoiceMutes { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Gets or sets whether this client asked for no voice at all.</summary>
        internal bool VoiceMuteAll { get; set; }

        internal bool HasOutbound => Queue.Count > 0 || CommitNeeded || CloseAfterFlush || Streams.CreditsDue;

        /// <summary>Starts every system-channel stream again, as a session replacement does.</summary>
        internal void RestartStreams() => Streams.Restart();

        internal bool HasPendingDeltas => PendingStates.Count > 0 || PendingObjects.Count > 0;

        internal void Enqueue(OutboundItem item) => Queue.Add(item);

        internal void ClearPendingDeltas()
        {
            PendingStates.Clear();
            PendingObjects.Clear();
        }

        /// <summary>Forgets a state for good: its pending delta and its update-rate history.</summary>
        internal void ForgetState(StateRecord record)
        {
            PendingStates.Remove(record);
            StateSentAt.Remove(record);
        }

        /// <summary>Forgets a despawned object: its pending change and its update-rate history.</summary>
        internal void ForgetObject(ObjectRecord record)
        {
            PendingObjects.Remove(record);
            ObjectSentAt.Remove(record);
        }

        /// <summary>Drops pending deltas and update-rate history: a session replacement resends everything in a snapshot.</summary>
        internal void ResetForNewSession()
        {
            ClearPendingDeltas();
            StateSentAt.Clear();
            ObjectSentAt.Clear();
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

        /// <summary>Gets or sets the host's voice state for this participant, or null when voice is off.</summary>
        internal VoiceSpeaker? Voice { get; set; }

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
