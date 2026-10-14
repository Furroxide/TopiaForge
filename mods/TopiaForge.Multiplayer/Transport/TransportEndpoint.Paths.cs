using System;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>How the endpoint's connections use one path: a count, never which peers they are.</summary>
    internal readonly struct TransportPathUsage
    {
        internal TransportPathUsage(int connections, int stalled, int validating)
        {
            Connections = connections;
            Stalled = stalled;
            Validating = validating;
        }

        /// <summary>Gets how many established or closing connections send on the path.</summary>
        internal int Connections { get; }

        /// <summary>Gets how many of them have heard nothing authenticated for the stall time.</summary>
        internal int Stalled { get; }

        /// <summary>Gets how many of them are validating another path right now.</summary>
        internal int Validating { get; }
    }

    /// <summary>
    /// The questions the NAT layer asks about paths: which connections use a path, and moving them to another. All
    /// work by path, so the NAT layer never holds a connection, and the transport's own validation decides every move.
    /// </summary>
    internal sealed partial class TransportEndpoint
    {
        /// <summary>Counts the connections that send on <paramref name="path"/>.</summary>
        internal TransportPathUsage UsageOf(TransportPath path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var count = 0;
            var stalled = 0;
            var validating = 0;
            foreach (var connection in connections)
            {
                if (connection.State != TransportConnectionState.Established && connection.State != TransportConnectionState.Closing) continue;
                if (!connection.Path.Equals(path)) continue;
                count++;
                if (connection.IsStalled) stalled++;
                if (connection.IsValidatingPath) validating++;
            }

            return new TransportPathUsage(count, stalled, validating);
        }

        /// <summary>Counts the established or closing connections whose path <paramref name="where"/> accepts.</summary>
        internal int CountConnections(Predicate<TransportPath> where)
        {
            if (where == null) throw new ArgumentNullException(nameof(where));
            var count = 0;
            foreach (var connection in connections)
            {
                if ((connection.State == TransportConnectionState.Established || connection.State == TransportConnectionState.Closing) && where(connection.Path)) count++;
            }

            return count;
        }

        /// <summary>
        /// Asks every established connection on <paramref name="from"/> to try <paramref name="to"/>; each moves only once
        /// the peer answers its challenge there. Returns how many started a validation.
        /// </summary>
        internal int ProbeConnections(TransportPath from, TransportPath to, long now)
        {
            if (from == null) throw new ArgumentNullException(nameof(from));
            if (to == null) throw new ArgumentNullException(nameof(to));
            var started = 0;
            foreach (var connection in connections)
            {
                if (connection.State == TransportConnectionState.Established && connection.Path.Equals(from) && connection.ProbePath(to, now)) started++;
            }

            return started;
        }
    }
}
