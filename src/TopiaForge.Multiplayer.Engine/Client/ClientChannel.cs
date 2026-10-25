using System;
using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Engine
{
    internal enum ClientPhase
    {
        AwaitingJoin = 0,
        Synchronizing = 1,
        Ready = 2
    }

    /// <summary>A client's connection to its host: the link, intern tables, join progress and unsent work.</summary>
    internal sealed class ClientChannel
    {
        internal ClientChannel(IEngineLink link, string displayName, byte[] content)
        {
            Link = link;
            DisplayName = displayName;
            Content = content;
        }

        internal IEngineLink Link { get; }
        internal string DisplayName { get; }
        internal byte[] Content { get; }
        internal InternWriter OutIntern { get; } = new InternWriter();
        internal InternReader InIntern { get; } = new InternReader();
        internal ClientPhase Phase { get; set; } = ClientPhase.AwaitingJoin;
        internal bool JoinPending { get; set; } = true;
        internal bool ReadyAckPending { get; set; }
        internal List<PendingSubmission> Unsent { get; } = new List<PendingSubmission>();

        /// <summary>Gets or sets the host time of the latest clock probe, echoed once; older probes go unanswered.</summary>
        internal ulong? PendingClockReply { get; set; }

        internal SnapshotStaging? Staging { get; set; }
        internal bool SnapshotCompleted { get; set; }

        /// <summary>Gets or sets whether the snapshot is complete but a readiness hold keeps the client short of Ready.</summary>
        internal bool ReadinessHeld { get; set; }

        /// <summary>Gets or sets whether the host must hear that its replacement reached this player.</summary>
        internal bool ReplacementReceivedPending { get; set; }

        /// <summary>Gets the system messages queued for the host since the last flush.</summary>
        internal List<SystemWireMessage> SystemOutbox { get; } = new List<SystemWireMessage>();

        /// <summary>Gets or sets the join generation the host accepted this connection with.</summary>
        internal ulong JoinGeneration { get; set; }

        /// <summary>Gets this connection's system-channel streams with the host.</summary>
        internal SystemStreamSet Streams { get; } = new SystemStreamSet();

        /// <summary>Gets or sets the rate this player keeps to, so its host never has to refuse it.</summary>
        internal TokenBucket? SystemSendRate { get; set; }

        /// <summary>Gets or sets the rate this player's datagrams keep to, half what its host takes.</summary>
        internal TokenBucket? DatagramSendRate { get; set; }

        /// <summary>
        /// Gets or sets whether a host flush has started arriving without its closing <c>Commit</c>. Confirmed values
        /// are then only partly updated, so reconciliation waits for the <c>Commit</c>.
        /// </summary>
        internal bool FlushOpen { get; set; }

        /// <summary>Gets or sets whether the local listener mute set must be sent to the host.</summary>
        internal bool VoiceMutesPending { get; set; }

        internal bool HasOutbound =>
            JoinPending || ReadyAckPending || ReplacementReceivedPending || Unsent.Count > 0 || PendingClockReply.HasValue || VoiceMutesPending ||
            SystemOutbox.Count > 0 || Streams.CreditsDue;

        /// <summary>Starts every system-channel stream again, as a session replacement does.</summary>
        internal void RestartStreams() => Streams.Restart();
    }

    /// <summary>Canonical values received between <c>SnapshotBegin</c> and <c>SnapshotEnd</c>.</summary>
    internal sealed class SnapshotStaging
    {
        internal List<StagedState> States { get; } = new List<StagedState>();
        internal List<ObjectWireMessage> Objects { get; } = new List<ObjectWireMessage>();
    }

    internal readonly struct StagedState
    {
        internal StagedState(WireKey key, ulong version, byte[] bytes)
        {
            Key = key;
            Version = version;
            Bytes = bytes;
        }

        internal WireKey Key { get; }
        internal ulong Version { get; }
        internal byte[] Bytes { get; }
    }

    /// <summary>The most recent despawned object ids of a session, so a stale message cannot resurrect one.</summary>
    internal sealed class TombstoneSet
    {
        private const int Capacity = 8192;
        private readonly HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> order = new Queue<string>();

        internal int Count => order.Count;

        internal bool Contains(string id) => ids.Contains(id);

        internal void Add(string id)
        {
            if (!ids.Add(id)) return;
            order.Enqueue(id);
            if (order.Count > Capacity) ids.Remove(order.Dequeue());
        }

        internal void Clear()
        {
            ids.Clear();
            order.Clear();
        }
    }
}
