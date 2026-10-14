extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using static TopiaForge.Mods.Multiplayer.Tests.TransportFrames;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Seeded fuzzing of the transport beyond the first pass: every kind of datagram the transport sends, captured, mutated
/// and readdressed to live connections, and authenticated packets whose frames are garbage. Nothing throws out of an
/// endpoint, no reply to unauthenticated input outweighs it, a live session stays on its path and keeps delivering,
/// every table stays within its bound, and a seed repeats every byte.
/// </summary>
internal static class TransportDeepFuzzTests
{
    internal static void EveryKindOfDatagramMutatedLeavesASessionStanding()
    {
        var first = Mutated(170);
        That(first == Mutated(170), "a seed repeats the fuzzing byte for byte");
    }

    internal static void AuthenticatedGarbageEndsOnlyItsOwnConnection()
    {
        var fuzz = new FuzzMutator(172);
        var pair = new TransportPair(172);
        var (client, host) = pair.Establish();
        var ended = 0;
        using var run = FuzzLedger.Start("authenticated garbage frames");
        for (var round = 0; round < FuzzMutator.Iterations(200); round++)
        {
            var raw = new RawTransportPeer(pair.Network, "raw-" + round, 2_000 + round);
            raw.Connect(pair.HostNode);
            TransportConnection? accepted = null;
            That(pair.Network.RunUntil(() => pair.Host.TryAccept(out accepted), 2_000_000), "a hand-made handshake completes");
            for (var packet = 1 + fuzz.Random.Next(30); packet > 0 && accepted!.State == TransportConnectionState.Established; packet--)
            {
                raw.Send(pair.HostNode, writer => Garbage(fuzz, writer));
                run.Inputs++;
                if (fuzz.Chance(30)) pair.Network.RunFor(1 + fuzz.Random.Next(20_000));
            }

            pair.Network.RunFor(100_000);
            var connection = accepted!;
            if (connection.State == TransportConnectionState.Established)
            {
                raw.Send(pair.HostNode, writer => FrameWriter.WriteClose(writer, CloseCode.Normal));
            }
            else
            {
                ended++;
                That(connection.CloseReason == TransportCloseReason.ProtocolViolation || connection.CloseReason == TransportCloseReason.Peer,
                    "garbage ends its own connection as a protocol violation, or as the close it carried (" + connection.CloseReason + ")");
            }

            if (round % 10 == 9)
            {
                pair.Network.RunFor(3_000_000);
                var census = pair.Host.Census();
                That(census.Connections <= 3 && census.HalfOpen == 0 && census.ReassemblyBytes <= 2 * 1024 * 1024,
                    "closed garbage leaves nothing behind (" + census + ")");
                That(client.State == TransportConnectionState.Established && host.State == TransportConnectionState.Established,
                    "and the session beside it stands");
                Exchange(pair, client, host, round);
            }
        }

        That(ended > 0 && pair.Network.Spins == 0, "the garbage reached the frame reader (" + ended + " connections ended) without a busy loop");
    }

    /// <summary>Captures every kind of datagram, then fires mutations of it at a live session from every kind of path.</summary>
    private static string Mutated(int seed)
    {
        var fuzz = new FuzzMutator(seed);
        var corpus = Capture(seed + 1000);
        var pair = new TransportPair(seed);
        pair.Network.Observe = (_, _, bytes) =>
        {
            if (corpus.Count < 1_024) corpus.Add(bytes);
        };
        pair.Host.SetInfo(new byte[] { 1, 2, 3 });
        var (client, host) = pair.Establish();
        Exchange(pair, client, host, 1);
        var attacker = pair.Network.AddRaw("attacker");
        long fromAttacker = 0;
        long toAttacker = 0;
        pair.Network.Observe = (_, to, bytes) =>
        {
            if (to == attacker) toAttacker += bytes.Length;
        };
        var kinds = new[] { TransportPathKind.Direct, TransportPathKind.Relayed, TransportPathKind.Lan };
        var liveIds = new[] { client.LocalConnectionId, host.LocalConnectionId };
        using var run = FuzzLedger.Start("transport datagrams");
        for (var round = 0; round < FuzzMutator.Iterations(50_000); round++, run.Inputs++)
        {
            var datagram = fuzz.Chance(5) ? fuzz.Bytes(1 + fuzz.Random.Next(1_400)) : fuzz.Mutate(fuzz.Pick(corpus), 1_400);
            if (datagram.Length > 9 && fuzz.Chance(40))
            {
                // Readdressed to a live connection, so it reaches the connection's own checks rather than the routing.
                BitConverter.GetBytes(fuzz.Pick(liveIds)).CopyTo(datagram, 1);
            }

            var spoofed = fuzz.Chance(30);
            var source = spoofed ? (fuzz.Chance(50) ? pair.ClientNode : pair.HostNode) : attacker;
            var target = source == pair.HostNode ? pair.ClientNode : fuzz.Chance(70) ? pair.HostNode : pair.ClientNode;
            if (source == attacker) fromAttacker += Math.Min(datagram.Length, TransportOptions.MaximumDatagramBytes);
            if (datagram.Length <= TransportOptions.MaximumDatagramBytes) pair.Network.Inject(source, target, datagram, fuzz.Pick(kinds));
            else pair.Host.Receive(VirtualDatagramNetwork.PathTo(attacker), datagram, 0, datagram.Length, pair.Network.Now);
            if (round % 500 == 0) pair.Client.QueryInfo(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now);
            if (round % 50 == 0) pair.Network.RunFor(10_000);
            if (round % 2_000 == 1_999) Bounded(pair);
        }

        pair.Network.RunFor(6_000_000);
        Bounded(pair);
        That(toAttacker <= fromAttacker, "no endpoint sent the attacker more than it received from it (" + toAttacker + " of " + fromAttacker + " bytes)");
        That(host.Path.Equals(VirtualDatagramNetwork.PathTo(pair.ClientNode)) && client.Path.Equals(VirtualDatagramNetwork.PathTo(pair.HostNode)),
            "no datagram moved either side off its path");
        That(client.State == TransportConnectionState.Established && host.State == TransportConnectionState.Established && pair.Host.HalfOpenCount == 0,
            "the session stands and the handshakes the fuzzing started have expired");
        Exchange(pair, client, host, 2);
        That(pair.Network.Spins == 0, "no endpoint spun");
        return pair.Network.TranscriptHash() + " " + pair.Host.Statistics.DatagramsDropped + " " + pair.Client.Statistics.DatagramsDropped;
    }

    /// <summary>Every kind of datagram: a cookie handshake, an info query, frames each way, a path challenge and a close.</summary>
    private static List<byte[]> Capture(int seed)
    {
        var captured = new List<byte[]>();
        var pair = new TransportPair(seed, configureHost: options => options.AlwaysRequireCookie = true);
        pair.Network.Observe = (_, _, bytes) => captured.Add(bytes);
        pair.Host.SetInfo(new byte[] { 4, 5, 6 });
        var (client, host) = pair.Establish();
        pair.Client.QueryInfo(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now);
        client.Send(ProviderChannel.Realtime, new byte[] { 7, 8, 9 }, 0, 3);
        Exchange(pair, client, host, seed);
        pair.ClientNode.Rebind();
        Exchange(pair, client, host, seed + 1);
        client.Close();
        pair.Network.RunFor(2_000_000);
        var kinds = captured.Select(bytes => bytes[0]).Distinct().ToList();
        That(new[] { PacketType.Initiation, PacketType.Retry, PacketType.Response, PacketType.Completion, PacketType.InfoQuery, PacketType.InfoReply, PacketType.Protected }
            .All(kinds.Contains), "the corpus holds every kind of datagram");
        return captured;
    }

    private static void Bounded(TransportPair pair)
    {
        foreach (var endpoint in new[] { pair.Host, pair.Client })
        {
            var census = endpoint.Census();
            // A responder keeps its initiation entry, to answer a repeated Initiation, until its connection closes.
            That(census.HalfOpen <= endpoint.Options.MaximumHalfOpen && census.Initiations <= census.Connections && census.OutstandingQueries <= 64 &&
                 census.QueuedInfoReplies <= 64 && census.OutboundDatagrams <= TransportEndpoint.MaximumQueuedDatagrams &&
                 census.ReassemblyBytes <= 2 * endpoint.Options.ProcessReassemblyBytes && census.Connections <= 1 + census.HalfOpen + 2,
                "the endpoint's tables stay within their bounds (" + census + ")");
        }
    }

    /// <summary>One to six frames, well-formed or not, of every type and none, within the packet.</summary>
    private static void Garbage(FuzzMutator fuzz, PacketWriter writer)
    {
        for (var frames = 1 + fuzz.Random.Next(6); frames > 0; frames--)
        {
            var bytes = new List<byte>();
            void Varint(ulong value) => bytes.AddRange(fuzz.Varint(value, minimal: fuzz.Chance(90)));
            ulong Number() => fuzz.Random.Next(4) switch
            {
                0 => (ulong)fuzz.Random.Next(4),
                1 => (ulong)fuzz.Random.Next(1 << 21),
                2 => (ulong)fuzz.Random.Next(2 * 1024 * 1024, 3 * 1024 * 1024),
                _ => (ulong)fuzz.Random.NextInt64(long.MaxValue) << fuzz.Random.Next(2)
            };

            switch (fuzz.Random.Next(11))
            {
                case 0:
                    bytes.AddRange(new byte[1 + fuzz.Random.Next(16)]);
                    break;
                case 1:
                    bytes.Add(FrameType.Ping);
                    break;
                case 2:
                    bytes.Add(FrameType.Ack);
                    Varint(Number());
                    Varint(Number());
                    var ranges = (ulong)fuzz.Random.Next(36);
                    Varint(ranges);
                    for (var range = 0UL; range <= ranges; range++) Varint(Number());
                    break;
                case 3:
                    bytes.Add(FrameType.Close);
                    Varint((ulong)fuzz.Random.Next(12));
                    break;
                case 4:
                    bytes.Add(FrameType.Stream);
                    Varint(Number());
                    var data = fuzz.Bytes(fuzz.Random.Next(200));
                    Varint(fuzz.Chance(90) ? (ulong)data.Length : Number());
                    bytes.AddRange(data);
                    break;
                case 5:
                    bytes.Add(FrameType.Window);
                    Varint(Number());
                    break;
                case 6:
                    bytes.Add(FrameType.Datagram);
                    bytes.Add((byte)fuzz.Random.Next(5));
                    Varint(Number());
                    var payload = fuzz.Bytes(fuzz.Random.Next(100));
                    Varint(fuzz.Chance(90) ? (ulong)payload.Length : Number());
                    bytes.AddRange(payload);
                    break;
                case 7:
                    bytes.Add(fuzz.Chance(50) ? FrameType.PathChallenge : FrameType.PathResponse);
                    bytes.AddRange(fuzz.Bytes(fuzz.Chance(90) ? FrameType.PathDataSize : fuzz.Random.Next(FrameType.PathDataSize)));
                    break;
                case 8:
                    bytes.Add((byte)fuzz.Pick(new[] { 0x04, 0x0F, 0x12, 0x1F, 0x21, 0x32, 0x40, 0x7F, 0x80, 0xFF }));
                    bytes.AddRange(fuzz.Bytes(fuzz.Random.Next(8)));
                    break;
                default:
                    bytes.AddRange(fuzz.Bytes(1 + fuzz.Random.Next(32)));
                    break;
            }

            if (fuzz.Chance(10) && bytes.Count > 1)
            {
                var cut = 1 + fuzz.Random.Next(bytes.Count - 1);
                bytes.RemoveRange(cut, bytes.Count - cut);
            }

            var fits = Math.Min(bytes.Count, writer.Remaining);
            if (fits == 0) return;
            writer.WriteBytes(bytes.ToArray(), 0, fits);
        }
    }

    /// <summary>Sends a frame each way and waits for both to arrive intact.</summary>
    private static void Exchange(TransportPair pair, TransportConnection client, TransportConnection host, int seed)
    {
        var up = Pattern(3_000, seed);
        var down = Pattern(1_500, seed + 1);
        client.Send(ProviderChannel.Session, up, 0, up.Length);
        host.Send(ProviderChannel.Session, down, 0, down.Length);
        var atHost = new List<byte[]>();
        var atClient = new List<byte[]>();
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, atHost);
            Drain(client, atClient);
            return atHost.Count >= 1 && atClient.Count >= 1;
        }, 30_000_000), "a frame crosses each way");
        That(atHost[^1].SequenceEqual(up) && atClient[^1].SequenceEqual(down), "intact");
    }
}
