extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The shared socket: the first-byte split, the socket hook around the game socket, and its allocation-free steady state.</summary>
internal static class NatDemuxTests
{
    internal static void TheFirstByteSplitIsUnambiguous()
    {
        for (var value = 0; value < 256; value++)
        {
            var first = (byte)value;
            var kind = NatDemux.Classify(first);
            That((kind == NatDatagramKind.Stun) == (value <= 3), "STUN is exactly 0 to 3 (" + value + ")");
            That((kind == NatDatagramKind.ChannelData) == (value >= 64 && value <= 79), "ChannelData is exactly 64 to 79 (" + value + ")");
            That(NatDemux.AgreesWithTransport(first), "the transport's own split agrees at " + value);
        }

        var transport = new[] { PacketType.Initiation, PacketType.Retry, PacketType.Response, PacketType.Completion, PacketType.InfoQuery, PacketType.InfoReply, PacketType.Protected, LanBeacon.PacketType };
        That(transport.All(type => NatDemux.Classify(type) == NatDatagramKind.Transport && !PacketType.IsForeign(type)), "every transport and beacon type stays with the transport");

        // Everything this layer writes lands in its own band.
        var writer = new StunMessageWriter();
        var buffer = new byte[StunMessage.MaximumBytes];
        foreach (var method in new[] { StunMethod.Binding, StunMethod.Allocate, StunMethod.Refresh, StunMethod.Send, StunMethod.Data, StunMethod.CreatePermission, StunMethod.ChannelBind })
        {
            foreach (var messageClass in new[] { StunClass.Request, StunClass.Indication, StunClass.Success, StunClass.Error })
            {
                writer.Begin(buffer, method, messageClass, StunTransactionId.Read(new byte[12], 0));
                That(buffer[0] <= 1 && NatDemux.Classify(buffer[0]) == NatDatagramKind.Stun, "every STUN message starts with 0 or 1");
            }
        }

        for (var channel = ChannelData.FirstChannel; channel <= ChannelData.LastChannel; channel++)
        {
            ChannelData.Write(buffer, channel, buffer, 100, 4);
            That(NatDemux.Classify(buffer[0]) == NatDatagramKind.ChannelData, "every channel number starts with 64 to 79");
        }

        That(!ChannelData.IsChannel(0x3FFF) && !ChannelData.IsChannel(0x5000) && !ChannelData.IsChannel(0x7FFF), "numbers outside 0x4000 to 0x4FFF are never used");

        // And every datagram a live transport session sends stays with the transport.
        var pair = new TransportPair(500);
        var foreign = 0;
        pair.Network.Observe = (_, _, bytes) => foreign += NatDemux.Classify(bytes[0]) == NatDatagramKind.Transport ? 0 : 1;
        var (client, host) = pair.Establish();
        client.Send(provider::TopiaForge.Multiplayer.Engine.EngineChannel.Session, new byte[3000], 0, 3000);
        pair.Network.RunFor(2_000_000);
        That(foreign == 0 && pair.Network.Sent > 10, "a session's own datagrams never look like STUN or ChannelData");
    }

    internal static void TheSocketHookDivertsOnlyNatTraffic()
    {
        var (scenario, host, _, _, _) = NatMatrixTests.Session(510, NatBehaviour.Symmetric, NatBehaviour.Symmetric);
        var inner = new NatPrivacyTests.RecordingSocket();
        var socket = new NatDatagramSocket(inner, host.Agent, () => scenario.Network.Now);
        var stranger = UdpPaths.For(new IPEndPoint(IPAddress.Parse("198.51.100.99"), 5000));
        var request = new byte[20];
        new StunMessageWriter().Begin(request, StunMethod.Binding, StunClass.Request, StunTransactionId.Read(new byte[12], 0));
        inner.Arrivals.Enqueue((new byte[] { 0xE8, 1, 2, 3 }, stranger));
        inner.Arrivals.Enqueue((request, stranger));
        inner.Arrivals.Enqueue((new byte[] { 0x40, 0x00, 0x00, 0x01, 0xE8 }, stranger));
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];
        That(socket.TryReceive(buffer, out var count, out var path) && path!.Equals(stranger) && count == 4, "a transport datagram passes untouched");
        That(socket.TryReceive(buffer, out _, out path) && path == null, "an unauthenticated check is consumed and answered with nothing");
        That(socket.TryReceive(buffer, out _, out path) && path == null, "ChannelData from anyone but the relay server is dropped");
        That(!socket.TryReceive(buffer, out _, out _), "and the hook reads no further than the socket");
        That(socket.Diverted == 2 && inner.Sent.Count == 0, "two were diverted, and nothing was sent in reply");
        socket.Send(stranger, new byte[] { 0xE8 }, 0, 1);
        That(inner.Sent.Count == 1 && inner.Sent[0].Path.Equals(stranger), "a send to a UDP path goes straight out");
        socket.Send(TurnRelayPath.For(99, new IPEndPoint(IPAddress.Parse("198.51.100.99"), 5000)), new byte[] { 0xE8 }, 0, 1);
        That(inner.Sent.Count == 1, "a send through an allocation that does not exist goes nowhere");
    }

    internal static void TheRelayedSteadyStateAllocatesNothing()
    {
        // A session that stays on the relay. Whichever side's connection leaves through its own allocation receives the
        // other's transport datagrams as ChannelData from its TURN server: that side's hook is the one measured.
        var (scenario, host, joiner, joinerLink, hostLink) = NatMatrixTests.Session(520, NatBehaviour.Symmetric, NatBehaviour.Symmetric);
        That(scenario.Exchange(joinerLink, hostLink, 1), "frames flow");
        scenario.Network.RunFor(1_000_000);
        var side = new[] { host, joiner }.Single(peer => peer.Agent.Sessions.Any(session => session.Current != null && session.Current.Local.IsRelay));
        var router = scenario.Router.Internet.Nats.Values.First(nat => nat.Name == (ReferenceEquals(side, host) ? "host router" : "joiner router")).PublicAddress;
        byte[]? captured = null;
        scenario.Network.ObservePath = (from, to, bytes) =>
        {
            if (captured != null || from != scenario.TurnNode || !UdpPaths.TryGetEndPoint(to, out var target) || !NatAddresses.SameAddress(target.Address, router)) return;
            // A copy: the receiving agent unwraps the delivered array in place.
            if (bytes.Length > 40 && bytes[0] >= 0x40 && bytes[0] <= 0x4F && bytes[4] == PacketType.Protected) captured = (byte[])bytes.Clone();
        };
        That(scenario.Exchange(joinerLink, hostLink, 2), "frames keep flowing");
        scenario.Network.RunFor(3_000_000);
        scenario.Network.ObservePath = null;
        That(captured != null, "a relayed transport datagram was seen on the wire as ChannelData");
        var server = side.Agent.Relay!.Server;
        var relayedPath = side.Agent.Sessions.Single(session => session.Current != null).Current!.Path;
        var inner = new LoopingSocket(captured!, server);
        var socket = new NatDatagramSocket(inner, side.Agent, () => scenario.Network.Now);
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];
        var payload = new byte[600];
        payload[0] = PacketType.Protected;
        for (var warm = 0; warm < 200; warm++) Cycle(socket, buffer, relayedPath, payload);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var relayed = 0;
        for (var round = 0; round < 20_000; round++) relayed += Cycle(socket, buffer, relayedPath, payload) ? 1 : 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        That(relayed == 20_000, "every replayed ChannelData came out as a relayed transport datagram (" + relayed + ")");
        That(inner.Sent == 20_200 && inner.LastSentChannel, "and every relayed send went out as ChannelData");
        That(allocated == 0, "the hook allocates nothing in the steady state (" + allocated + " bytes)");

        // Each datagram wakes the network thread, which turns the agent and asks for its next timer: with nothing due,
        // that allocates nothing either.
        var now = scenario.Network.Now;
        for (var warm = 0; warm < 10; warm++) side.Agent.Turn(side.Stack, now);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 20_000; round++)
        {
            side.Agent.Turn(side.Stack, now);
            side.Agent.NextTimeoutMicros();
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        That(allocated == 0, "a turn with nothing due allocates nothing (" + allocated + " bytes)");

        // A route tick that sends nothing (no consent check, keepalive or refresh fell due) allocates nothing.
        var quietTicks = 0;
        for (var tick = 1; tick <= 25; tick++)
        {
            var at = now + tick * NatSession.RouteIntervalMicros;
            before = GC.GetAllocatedBytesForCurrentThread();
            side.Agent.Turn(side.Stack, at);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var sent = false;
            while (side.Agent.TryDequeueDatagram(out var datagram))
            {
                sent = true;
                side.Agent.Release(datagram);
            }

            if (sent) continue;
            quietTicks++;
            That(allocated == 0, "a route tick that sends nothing allocates nothing (" + allocated + " bytes at tick " + tick + ")");
        }

        That(quietTicks >= 20, "most route ticks send nothing (" + quietTicks + " of 25)");
    }

    private static bool Cycle(NatDatagramSocket socket, byte[] buffer, TransportPath relayedPath, byte[] payload)
    {
        var received = socket.TryReceive(buffer, out _, out var path) && path != null && TurnRelayPath.IsRelayed(path);
        socket.Send(relayedPath, payload, 0, payload.Length);
        return received;
    }

    /// <summary>A socket that hands over the same datagram from the same path forever and swallows sends, allocating nothing.</summary>
    private sealed class LoopingSocket : IDatagramSocket
    {
        private readonly byte[] datagram;
        private readonly TransportPath path;

        internal LoopingSocket(byte[] datagram, TransportPath path)
        {
            this.datagram = datagram;
            this.path = path;
        }

        internal int Sent { get; private set; }

        internal bool LastSentChannel { get; private set; }

        public int LocalPort => 27250;

        public bool WaitReadable(long timeoutMicros) => true;

        public bool TryReceive(byte[] buffer, out int count, out TransportPath? from)
        {
            Buffer.BlockCopy(datagram, 0, buffer, 0, datagram.Length);
            count = datagram.Length;
            from = path;
            return true;
        }

        public void Send(TransportPath to, byte[] buffer, int offset, int count)
        {
            Sent++;
            LastSentChannel = buffer[offset] >= 0x40 && buffer[offset] <= 0x4F && to.Equals(path);
        }

        public void Dispose()
        {
        }
    }
}
