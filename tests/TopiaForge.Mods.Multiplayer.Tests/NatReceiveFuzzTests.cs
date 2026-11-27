extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The receive path against hostile and edge-case input: sources that cannot send (port 0, unspecified, multicast,
/// broadcast) and every other kind, and a seeded flood of mutated checks, TURN answers forged for real transactions,
/// Data indications and ChannelData from the relay server, and random bytes. Nothing may throw out of the socket hook,
/// since a faulted network-thread turn counts toward stopping the thread, and the session must carry on afterwards.
/// </summary>
internal static class NatReceiveFuzzTests
{
    private static readonly IPEndPoint Joiner = new(IPAddress.Parse("198.51.100.20"), 40000);

    internal static void HostileInputNeverThrowsOutOfTheReceivePath()
    {
        var (scenario, host, joiner, joinerLink, hostLink) = NatMatrixTests.Session(1100, NatBehaviour.FullCone, NatBehaviour.FullCone);
        var session = host.Agent.Sessions.Single(item => item.State != NatSessionState.Ended && item.State != NatSessionState.Failed);
        var local = session.Checker.Local;
        var remote = session.Checker.Remote!;
        var inner = new NatPrivacyTests.RecordingSocket();
        var socket = new NatDatagramSocket(inner, host.Agent, () => scenario.Network.Now);
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];

        // The proven case first: an authentic check, which the host answers naming the source's address, from port 0.
        var check = Check(local.Ufrag + ":" + remote.Ufrag, local.Key);
        foreach (var source in Unsendable())
        {
            var answered = host.Agent.ChecksAnswered;
            inner.Arrivals.Enqueue((check, source));
            That(socket.TryReceive(buffer, out _, out var path) && path == null, "a check from a source that cannot send is dropped, not thrown");
            That(host.Agent.ChecksAnswered == answered, "and gets no answer");
        }

        var genuine = host.Agent.ChecksAnswered;
        inner.Arrivals.Enqueue((check, UdpPaths.For(Joiner)));
        socket.TryReceive(buffer, out _, out _);
        That(host.Agent.ChecksAnswered == genuine + 1, "while the same check from a real source is answered");
        inner.Arrivals.Enqueue((new byte[] { 0xE8, 1, 2, 3 }, UdpPaths.For(new IPEndPoint(IPAddress.Parse("198.51.100.20"), 0))));
        That(socket.TryReceive(buffer, out _, out var transport) && transport == null, "a transport datagram from port 0 is dropped too");

        // Then a seeded flood, through the socket hook, from every kind of source and the relay server itself.
        var relayServer = host.Agent.Relay!.Server;
        var corpus = Corpus(scenario, host, check, relayServer);
        var sources = Unsendable().Concat(Plausible()).Append(relayServer).Append(relayServer).Append(relayServer).ToList();
        var random = new Random(1100);
        for (var round = 0; round < 20_000; round++)
        {
            var bytes = (byte[])corpus[random.Next(corpus.Count)].Clone();
            var flips = random.Next(0, 4);
            if (round % 7 == 0)
            {
                bytes = new byte[random.Next(1, 1600)];
                random.NextBytes(bytes);
                bytes[0] = (byte)(round % 3 == 0 ? random.Next(0, 4) : round % 3 == 1 ? 0x40 + random.Next(0, 16) : random.Next(256));
            }

            for (var flip = 0; flip < flips && bytes.Length > 0; flip++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            if (bytes.Length > 2048) continue;
            inner.Arrivals.Enqueue((bytes, sources[random.Next(sources.Count)]));
            while (socket.TryReceive(buffer, out _, out _))
            {
            }

            if (round % 500 == 0)
            {
                // Between bursts the agent keeps turning, so what the input started runs its course.
                host.Agent.Turn(host.Stack, scenario.Network.Now);
                while (host.Agent.TryDequeueDatagram(out var queued)) host.Agent.Release(queued);
            }
        }

        // Whatever the flood did to the relay (an on-path 437 can end an allocation), the session carries on.
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 60_000_000), "the host's relay is up again after the flood (" + host.Agent.Status + ")");
        That(scenario.Exchange(joinerLink, hostLink, 9, 15_000_000), "and frames still cross both ways");
    }

    /// <summary>Sources nothing genuine sends from.</summary>
    private static List<TransportPath> Unsendable() => new[]
    {
        new IPEndPoint(IPAddress.Parse("198.51.100.20"), 0),
        new IPEndPoint(IPAddress.Parse("2001:db8::5"), 0),
        new IPEndPoint(IPAddress.Parse("0.0.0.0"), 40000),
        new IPEndPoint(IPAddress.IPv6Any, 40000),
        new IPEndPoint(IPAddress.Parse("224.0.0.1"), 40000),
        new IPEndPoint(IPAddress.Parse("255.255.255.255"), 40000),
        new IPEndPoint(IPAddress.Parse("ff02::1"), 40000),
        new IPEndPoint(IPAddress.Parse("::ffff:0.0.0.0"), 40000)
    }.Select(UdpPaths.For).ToList();

    /// <summary>Sources a datagram can genuinely come from, of every kind, and two keys no socket would ever make.</summary>
    private static List<TransportPath> Plausible() => new[]
    {
        UdpPaths.For(Joiner),
        UdpPaths.For(new IPEndPoint(IPAddress.Parse("198.51.100.20"), 65535)),
        UdpPaths.For(new IPEndPoint(IPAddress.Parse("::ffff:198.51.100.20"), 40000)),
        UdpPaths.For(new IPEndPoint(IPAddress.Parse("2001:db8::5"), 40000)),
        UdpPaths.For(new IPEndPoint(IPAddress.Loopback, 40000)),
        UdpPaths.For(new IPEndPoint(IPAddress.IPv6Loopback, 40000)),
        UdpPaths.For(new IPEndPoint(IPAddress.Parse("fe80::1%3"), 40000)),
        UdpPaths.For(new IPEndPoint(IPAddress.Parse("192.168.1.77"), 27250)),
        TurnRelayPath.For(1, new IPEndPoint(IPAddress.Parse("198.51.100.20"), 0)),
        new TransportPath(new byte[] { 9 }, TransportPathKind.Direct)
    }.ToList();

    /// <summary>
    /// Messages worth mutating: the check, Binding and TURN answers forged for the host's own outstanding transactions
    /// (401s, 438s with the longest nonce, successes without integrity, unknown required attributes), Data indications
    /// naming port 0, IPv6 and garbage families, and ChannelData on bound and unbound channels.
    /// </summary>
    private static List<byte[]> Corpus(NatScenario scenario, NatPeerNode host, byte[] check, TransportPath relayServer)
    {
        var corpus = new List<byte[]> { check };
        host.Agent.Relay!.Permit(IPAddress.Parse("198.51.100.77"), "fuzz", scenario.Network.Now);
        host.Agent.Turn(host.Stack, scenario.Network.Now);
        var requests = new List<StunMessage>();
        while (host.Agent.TryDequeueDatagram(out var queued))
        {
            if (queued.Path.Equals(relayServer) && StunMessage.TryParse(queued.Buffer, 0, queued.Length, out var request) && request.Class == StunClass.Request) requests.Add(request);
            host.Agent.Release(queued);
        }

        That(requests.Count > 0, "the host has a TURN request outstanding to forge answers for");
        var writer = new StunMessageWriter();
        var longNonce = new string('n', StunMessage.MaximumTextBytes);
        foreach (var request in requests)
        {
            foreach (var code in new[] { 300, 400, 401, 403, 420, 437, 438, 441, 442, 486, 500, 508, 699 })
            {
                corpus.Add(Write(writer, request.Method, StunClass.Error, request.TransactionId, message =>
                {
                    message.AddErrorCode(code);
                    message.AddText(StunAttribute.Realm, "relay.example.test");
                    message.AddText(StunAttribute.Nonce, longNonce);
                }));
            }

            corpus.Add(Write(writer, request.Method, StunClass.Success, request.TransactionId, message => message.AddUInt32(StunAttribute.Lifetime, uint.MaxValue)));
            corpus.Add(Write(writer, request.Method, StunClass.Success, request.TransactionId, message => message.AddBytes(0x0033, new byte[] { 1, 2, 3, 4 }, 0, 4)));
        }

        var stunId = StunTransactionId.Read(new byte[12], 0);
        corpus.Add(Write(writer, StunMethod.Binding, StunClass.Success, stunId, message => message.AddAddressBytes(StunAttribute.XorMappedAddress, new byte[] { 0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 }, 9)));
        foreach (var (family, port) in new[] { (1, 0), (2, 40000), (3, 40000), (1, 40000) })
        {
            corpus.Add(Write(writer, StunMethod.Data, StunClass.Indication, stunId, message =>
            {
                var address = family == 2 ? IPAddress.Parse("2001:db8::5").GetAddressBytes() : IPAddress.Parse("198.51.100.20").GetAddressBytes();
                if (family == 3) message.AddBytes(StunAttribute.XorPeerAddress, new byte[] { 0, 3, 0, 1, 1, 2, 3, 4 }, 0, 8);
                else if (port == 0) message.AddBytes(StunAttribute.XorPeerAddress, new byte[] { 0, 1, 0x21, 0x12, 1, 2, 3, 4 }, 0, 8);
                else message.AddAddressBytes(StunAttribute.XorPeerAddress, address, port);
                message.AddBytes(StunAttribute.Data, check, 0, check.Length);
            }));
        }

        foreach (var channel in new ushort[] { 0x4000, 0x4001, 0x4FFF })
        {
            var data = new byte[64];
            data[0] = 0xE8;
            var framed = new byte[ChannelData.HeaderSize + data.Length];
            ChannelData.Write(framed, channel, data, 0, data.Length);
            corpus.Add(framed);
        }

        return corpus;
    }

    private static byte[] Write(StunMessageWriter writer, int method, StunClass messageClass, StunTransactionId id, Action<StunMessageWriter> attributes)
    {
        var buffer = new byte[StunMessage.MaximumBytes];
        writer.Begin(buffer, method, messageClass, id);
        attributes(writer);
        return buffer.AsSpan(0, writer.Finish(null, true)).ToArray();
    }

    private static byte[] Check(string username, byte[] key)
    {
        var writer = new StunMessageWriter();
        var buffer = new byte[256];
        writer.Begin(buffer, StunMethod.Binding, StunClass.Request, StunTransactionId.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, 0));
        writer.AddText(StunAttribute.Username, username);
        writer.AddUInt32(StunAttribute.Priority, IceCandidate.ComputePriority(IceCandidateKind.PeerReflexive, 65535));
        writer.AddUInt64(StunAttribute.IceControlling, 7);
        return buffer.AsSpan(0, writer.Finish(key, true)).ToArray();
    }
}
