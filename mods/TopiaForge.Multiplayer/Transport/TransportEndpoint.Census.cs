namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>
    /// The sizes of an endpoint's tables and buffers, so soak and fuzz tests can hold each to a ceiling. It counts entries
    /// and bytes only and names no path or peer.
    /// </summary>
    internal readonly struct TransportCensus
    {
        internal TransportCensus(
            int connections,
            int halfOpen,
            int initiations,
            int outboundDatagrams,
            long reassemblyBytes,
            long sendBacklogBytes,
            int packetsInFlight,
            int queuedDatagrams,
            int outstandingQueries,
            int queuedInfoReplies)
        {
            Connections = connections;
            HalfOpen = halfOpen;
            Initiations = initiations;
            OutboundDatagrams = outboundDatagrams;
            ReassemblyBytes = reassemblyBytes;
            SendBacklogBytes = sendBacklogBytes;
            PacketsInFlight = packetsInFlight;
            QueuedDatagrams = queuedDatagrams;
            OutstandingQueries = outstandingQueries;
            QueuedInfoReplies = queuedInfoReplies;
        }

        /// <summary>Gets every connection the endpoint still routes to: handshaking, open, closing or draining.</summary>
        internal int Connections { get; }

        internal int HalfOpen { get; }

        /// <summary>Gets the responder handshakes keyed by their initiator, for duplicate Initiations.</summary>
        internal int Initiations { get; }

        /// <summary>Gets the datagrams queued for the I/O layer and not yet taken.</summary>
        internal int OutboundDatagrams { get; }

        internal long ReassemblyBytes { get; }

        internal long SendBacklogBytes { get; }

        internal int PacketsInFlight { get; }

        internal int QueuedDatagrams { get; }

        internal int OutstandingQueries { get; }

        internal int QueuedInfoReplies { get; }

        public override string ToString() =>
            "connections " + Connections + " (" + HalfOpen + " half-open, " + Initiations + " initiations), outbound " + OutboundDatagrams +
            ", reassembly " + ReassemblyBytes + " B, backlog " + SendBacklogBytes + " B, in flight " + PacketsInFlight +
            ", datagrams " + QueuedDatagrams + ", queries " + OutstandingQueries + "/" + QueuedInfoReplies;
    }

    internal sealed partial class TransportEndpoint
    {
        /// <summary>Counts the endpoint's tables and buffers; on the endpoint's thread only, like every other call.</summary>
        internal TransportCensus Census()
        {
            long backlog = 0;
            var inFlight = 0;
            var datagrams = 0;
            foreach (var connection in connections)
            {
                backlog += connection.SendBacklogBytes;
                inFlight += connection.PacketsInFlight;
                datagrams += connection.QueuedDatagrams;
            }

            return new TransportCensus(
                connections.Count,
                halfOpen,
                initiations.Count,
                outbound.Count,
                ReassemblyBytes,
                backlog,
                inFlight,
                datagrams,
                outstandingQueries.Count,
                infoReplies.Count);
        }
    }
}
