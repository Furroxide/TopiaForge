extern alias provider;

using System.Net;
using System.Runtime.CompilerServices;
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
        var relayedSide = RelayedSide(520, false);
        var (scenario, side) = (relayedSide.Scenario, relayedSide.Side);
        var (relayed, sent, rounds, wrapped, allocated) = MeasureHook(relayedSide, false);
        That(relayed == 20_000, "every replayed ChannelData came out as a relayed transport datagram (" + relayed + ")");
        That(sent == rounds && wrapped, "and every relayed send went out as ChannelData");
        That(allocated == 0, "the hook allocates nothing in the steady state (" + allocated + " bytes)");

        // Each datagram wakes the network thread, which turns the agent and asks for its next timer: with nothing due,
        // that allocates nothing either.
        var now = scenario.Network.Now;
        allocated = AllocationPasses.Fewest(() => IdleTurns(side, now, 20_000));
        That(allocated == 0, "a turn with nothing due allocates nothing (" + allocated + " bytes)");

        // A route tick that sends nothing (no consent check, keepalive or refresh fell due) allocates nothing. Each pass
        // runs the next 25 route ticks.
        var tick = 0;
        var quietTicks = 0;
        allocated = AllocationPasses.Fewest(() => QuietRouteTicks(side, now, ref tick, 25, out quietTicks));
        That(allocated == 0, "a route tick that sends nothing allocates nothing (" + allocated + " bytes by tick " + tick + ")");
        That(quietTicks >= 20, "most route ticks send nothing (" + quietTicks + " of 25)");
    }

    internal static void TheIndicationFallbackAllocatesNothing()
    {
        // With every channel bind refused, relayed data travels as Send and Data indications, the fallback the design
        // declares. Each Data indication is read where it lies and its peer found by address, so it allocates nothing.
        var relayedSide = RelayedSide(521, true);
        var statistics = relayedSide.Side.Agent.Relay!.Statistics;
        That(statistics.DataIndicationsReceived > 0 && statistics.ChannelDataReceived == 0, "data arrives as Data indications (" + statistics.DataIndicationsReceived + ")");
        var (relayed, sent, rounds, wrapped, allocated) = MeasureHook(relayedSide, true);
        That(relayed == 20_000, "every replayed Data indication came out as a relayed transport datagram (" + relayed + ")");
        That(sent == rounds && wrapped, "and every relayed send went out as a Send indication");
        That(allocated == 0, "the fallback allocates nothing in the steady state either (" + allocated + " bytes)");
    }

    /// <summary>A session that stays on the relay, the side whose connection leaves through its own allocation, and that side's router.</summary>
    private static (NatScenario Scenario, NatPeerNode Side, IPAddress Router, NetworkLink JoinerLink, NetworkLink HostLink) RelayedSide(int seed, bool refuseChannels)
    {
        var (scenario, host, joiner, joinerLink, hostLink) = NatMatrixTests.Session(seed, NatBehaviour.Symmetric, NatBehaviour.Symmetric,
            configure: setup => setup.Turn.RefuseChannelBinds = refuseChannels);
        That(scenario.Exchange(joinerLink, hostLink, 1), "frames flow");
        scenario.Network.RunFor(1_000_000);
        That(scenario.Exchange(joinerLink, hostLink, 2), "frames keep flowing");
        var side = new[] { host, joiner }.Single(peer => peer.Agent.Sessions.Any(session => session.Current != null && session.Current.Local.IsRelay));
        var name = ReferenceEquals(side, host) ? "host router" : "joiner router";
        var router = scenario.Router.Internet.Nats.Values.First(nat => nat.Name == name).PublicAddress;
        return (scenario, side, router, joinerLink, hostLink);
    }

    /// <summary>
    /// Captures one relayed transport datagram on its way from the TURN server to the measured side, as ChannelData or as
    /// a Data indication, then replays it through the socket hook 20,000 times a pass while sending as often, and
    /// measures. Relayed is the last pass's count; Sent and Rounds count every pass.
    /// </summary>
    private static (int Relayed, int Sent, int Rounds, bool Wrapped, long Allocated) MeasureHook(
        (NatScenario Scenario, NatPeerNode Side, IPAddress Router, NetworkLink JoinerLink, NetworkLink HostLink) relayedSide, bool indications)
    {
        var (scenario, side, router, joinerLink, hostLink) = relayedSide;
        byte[]? captured = null;
        scenario.Network.ObservePath = (from, to, bytes) =>
        {
            if (captured != null || from != scenario.TurnNode || !UdpPaths.TryGetEndPoint(to, out var target) || !NatAddresses.SameAddress(target.Address, router)) return;

            // A copy: the receiving agent unwraps the delivered array in place.
            var wanted = indications ? CarriesProtected(bytes) : bytes.Length > 40 && bytes[0] >= 0x40 && bytes[0] <= 0x4F && bytes[4] == PacketType.Protected;
            if (wanted) captured = (byte[])bytes.Clone();
        };
        That(scenario.Exchange(joinerLink, hostLink, 3), "frames keep flowing while the wire is watched");
        scenario.Network.RunFor(3_000_000);
        scenario.Network.ObservePath = null;
        That(captured != null, "a relayed transport datagram was seen on the wire");
        var server = side.Agent.Relay!.Server;
        var relayedPath = side.Agent.Sessions.Single(session => session.Current != null).Current!.Path;
        var inner = new LoopingSocket(captured!, server);
        var socket = new NatDatagramSocket(inner, side.Agent, () => scenario.Network.Now);
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];
        var payload = new byte[600];
        payload[0] = PacketType.Protected;
        var relayed = 0;
        var rounds = 0;
        var allocated = AllocationPasses.Fewest(() =>
        {
            rounds += 20_000;
            return Replay(socket, buffer, relayedPath, payload, 20_000, out relayed);
        });
        return (relayed, inner.Sent, rounds, indications ? inner.LastSentIndication : inner.LastSentChannel, allocated);
    }

    /// <summary>
    /// Replays the captured datagram through the socket hook <paramref name="rounds"/> times, sending as often, and returns
    /// what this thread allocated meanwhile; <paramref name="relayed"/> counts what came out as relayed datagrams.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Replay(NatDatagramSocket socket, byte[] buffer, TransportPath relayedPath, byte[] payload, int rounds, out int relayed)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var round = 0; round < rounds; round++) count += Cycle(socket, buffer, relayedPath, payload) ? 1 : 0;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        relayed = count;
        return allocated;
    }

    /// <summary>
    /// Turns an agent with nothing due and asks for its next timer, <paramref name="rounds"/> times, and returns what this
    /// thread allocated meanwhile.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long IdleTurns(NatPeerNode side, long now, int rounds)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < rounds; round++)
        {
            side.Agent.Turn(side.Stack, now);
            side.Agent.NextTimeoutMicros();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// Turns the agent at the next <paramref name="ticks"/> route ticks after <paramref name="tick"/>, dropping what each
    /// sends, and returns what this thread allocated in the turns that sent nothing; <paramref name="quiet"/> counts them.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long QuietRouteTicks(NatPeerNode side, long start, ref int tick, int ticks, out int quiet)
    {
        var allocated = 0L;
        var silent = 0;
        for (var end = tick + ticks; tick < end;)
        {
            tick++;
            var before = GC.GetAllocatedBytesForCurrentThread();
            side.Agent.Turn(side.Stack, start + (tick * NatSession.RouteIntervalMicros));
            var turn = GC.GetAllocatedBytesForCurrentThread() - before;
            var any = false;
            while (side.Agent.TryDequeueDatagram(out var datagram))
            {
                any = true;
                side.Agent.Release(datagram);
            }

            if (any) continue;
            silent++;
            allocated += turn;
        }

        quiet = silent;
        return allocated;
    }

    /// <summary>Gets whether a datagram is a Data indication carrying a protected transport packet.</summary>
    private static bool CarriesProtected(byte[] bytes)
    {
        var address = new byte[16];
        return StunMessage.TryReadDataIndication(bytes, 0, bytes.Length, address, out _, out _, out var offset, out var length) && length > 20 && bytes[offset] == PacketType.Protected;
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

        internal bool LastSentIndication { get; private set; }

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
            LastSentIndication = buffer[offset] == 0x00 && buffer[offset + 1] == 0x16 && to.Equals(path);
        }

        public void Dispose()
        {
        }
    }
}
