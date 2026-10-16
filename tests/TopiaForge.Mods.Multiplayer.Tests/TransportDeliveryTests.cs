extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using ProtocolLimits = provider::TopiaForge.Multiplayer.Engine.ProtocolLimits;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using static TopiaForge.Mods.Multiplayer.Tests.TransportFrames;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The session stream and datagram channels: order, integrity, flow control and the caps that bound memory.</summary>
internal static class TransportDeliveryTests
{
    private static readonly Action<TransportOptions> SmallFrames = options =>
    {
        options.MaximumFrameBytes = 16 * 1024;
        options.StreamWindowBytes = 64 * 1024;
    };

    internal static void SessionFramesArriveWholeAndInOrderThroughLoss()
    {
        var pair = new TransportPair(11);
        var conditions = pair.Network.Conditions;
        conditions.LatencyMicros = 30_000;
        conditions.JitterMicros = 20_000;
        conditions.LossRate = 0.1;
        conditions.DuplicateRate = 0.05;
        var (client, host) = pair.Establish(maximumMicros: 30_000_000);
        var sizes = new[] { 0, 1, 1_000, 1_167, 1_168, 20_000, 70_000, ProtocolLimits.MaximumFrameBytes };
        var upstream = sizes.Select((size, index) => Pattern(size, index)).ToList();
        var downstream = sizes.Take(6).Select((size, index) => Pattern(size, 100 + index)).ToList();
        var start = pair.Network.Now;
        var sentBefore = client.BytesSent;
        foreach (var frame in upstream) That(client.Send(ProviderChannel.Session, frame, 0, frame.Length) == TransportSendResult.Queued, "the client queues a frame");
        foreach (var frame in downstream) That(host.Send(ProviderChannel.Session, frame, 0, frame.Length) == TransportSendResult.Queued, "the host queues a frame");
        var atHost = new List<byte[]>();
        var atClient = new List<byte[]>();
        var done = pair.Network.RunUntil(
            () =>
            {
                Drain(host, atHost);
                Drain(client, atClient);
                return atHost.Count == upstream.Count && atClient.Count == downstream.Count;
            },
            180_000_000);
        That(done, "every frame arrives (" + atHost.Count + " up, " + atClient.Count + " down)");
        That(atHost.Zip(upstream).All(match => match.First.SequenceEqual(match.Second)), "frames up to the engine's largest arrive whole and in order");
        That(atClient.Zip(downstream).All(match => match.First.SequenceEqual(match.Second)), "and the other direction too");
        That(client.PacketsLost > 0 && pair.Network.Lost > 0 && pair.Network.Duplicated > 0, "the network really lost and duplicated packets");

        // A full sustained bucket, then 256 kbit/s; ACK-only packets are exempt and probes may overdraw a little.
        var seconds = (pair.Network.Now - start) / 1_000_000.0;
        var sent = client.BytesSent - sentBefore;
        That(sent <= 66_736 + (32_768 * seconds) + 16_384, "the client never outran its budget (" + sent + " bytes in " + seconds + " s)");
        That(client.Send(ProviderChannel.Session, new byte[ProtocolLimits.MaximumFrameBytes + 1], 0, ProtocolLimits.MaximumFrameBytes + 1) ==
             TransportSendResult.TooLarge, "a frame past the engine's bound is refused");
        That(pair.Network.Spins == 0, "no endpoint asks to be polled for work it cannot do");
    }

    internal static void FlowControlHoldsTheSenderUntilTheReaderConsumes()
    {
        var pair = new TransportPair(12, SmallFrames, SmallFrames);
        var (client, host) = pair.Establish();
        var frames = Enumerable.Range(0, 20).Select(index => Pattern(16 * 1024, index)).ToList();
        foreach (var frame in frames) client.Send(ProviderChannel.Session, frame, 0, frame.Length);
        pair.Network.RunFor(20_000_000);
        That(host.BufferedBytes > 48 * 1024 && host.BufferedBytes <= 64 * 1024,
            "an unread receiver holds no more than its window (" + host.BufferedBytes + ")");
        That(pair.Host.ReassemblyBytes == host.BufferedBytes, "the endpoint counts what its connections hold");
        That(client.State == TransportConnectionState.Established && !client.IsStalled, "a blocked sender keeps the connection alive");

        // The reader takes every whole frame while its WINDOW updates are lost. Nothing more can complete until the
        // sender hears of the space, so a lost update must travel again or both sides wait forever.
        var received = new List<byte[]>();
        pair.Network.Drop = (from, _, _) => from == pair.HostNode;
        Drain(host, received);
        That(received.Count == 3, "three whole frames fit the window (" + received.Count + ")");
        pair.Network.RunFor(1_000_000);
        pair.Network.Drop = null;
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, received);
            return received.Count == frames.Count;
        }, 60_000_000), "reading opens the window until every frame arrives");
        That(received.Zip(frames).All(match => match.First.SequenceEqual(match.Second)), "in order");
        That(pair.Host.ReassemblyBytes == 0 && host.BufferedBytes == 0, "and nothing is left buffered");
        That(pair.Network.Spins == 0, "a blocked sender does not spin");
    }

    internal static void TheProcessCapRefusesBytesItCannotHold()
    {
        var pair = new TransportPair(13, options =>
        {
            SmallFrames(options);
            options.ProcessReassemblyBytes = 64 * 1024;
        }, SmallFrames);
        var (first, firstAtHost) = pair.Establish();
        var secondOptions = new TransportOptions { Random = new SeededTransportRandom(133), RelayOnly = false };
        SmallFrames(secondOptions);
        var secondEndpoint = new TransportEndpoint(TransportPair.Key(13, 3), secondOptions, pair.Network.Now);
        pair.Network.AddEndpoint("second", secondEndpoint);
        var second = secondEndpoint.Connect(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now);
        TransportConnection? secondAtHost = null;
        That(pair.Network.RunUntil(() => second.State == TransportConnectionState.Established && (secondAtHost != null || pair.Host.TryAccept(out secondAtHost)), 5_000_000),
            "a second client connects");
        var frames = Enumerable.Range(0, 3).Select(index => Pattern(16 * 1024, index)).ToList();
        foreach (var frame in frames)
        {
            first.Send(ProviderChannel.Session, frame, 0, frame.Length);
            second.Send(ProviderChannel.Session, frame, 0, frame.Length);
        }

        long peak = 0;
        pair.Network.RunUntil(() =>
        {
            peak = Math.Max(peak, pair.Host.ReassemblyBytes);
            return false;
        }, 10_000_000);
        That(peak <= (2 * 64 * 1024) + ProtectedPacket.MaximumFramesBytes && secondAtHost != null,
            "the endpoint holds at most twice its cap, the second half only in equal shares (" + peak + ")");
        var firstFrames = new List<byte[]>();
        var secondFrames = new List<byte[]>();
        That(pair.Network.RunUntil(() =>
        {
            Drain(firstAtHost, firstFrames);
            Drain(secondAtHost!, secondFrames);
            return firstFrames.Count == 3 && secondFrames.Count == 3;
        }, 60_000_000), "refused bytes come again once there is room");
        That(firstFrames.Concat(secondFrames).Zip(frames.Concat(frames)).All(match => match.First.SequenceEqual(match.Second)), "intact");
    }

    internal static void PeersFillingTheCapCannotStallTheRest()
    {
        var network = new VirtualDatagramNetwork(18);
        var hostOptions = new TransportOptions { Random = new SeededTransportRandom(180), ProcessReassemblyBytes = 64 * 1024, RelayOnly = false };
        SmallFrames(hostOptions);
        var host = new TransportEndpoint(TransportPair.Key(18, 1), hostOptions, network.Now);
        var hostNode = network.AddEndpoint("host", host);

        // Four authenticated peers each announce a 16 KiB frame and send all but its last three bytes: 16 KiB each with
        // the length, filling the cap exactly while no frame can complete.
        var announced = new byte[] { 0x80, 0x80, 0x01 };
        foreach (var hog in Enumerable.Range(0, 4).Select(index => new RawTransportPeer(network, "hog" + index, 180 + index)))
        {
            hog.Connect(hostNode, 64 * 1024);
            That(network.RunUntil(() => host.TryAccept(out _), 1_000_000), "a hog connects");
            var stream = announced.Concat(new byte[(16 * 1024) - announced.Length]).ToArray();
            for (var offset = 0; offset < stream.Length; offset += 1_000)
            {
                var piece = stream.Skip(offset).Take(1_000).ToArray();
                var at = (ulong)offset;
                hog.Send(hostNode, writer =>
                {
                    FrameWriter.WriteStreamHeader(writer, at, piece.Length);
                    writer.WriteBytes(piece, 0, piece.Length);
                });
            }
        }

        network.RunFor(200_000);
        That(host.ReassemblyBytes == 64 * 1024, "the hogs hold the whole cap (" + host.ReassemblyBytes + ")");

        var clientOptions = new TransportOptions { Random = new SeededTransportRandom(189), RelayOnly = false };
        SmallFrames(clientOptions);
        var clientEndpoint = new TransportEndpoint(TransportPair.Key(18, 2), clientOptions, network.Now);
        network.AddEndpoint("client", clientEndpoint);
        var client = clientEndpoint.Connect(VirtualDatagramNetwork.PathTo(hostNode), network.Now);
        TransportConnection? atHost = null;
        That(network.RunUntil(() => client.State == TransportConnectionState.Established && (atHost != null || host.TryAccept(out atHost)), 2_000_000),
            "a fair client connects");
        var frames = Enumerable.Range(0, 3).Select(index => Pattern(8 * 1024, index)).ToList();
        foreach (var frame in frames) client.Send(ProviderChannel.Session, frame, 0, frame.Length);
        var received = new List<byte[]>();
        long peak = 0;
        // Well inside the hogs' idle timeout, so their connections and the cap they hold stay put throughout.
        That(network.RunUntil(() =>
        {
            peak = Math.Max(peak, host.ReassemblyBytes);
            Drain(atHost!, received);
            return received.Count == frames.Count;
        }, 5_000_000) && host.ConnectionCount == 5, "its frames still arrive: a connection below its equal share is always admitted");
        That(received.Zip(frames).All(match => match.First.SequenceEqual(match.Second)) && peak <= 2 * 64 * 1024,
            "intact, with the endpoint under twice its cap (" + peak + ")");
    }

    internal static void DatagramChannelsDeliverOnlyNewerFrames()
    {
        var pair = new TransportPair(14);
        var client = pair.Client.Connect(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now);
        That(client.Send(ProviderChannel.Realtime, new byte[4], 0, 4) == TransportSendResult.NotOpen, "datagrams need an established connection");
        That(client.Send(ProviderChannel.Session, new byte[] { 42 }, 0, 1) == TransportSendResult.Queued, "session frames may queue during the handshake");
        TransportConnection? host = null;
        That(pair.Network.RunUntil(() => client.State == TransportConnectionState.Established && (host != null || pair.Host.TryAccept(out host)), 5_000_000),
            "the client connects");
        pair.Network.Conditions.JitterMicros = 40_000;
        var frames = new List<byte[]>();
        var datagrams = new List<(ProviderChannel Channel, byte[] Payload)>();
        for (var index = 0; index < 60; index++)
        {
            var channel = index % 2 == 0 ? ProviderChannel.Realtime : ProviderChannel.Voice;
            That(client.Send(channel, BitConverter.GetBytes(index), 0, 4) == TransportSendResult.Queued, "a datagram queues");
            pair.Network.RunFor(5_000);
            Drain(host!, frames, datagrams);
        }

        pair.Network.RunFor(500_000);
        Drain(host!, frames, datagrams);
        That(frames.Count == 1 && frames[0].SequenceEqual(new byte[] { 42 }), "the frame queued during the handshake arrives");
        foreach (var channel in new[] { ProviderChannel.Realtime, ProviderChannel.Voice })
        {
            var order = datagrams.Where(item => item.Channel == channel).Select(item => BitConverter.ToInt32(item.Payload)).ToList();
            That(order.Count > 0 && order.Zip(order.Skip(1)).All(step => step.First < step.Second),
                channel + " delivers only frames newer than the last (" + string.Join(",", order) + ")");
        }

        That(datagrams.Count < 60, "reordered stale datagrams are dropped rather than delivered late");
        var largest = Pattern(ProtectedPacket.MaximumDatagramPayload, 3);
        That(client.Send(ProviderChannel.Realtime, new byte[largest.Length + 1], 0, largest.Length + 1) == TransportSendResult.TooLarge,
            "a datagram must fit one packet");
        That(client.Send(ProviderChannel.Realtime, largest, 0, largest.Length) == TransportSendResult.Queued, "the largest datagram queues");
        datagrams.Clear();
        That(pair.Network.RunUntil(() =>
        {
            Drain(host!, frames, datagrams);
            return datagrams.Count == 1;
        }, 1_000_000) && datagrams[0].Payload.SequenceEqual(largest), "and arrives whole");
        Throws<ArgumentOutOfRangeException>(() => client.Send((ProviderChannel)7, new byte[1], 0, 1), "an unknown channel is a caller error");
    }

    internal static void ProbesCarryTailLossThemselves()
    {
        var pair = new TransportPair(16);
        var (client, host) = pair.Establish();
        pair.Network.RunFor(1_000_000);

        // Nothing comes back from the host, so no acknowledgement can tell the client its packet was lost: the bytes
        // arrive only if a probe carries them.
        var lost = false;
        pair.Network.Drop = (from, _, bytes) =>
        {
            if (from == pair.HostNode) return true;
            if (lost || bytes[0] != PacketType.Protected || bytes.Length < 500) return false;
            lost = true;
            return true;
        };
        var frame = Pattern(600, 3);
        var start = pair.Network.Now;
        client.Send(ProviderChannel.Session, frame, 0, frame.Length);
        var received = new List<byte[]>();
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, received);
            return received.Count == 1;
        }, 2_000_000) && lost && received[0].SequenceEqual(frame), "a frame whose only packet was lost arrives in a probe");
        That(pair.Network.Now - start < 500_000,
            "within a probe timeout the handshake's round trip seeded, long before a keepalive (" + (pair.Network.Now - start) + ")");
    }

    internal static void ABacklogPastItsCapClosesTheConnection()
    {
        Action<TransportOptions> capped = options =>
        {
            SmallFrames(options);
            options.SendBacklogBytes = 64 * 1024;
        };
        var pair = new TransportPair(15, capped, capped);
        var (client, host) = pair.Establish();
        var frame = new byte[16 * 1024];
        for (var index = 0; index < 3; index++) That(client.Send(ProviderChannel.Session, frame, 0, frame.Length) == TransportSendResult.Queued, "frames queue");
        That(client.Send(ProviderChannel.Session, frame, 0, frame.Length) == TransportSendResult.Overloaded && client.State == TransportConnectionState.Closing,
            "the frame that would pass the backlog cap closes the connection");
        That(client.Send(ProviderChannel.Session, new byte[1], 0, 1) == TransportSendResult.NotOpen, "a closing connection takes nothing more");
        That(pair.Network.RunUntil(() => host.State >= TransportConnectionState.Draining, 2_000_000), "the peer hears CLOSE");
        That(host.CloseReason == TransportCloseReason.Peer && host.PeerCloseCode == CloseCode.Overloaded && client.CloseReason == TransportCloseReason.Overloaded,
            "both sides know why");
        That(pair.ClientDiagnostics.Reports.Any(report => report.Message == "The session backlog passed its cap."), "the sender reports it");
        var sendStream = TransportSecrets.Field(client, "sendStream");
        That(((System.Collections.ICollection)TransportSecrets.Field(sendStream, "chunks")).Count == 0,
            "and the closed connection lets go of the backlog it can never send");
    }
}
