extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using static TopiaForge.Mods.Multiplayer.Tests.TransportFrames;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Seeded hostile input against live endpoints, and the guarantee that a seeded run repeats every byte.</summary>
internal static class TransportFuzzTests
{
    private static readonly byte[] TransportTypes =
    {
        PacketType.Initiation, PacketType.Retry, PacketType.Response, PacketType.Completion, PacketType.InfoQuery,
        PacketType.InfoReply, PacketType.Protected
    };

    internal static void HostileDatagramsNeverDisturbASession()
    {
        var pair = new TransportPair(50);
        var captured = new List<byte[]>();
        pair.Network.Observe = (_, _, bytes) =>
        {
            if (captured.Count < 256) captured.Add(bytes);
        };
        pair.Host.SetInfo(new byte[] { 1, 2, 3 });
        var (client, host) = pair.Establish();
        Exchange(pair, client, host, 1);
        var random = new Random(50);
        var attacker = pair.Network.AddRaw("attacker");
        var sources = new[] { attacker, pair.ClientNode, pair.HostNode };
        var kinds = new[] { TransportPathKind.Direct, TransportPathKind.Relayed, TransportPathKind.Lan };
        for (var round = 0; round < 4_000; round++)
        {
            var source = sources[random.Next(sources.Length)];
            var target = source == pair.HostNode ? pair.ClientNode : random.Next(2) == 0 ? pair.HostNode : pair.ClientNode;
            pair.Network.Inject(source, target, Hostile(random, captured), kinds[random.Next(kinds.Length)]);
            if (round % 200 == 0) pair.Client.QueryInfo(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now);
            if (round % 40 == 0) pair.Network.RunFor(10_000);
        }

        That(host.Path.Equals(VirtualDatagramNetwork.PathTo(pair.ClientNode)) && client.Path.Equals(VirtualDatagramNetwork.PathTo(pair.HostNode)),
            "no hostile datagram moved either side off its path");

        // Oversized datagrams never reach a decoder, even ones addressed to a live connection.
        var dropped = pair.Host.Statistics.DatagramsDropped;
        foreach (var size in new[] { 1_201, 1_500, 65_000 })
        {
            var oversized = Bytes(random, size);
            oversized[0] = PacketType.Protected;
            BitConverter.GetBytes(host.LocalConnectionId).CopyTo(oversized, 1);
            pair.Host.Receive(VirtualDatagramNetwork.PathTo(pair.ClientNode), oversized, 0, oversized.Length, pair.Network.Now);
        }

        That(pair.Host.Statistics.DatagramsDropped == dropped + 3, "datagrams over 1,200 bytes are dropped unread");
        Throws<ArgumentOutOfRangeException>(
            () => pair.Host.Receive(VirtualDatagramNetwork.PathTo(pair.ClientNode), new byte[10], 5, 6, pair.Network.Now),
            "a range past the buffer is a caller error");
        pair.Network.RunFor(6_000_000);
        That(client.State == TransportConnectionState.Established && host.State == TransportConnectionState.Established,
            "four thousand hostile datagrams leave the connection standing");
        That(pair.Host.HalfOpenCount == 0 && pair.Host.ConnectionCount == 1 && pair.Client.ConnectionCount == 1,
            "half-open handshakes they started have expired");
        Exchange(pair, client, host, 2);
    }

    internal static void SeededRunsRepeatEveryByte()
    {
        var first = Scenario(60);
        var second = Scenario(60);
        var other = Scenario(61);
        That(first == second, "the same seeds give the same datagrams at the same times");
        That(first != other, "another seed gives another run");
    }

    private static string Scenario(int seed)
    {
        var pair = new TransportPair(seed);
        var conditions = pair.Network.Conditions;
        conditions.LatencyMicros = 20_000;
        conditions.JitterMicros = 15_000;
        conditions.LossRate = 0.1;
        conditions.DuplicateRate = 0.05;
        var (client, host) = pair.Establish(maximumMicros: 30_000_000);
        client.Send(ProviderChannel.Realtime, new byte[] { 1, 2, 3 }, 0, 3);
        Exchange(pair, client, host, seed, 30_000);
        pair.Network.RunFor(2_000_000);
        return pair.Network.TranscriptHash() + "@" + pair.Network.Now;
    }

    /// <summary>Sends a frame each way and waits for both to arrive intact.</summary>
    private static void Exchange(TransportPair pair, TransportConnection client, TransportConnection host, int seed, int size = 5_000)
    {
        var up = Pattern(size, seed);
        var down = Pattern(size / 2, seed + 1);
        client.Send(ProviderChannel.Session, up, 0, up.Length);
        host.Send(ProviderChannel.Session, down, 0, down.Length);
        var atHost = new List<byte[]>();
        var atClient = new List<byte[]>();
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, atHost);
            Drain(client, atClient);
            return atHost.Count == 1 && atClient.Count == 1;
        }, 30_000_000), "a frame crosses each way");
        That(atHost[0].SequenceEqual(up) && atClient[0].SequenceEqual(down), "intact");
    }

    /// <summary>Random bytes, a transport type byte on random bytes, or a captured datagram flipped, cut or extended.</summary>
    private static byte[] Hostile(Random random, List<byte[]> captured)
    {
        var kind = random.Next(4);
        if (kind == 0) return Bytes(random, 1 + random.Next(TransportOptions.MaximumDatagramBytes));
        if (kind == 1)
        {
            var typed = Bytes(random, random.Next(2) == 0 ? HandshakePackets.InitiationSize : 1 + random.Next(200));
            typed[0] = TransportTypes[random.Next(TransportTypes.Length)];
            return typed;
        }

        var datagram = (byte[])captured[random.Next(captured.Count)].Clone();
        switch (random.Next(3))
        {
            case 0:
                for (var flips = 1 + random.Next(3); flips > 0; flips--) datagram[random.Next(datagram.Length)] ^= (byte)(1 << random.Next(8));
                return datagram;
            case 1:
                return datagram.Take(1 + random.Next(datagram.Length)).ToArray();
            default:
                return datagram.Concat(Bytes(random, 1 + random.Next(TransportOptions.MaximumDatagramBytes - datagram.Length + 1))).Take(1200).ToArray();
        }
    }

    private static byte[] Bytes(Random random, int count)
    {
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }
}
