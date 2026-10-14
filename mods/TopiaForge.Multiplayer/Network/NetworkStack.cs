using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    internal enum ConnectAttemptState
    {
        Pending = 0,
        Established = 1,
        Failed = 2
    }

    /// <summary>
    /// An outbound connection in progress. The main thread holds it and reads it every frame; the network thread fills
    /// it in once the handshake completes or fails.
    /// </summary>
    internal sealed class ConnectAttempt
    {
        private int state;
        private NetworkLink? link;
        private int failure;

        internal ConnectAttemptState State => (ConnectAttemptState)Volatile.Read(ref state);

        /// <summary>Gets the link once the attempt is established.</summary>
        internal NetworkLink? Link => Volatile.Read(ref link);

        /// <summary>Gets why the attempt failed: the transport's close reason.</summary>
        internal TransportCloseReason Failure => (TransportCloseReason)Volatile.Read(ref failure);

        internal void Succeed(NetworkLink established)
        {
            Volatile.Write(ref link, established);
            Volatile.Write(ref state, (int)ConnectAttemptState.Established);
        }

        internal void Fail(TransportCloseReason reason)
        {
            Volatile.Write(ref failure, (int)reason);
            Volatile.Write(ref state, (int)ConnectAttemptState.Failed);
        }
    }

    /// <summary>
    /// The network layer without I/O: one transport endpoint and the links over its connections. Whoever owns it, the
    /// network thread in the game or a virtual network in tests, hands it datagrams and the time and sends what it
    /// yields. It moves frames between connections and links, accepts inbound links, completes outbound attempts, and
    /// gives each peer the player key this host derives for it.
    /// </summary>
    internal sealed class NetworkStack : IDisposable
    {
        private readonly List<Pair> pairs = new List<Pair>();
        private readonly List<PendingConnect> pending = new List<PendingConnect>();
        private readonly Queue<NetworkLink> accepted = new Queue<NetworkLink>();
        private readonly Func<byte[], MultiplayerPlayerKey>? playerKeys;
        private readonly Func<TransportPath, string?> sourceOf;
        private readonly int maximumLinksPerSource;
        private bool disposed;

        /// <param name="endpoint">The endpoint this stack owns from now on; disposing the stack disposes it.</param>
        /// <param name="playerKeys">Derives the player key for a peer's transport key; null on a client, which gives none.</param>
        /// <param name="maximumLinksPerSource">How many inbound links one source may hold at once; a host keeps it small.</param>
        /// <param name="sourceOf">Names the host a path reaches, or null where sources cannot be told apart; UDP addresses by default.</param>
        internal NetworkStack(
            TransportEndpoint endpoint,
            Func<byte[], MultiplayerPlayerKey>? playerKeys = null,
            int maximumLinksPerSource = int.MaxValue,
            Func<TransportPath, string?>? sourceOf = null)
        {
            if (maximumLinksPerSource < 1) throw new ArgumentOutOfRangeException(nameof(maximumLinksPerSource));
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            this.playerKeys = playerKeys;
            this.maximumLinksPerSource = maximumLinksPerSource;
            this.sourceOf = sourceOf ?? UdpPaths.SourceOf;
        }

        internal TransportEndpoint Endpoint { get; }

        /// <summary>Gets how many links are open, or still closing.</summary>
        internal int LinkCount => pairs.Count;

        /// <summary>Gets how many inbound links were closed because their source already held its share.</summary>
        internal long RefusedForSource { get; private set; }

        /// <summary>Takes one datagram. Once the stack is disposed, datagrams are ignored rather than refused.</summary>
        internal void Receive(TransportPath path, byte[] datagram, int offset, int count, long now)
        {
            if (!disposed) Endpoint.Receive(path, datagram, offset, count, now);
        }

        /// <summary>Moves frames both ways, then runs the endpoint's timers so whatever was queued goes out now.</summary>
        internal void Poll(long now)
        {
            if (disposed) return;
            AcceptConnections();
            CompleteAttempts();
            PumpLinks();
            Endpoint.Poll(now);
            PumpLinks();
        }

        internal long NextTimeoutMicros() => disposed ? long.MaxValue : Endpoint.NextTimeoutMicros();

        internal bool TryDequeueDatagram(out OutgoingDatagram datagram)
        {
            if (!disposed) return Endpoint.TryDequeueDatagram(out datagram);
            datagram = default;
            return false;
        }

        internal void Release(OutgoingDatagram datagram) => Endpoint.Release(datagram);

        /// <summary>
        /// Starts a connection to <paramref name="path"/>, pinned to <paramref name="expectedKey"/> when one is known.
        /// Every attempt completes: one the stack cannot start fails at once rather than throwing.
        /// </summary>
        internal void Connect(ConnectAttempt attempt, TransportPath path, byte[]? expectedKey, long now)
        {
            if (attempt == null) throw new ArgumentNullException(nameof(attempt));
            if (disposed)
            {
                attempt.Fail(TransportCloseReason.Local);
                return;
            }

            TransportConnection connection;
            try
            {
                connection = Endpoint.Connect(path, now, expectedKey);
            }
            catch (ArgumentException)
            {
                // Relay-only refuses a direct path before anything is sent.
                attempt.Fail(TransportCloseReason.Local);
                return;
            }
            catch (InvalidOperationException)
            {
                // The endpoint already holds as many connections as it may.
                attempt.Fail(TransportCloseReason.Overloaded);
                return;
            }

            pending.Add(new PendingConnect(connection, attempt));
        }

        /// <summary>Yields the next link a peer opened to this host.</summary>
        internal bool TryAccept(out NetworkLink link)
        {
            if (accepted.Count > 0)
            {
                link = accepted.Dequeue();
                return true;
            }

            link = null!;
            return false;
        }

        /// <summary>
        /// Closes every link gracefully, the way the main thread would, so frames the engine queued before the close (the
        /// message that ends a player's session, say) still go out first; <see cref="Poll"/> keeps running until the
        /// links have gone. Attempts still in their handshake are abandoned.
        /// </summary>
        internal void CloseAll()
        {
            foreach (var pair in pairs) pair.Link.Close();
            foreach (var attempt in pending) attempt.Connection.Abort();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var pair in pairs) pair.Link.Publish(EngineLinkState.Closed);
            foreach (var attempt in pending) attempt.Attempt.Fail(TransportCloseReason.Local);
            pairs.Clear();
            pending.Clear();
            Endpoint.Dispose();
        }

        private void AcceptConnections()
        {
            while (Endpoint.TryAccept(out var connection))
            {
                // One address holds no more of a host's slots than a household of players needs, so a peer that opens
                // many connections cannot lock everyone else out.
                var source = sourceOf(connection.Path);
                if (source != null && LinksFrom(source) >= maximumLinksPerSource)
                {
                    RefusedForSource++;
                    connection.Close();
                    continue;
                }

                accepted.Enqueue(Attach(connection, source));
            }
        }

        private int LinksFrom(string source)
        {
            var count = 0;
            foreach (var pair in pairs)
            {
                if (pair.Source == source) count++;
            }

            return count;
        }

        private void CompleteAttempts()
        {
            for (var index = pending.Count - 1; index >= 0; index--)
            {
                var item = pending[index];
                if (item.Connection.State == TransportConnectionState.Established)
                {
                    pending.RemoveAt(index);
                    item.Attempt.Succeed(Attach(item.Connection));
                }
                else if (item.Connection.State == TransportConnectionState.Closed || item.Connection.State == TransportConnectionState.Draining)
                {
                    pending.RemoveAt(index);
                    item.Attempt.Fail(item.Connection.CloseReason);
                }
            }
        }

        private NetworkLink Attach(TransportConnection connection, string? source = null)
        {
            var remote = connection.RemoteStaticKey;
            var link = new NetworkLink(connection.Number, remote, playerKeys?.Invoke(remote) ?? default, connection.HandshakeHash);
            link.Publish(EngineLinkState.Open);
            pairs.Add(new Pair(connection, link, source));
            return link;
        }

        private void PumpLinks()
        {
            for (var index = pairs.Count - 1; index >= 0; index--)
            {
                var pair = pairs[index];
                var connection = pair.Connection;
                var link = pair.Link;

                // The close flag is read before the queue is drained: everything the main thread sent before asking to
                // close is then already queued, so a graceful close delivers it.
                var closeRequested = link.CloseRequested;
                while (connection.State == TransportConnectionState.Established && link.TryTakeOutbound(out var channel, out var frame))
                {
                    // An oversized datagram is the engine's bug and goes nowhere; a backlog past its cap has already
                    // closed the connection, which the state below reports.
                    connection.Send(channel, frame, 0, frame.Length);
                }

                if (closeRequested && connection.State == TransportConnectionState.Established) connection.Close();

                // Session frames leave the transport only while the main thread has room, so the peer's flow control,
                // not this process's memory, absorbs a stall; a closing connection still takes the peer's data, so it is
                // held back too. Once the connection drains nothing more can arrive, so the rest moves at once, and the
                // link reports the close only after the engine has read it all.
                var ending = connection.State == TransportConnectionState.Draining || connection.State == TransportConnectionState.Closed;
                while ((ending || link.HasInboundRoom) && connection.TryReceive(out var kind, out var received))
                {
                    if (kind == EngineChannel.Session) link.DeliverSession(received);
                    else link.DeliverDatagram(kind, received);
                }

                if (connection.State == TransportConnectionState.Established)
                {
                    link.Publish(connection.IsStalled ? EngineLinkState.Stalled : EngineLinkState.Open);
                }
                else if (connection.State == TransportConnectionState.Draining || connection.State == TransportConnectionState.Closed)
                {
                    // A closing connection stays published as it was until the transport has delivered what was sent.
                    link.Publish(EngineLinkState.Closed);
                    pairs.RemoveAt(index);
                }
            }
        }

        private readonly struct Pair
        {
            internal Pair(TransportConnection connection, NetworkLink link, string? source)
            {
                Connection = connection;
                Link = link;
                Source = source;
            }

            internal TransportConnection Connection { get; }

            internal NetworkLink Link { get; }

            /// <summary>Gets the source an inbound link came from, or null for an outbound link or an unknown source.</summary>
            internal string? Source { get; }
        }

        private readonly struct PendingConnect
        {
            internal PendingConnect(TransportConnection connection, ConnectAttempt attempt)
            {
                Connection = connection;
                Attempt = attempt;
            }

            internal TransportConnection Connection { get; }

            internal ConnectAttempt Attempt { get; }
        }
    }
}
