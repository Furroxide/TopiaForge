extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using ProtocolLimits = provider::TopiaForge.Multiplayer.Engine.ProtocolLimits;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using ProviderLinkState = provider::TopiaForge.Multiplayer.Engine.EngineLinkState;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using static TopiaForge.Mods.Multiplayer.Tests.TransportFrames;
using static TopiaForge.Mods.Multiplayer.Tests.TransportSecrets;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Keepalive, stall, idle timeout, graceful close, protocol violations and packets that must be dropped.</summary>
internal static class TransportLifecycleTests
{
    internal static void KeepalivesHoldAnIdleConnectionOpen()
    {
        var pair = new TransportPair(21);
        var (client, host) = pair.Establish();
        var sentBefore = client.PacketsSent;
        pair.Network.RunFor(60_000_000);
        That(client.State == TransportConnectionState.Established && host.State == TransportConnectionState.Established &&
             !client.IsStalled && !host.IsStalled,
            "a minute of silence from the application keeps the connection open");
        var sent = client.PacketsSent - sentBefore;
        That(sent >= 30 && sent <= 130, "about one keepalive a second (" + sent + ")");
        That(pair.Network.Spins == 0, "an idle connection never spins");
    }

    internal static void SilentPeersStallThenTimeOut()
    {
        var pair = new TransportPair(22);
        var (client, host) = pair.Establish();
        var link = new TransportEngineLink(client);
        pair.Network.Partition(pair.ClientNode, pair.HostNode, true);
        pair.Network.RunFor(1_000_000);
        That(!client.IsStalled && link.State == ProviderLinkState.Open, "a short silence is not a stall");
        pair.Network.RunFor(2_100_000);
        That(client.IsStalled && host.IsStalled && link.State == ProviderLinkState.Stalled, "three seconds of silence stalls both sides");
        pair.Network.Partition(pair.ClientNode, pair.HostNode, false);
        That(pair.Network.RunUntil(() => !client.IsStalled && !host.IsStalled, 2_000_000), "traffic resumes when the path heals");
        pair.Network.Partition(pair.ClientNode, pair.HostNode, true);
        client.Send(ProviderChannel.Session, Pattern(5_000, 9), 0, 5_000);
        pair.Network.RunFor(10_100_000);
        That(client.State == TransportConnectionState.Closed && client.CloseReason == TransportCloseReason.IdleTimeout &&
             host.CloseReason == TransportCloseReason.IdleTimeout && link.State == ProviderLinkState.Closed,
            "ten seconds of silence ends the connection on both sides");
        var sendStream = TransportSecrets.Field(client, "sendStream");
        That(((System.Collections.ICollection)TransportSecrets.Field(sendStream, "chunks")).Count == 0,
            "a connection that timed out lets go of the frame it never delivered");
        That(pair.ClientDiagnostics.Reports.Last() == (TransportDiagnosticKind.Closed, client.Number, "The connection timed out."),
            "diagnostics name the connection by number");
        That(pair.Client.ConnectionCount == 0 && pair.Host.ConnectionCount == 0, "both endpoints forget it");
    }

    internal static void AGracefulCloseDeliversQueuedFramesFirst()
    {
        var pair = new TransportPair(23);
        pair.Network.Conditions.LossRate = 0.1;
        var (client, host) = pair.Establish(maximumMicros: 30_000_000);
        var keys = new[] { client, host }.SelectMany(end => new[] { KeyOf(end, "sendKey"), KeyOf(end, "receiveKey") }).ToList();
        var frames = Enumerable.Range(0, 5).Select(index => Pattern((index * 4_000) + 10, index)).ToList();
        foreach (var frame in frames) client.Send(ProviderChannel.Session, frame, 0, frame.Length);
        client.Close();
        That(client.State == TransportConnectionState.Closing && new TransportEngineLink(client).State == ProviderLinkState.Closed,
            "the engine sees its link closed at once");
        That(client.Send(ProviderChannel.Session, new byte[1], 0, 1) == TransportSendResult.NotOpen, "and can send nothing more");
        That(pair.Network.RunUntil(() => host.State >= TransportConnectionState.Draining, 30_000_000), "the peer hears CLOSE");
        var received = new List<byte[]>();
        Drain(host, received);
        That(received.Count == frames.Count && received.Zip(frames).All(match => match.First.SequenceEqual(match.Second)),
            "every frame sent before Close arrives before the CLOSE, and stays readable");
        That(host.CloseReason == TransportCloseReason.Peer && host.PeerCloseCode == CloseCode.Normal, "the peer learns it was a normal close");
        That(pair.Network.RunUntil(() => client.State == TransportConnectionState.Closed && host.State == TransportConnectionState.Closed, 10_000_000) &&
             client.CloseReason == TransportCloseReason.Local,
            "both sides finish draining");
        That(pair.Client.ConnectionCount == 0 && pair.Host.ConnectionCount == 0, "and the endpoints forget the connection");
        That(keys.All(key => !key.HasKey), "both sides zero both directions' keys once closed");
        var attempt = pair.Client.Connect(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now);
        attempt.Close();
        That(attempt.State == TransportConnectionState.Closed && attempt.CloseReason == TransportCloseReason.Local, "closing mid-handshake abandons it");
    }

    internal static void DisposingAnEndpointWipesItsSecrets()
    {
        var pair = new TransportPair(25, host => host.AlwaysRequireCookie = true);
        var (client, _) = pair.Establish();
        var keys = new[] { KeyOf(client, "sendKey"), KeyOf(client, "receiveKey") };
        pair.Host.Dispose();
        pair.Client.Dispose();
        That(client.State == TransportConnectionState.Closed && keys.All(key => !key.HasKey), "disposing an endpoint ends its connections and zeroes their keys");
        That(IsZero(pair.Client, "staticPrivateKey") && IsZero(pair.Host, "staticPrivateKey"), "and its static key");
        var cookies = Field(pair.Host, "cookies");
        That(IsZero(cookies, "currentSecret") && IsZero(cookies, "previousSecret"), "and its cookie secrets");
        Throws<ObjectDisposedException>(
            () => pair.Host.Receive(VirtualDatagramNetwork.PathTo(pair.ClientNode), new byte[1], 0, 1, pair.Network.Now),
            "a disposed endpoint takes nothing more");
    }

    internal static void ProtocolViolationsCloseTheConnection()
    {
        var tooLong = (ulong)ProtocolLimits.MaximumFrameBytes + 1;
        var cases = new (string Name, CloseCode Code, Action<PacketWriter> Write)[]
        {
            ("an unknown frame", CloseCode.ProtocolViolation, writer => writer.WriteByte(0x7F)),
            ("an ACK for a packet never sent", CloseCode.ProtocolViolation,
                writer => FrameWriter.WriteAck(writer, new List<PacketRange> { new(1_000_000, 1_000_000) }, 0)),
            ("a datagram on an unknown channel", CloseCode.ProtocolViolation, writer => FrameWriter.WriteDatagram(writer, 9, 0, new byte[] { 1 })),
            ("stream bytes past the window", CloseCode.FlowControl, writer =>
            {
                FrameWriter.WriteStreamHeader(writer, 2 * 1024 * 1024, 1);
                writer.WriteByte(0);
            }),
            ("a frame longer than the largest frame", CloseCode.ProtocolViolation, writer =>
            {
                FrameWriter.WriteStreamHeader(writer, 0, PacketWriter.VarUIntSize(tooLong));
                writer.WriteVarUInt(tooLong);
            })
        };
        var seed = 30;
        foreach (var (name, code, write) in cases)
        {
            var network = new VirtualDatagramNetwork(seed);
            var diagnostics = new RecordingTransportDiagnostics();
            var options = new TransportOptions { Random = new SeededTransportRandom(seed), Diagnostics = diagnostics };
            var endpoint = new TransportEndpoint(TransportPair.Key(seed, 1), options, network.Now);
            var node = network.AddEndpoint("host", endpoint);
            var raw = new RawTransportPeer(network, "raw", seed++);
            raw.Connect(node);
            TransportConnection? accepted = null;
            That(network.RunUntil(() => endpoint.TryAccept(out accepted), 1_000_000), name + ": the raw peer connects");
            raw.Send(node, write);
            network.RunFor(200_000);
            raw.ReadAll();
            That(raw.Received.Closes.SequenceEqual(new[] { code }), name + " ends the connection with " + code);
            That(accepted!.CloseReason == TransportCloseReason.ProtocolViolation && diagnostics.Count(TransportDiagnosticKind.ProtocolViolation) == 1,
                name + " is reported");
        }
    }

    internal static void ReplayedForgedAndStrayPacketsAreDropped()
    {
        var pair = new TransportPair(24);
        var (client, host) = pair.Establish();
        byte[]? captured = null;
        var holding = true;
        var injecting = false;

        // Hold the client's packets back, so the captured one is still fresh while the test plays with copies of it.
        pair.Network.Drop = (from, _, bytes) =>
        {
            if (injecting || !holding || from != pair.ClientNode || bytes[0] != PacketType.Protected) return false;
            if (bytes.Length > 200) captured ??= bytes;
            return true;
        };
        void Inject(VirtualNode from, byte[] bytes)
        {
            injecting = true;
            pair.Network.Inject(from, pair.HostNode, bytes);
            injecting = false;
        }

        client.Send(ProviderChannel.Session, Pattern(200, 1), 0, 200);
        That(pair.Network.RunUntil(() => captured != null, 1_000_000), "the frame's packet is captured");
        var farAhead = (byte[])captured!.Clone();
        farAhead[15] ^= 0x40;
        var flipped = (byte[])captured.Clone();
        flipped[40] ^= 0x01;
        var misaddressed = (byte[])captured.Clone();
        misaddressed[1] ^= 0xFF;
        var dropped = pair.Host.Statistics.DatagramsDropped;
        var received = new List<byte[]>();
        Inject(pair.Network.AddRaw("stray"), captured);
        Inject(pair.ClientNode, farAhead);
        Inject(pair.ClientNode, flipped);
        Inject(pair.ClientNode, misaddressed);
        pair.Network.RunFor(50_000);
        Drain(host, received);
        That(received.Count == 0 && pair.Host.Statistics.DatagramsDropped == dropped + 4,
            "a fresh packet from another path, a forged packet number, a flipped byte and an unknown id are all dropped");
        Inject(pair.ClientNode, captured);
        pair.Network.RunFor(50_000);
        Drain(host, received);
        That(received.Count == 1 && received[0].SequenceEqual(Pattern(200, 1)), "the genuine packet still opens: no forgery moved the replay window");
        Inject(pair.ClientNode, captured);
        pair.Network.RunFor(50_000);
        That(pair.Host.Statistics.DatagramsDropped == dropped + 5, "and a replay of it is dropped");
        holding = false;
        client.Send(ProviderChannel.Session, Pattern(10, 2), 0, 10);
        That(pair.Network.RunUntil(() =>
        {
            Drain(host, received);
            return received.Count == 2;
        }, 2_000_000) && client.State == TransportConnectionState.Established, "and the connection carries on");
    }
}
