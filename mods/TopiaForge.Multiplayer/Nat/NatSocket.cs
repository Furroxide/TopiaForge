using System;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Stun;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>
    /// The game socket as the network thread sees it once the NAT layer shares it: the one hook the thread needs, as a
    /// socket around the socket rather than a change to the thread. Each read takes one datagram and splits it by first
    /// byte; STUN and ChannelData go to the agent, whose unwrapped relay traffic comes back as a datagram from a relayed
    /// path, and the thread counts everything the agent consumed against its turn like any dropped datagram. A send to a
    /// relayed path is wrapped for the TURN server. Neither direction allocates once a peer's channel is bound.
    /// </summary>
    internal sealed class NatDatagramSocket : IDatagramSocket
    {
        /// <summary>Room for a Data indication carrying a full transport datagram, with margin.</summary>
        internal const int ReceiveBytes = 2048;

        private readonly IDatagramSocket inner;
        private readonly NatAgent agent;
        private readonly Func<long> clock;
        private readonly byte[] scratch = new byte[ReceiveBytes];
        private readonly byte[] wire = new byte[StunMessage.MaximumBytes];

        internal NatDatagramSocket(IDatagramSocket inner, NatAgent agent, Func<long> clock)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public int LocalPort => inner.LocalPort;

        /// <summary>Gets how many datagrams were consumed or dropped here rather than handed to the transport.</summary>
        internal long Diverted { get; private set; }

        public bool WaitReadable(long timeoutMicros) => inner.WaitReadable(timeoutMicros);

        public bool TryReceive(byte[] buffer, out int count, out TransportPath? path)
        {
            count = 0;
            path = null;
            if (buffer == null || !inner.TryReceive(scratch, out var received, out var from)) return false;
            if (from == null) return true;
            switch (agent.Receive(from, scratch, 0, received, clock(), out var relayed, out var offset, out var length))
            {
                case NatReceiveResult.Transport:
                    if (received > buffer.Length) break;
                    Buffer.BlockCopy(scratch, 0, buffer, 0, received);
                    count = received;
                    path = from;
                    return true;
                case NatReceiveResult.Relayed:
                    if (length > buffer.Length) break;
                    Buffer.BlockCopy(scratch, offset, buffer, 0, length);
                    count = length;
                    path = relayed;
                    return true;
            }

            Diverted++;
            return true;
        }

        public void Send(TransportPath path, byte[] buffer, int offset, int count)
        {
            if (!TurnRelayPath.IsRelayed(path))
            {
                inner.Send(path, buffer, offset, count);
                return;
            }

            var length = agent.Wrap(path, buffer, offset, count, wire, clock(), out var server);
            if (length > 0) inner.Send(server, wire, 0, length);
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// Runs the agent on the network thread: its turn, then whatever it queued goes straight out of the raw socket. When
    /// the thread stops it disposes services before the socket, so the agent's goodbyes and relay releases still leave.
    /// </summary>
    internal sealed class NatNetworkService : INetworkService
    {
        private readonly NatAgent agent;
        private readonly IDatagramSocket socket;

        internal NatNetworkService(NatAgent agent, IDatagramSocket socket)
        {
            this.agent = agent ?? throw new ArgumentNullException(nameof(agent));
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        }

        public void Turn(NetworkStack stack, long now)
        {
            agent.Turn(stack, now);
            Flush();
        }

        public bool TryClaim(TransportInfoReply reply, long now) => false;

        public long NextTimeoutMicros() => agent.NextTimeoutMicros();

        public void Dispose()
        {
            agent.Dispose();
            Flush();
        }

        private void Flush()
        {
            while (agent.TryDequeueDatagram(out var datagram))
            {
                try
                {
                    socket.Send(datagram.Path, datagram.Buffer, 0, datagram.Length);
                }
                finally
                {
                    agent.Release(datagram);
                }
            }
        }
    }

    /// <summary>
    /// The NAT layer for one game socket, assembled: the agent, the socket the network thread is built on, and the
    /// service it attaches. Building it contacts nothing; only a session started with a control-plane plan does.
    /// </summary>
    internal sealed class NatPlumbing
    {
        private NatPlumbing(NatAgent agent, IDatagramSocket socket, INetworkService service)
        {
            Agent = agent;
            Socket = socket;
            Service = service;
        }

        internal NatAgent Agent { get; }

        /// <summary>Gets the socket to build the <see cref="NetworkThread"/> on.</summary>
        internal IDatagramSocket Socket { get; }

        /// <summary>Gets the service to attach to that thread.</summary>
        internal INetworkService Service { get; }

        internal static NatPlumbing Wrap(IDatagramSocket socket, NatAgentOptions options, Func<long> clock)
        {
            if (socket == null) throw new ArgumentNullException(nameof(socket));
            var agent = new NatAgent(options);
            return new NatPlumbing(agent, new NatDatagramSocket(socket, agent, clock), new NatNetworkService(agent, socket));
        }
    }
}
