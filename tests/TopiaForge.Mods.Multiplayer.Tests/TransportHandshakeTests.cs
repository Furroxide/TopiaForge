extern alias provider;

using provider::TopiaForge.Multiplayer.Transport;
using provider::TopiaForge.Multiplayer.Transport.Crypto;
using provider::TopiaForge.Multiplayer.Transport.Packets;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The handshake over datagrams: keys, pinning, retransmission, cookies, budgets, timeouts and amplification.</summary>
internal static class TransportHandshakeTests
{
    internal static void PeersLearnEachOthersKeysAndConfirmOnce()
    {
        var pair = new TransportPair(1);
        var completions = 0;
        pair.Network.Observe = (from, _, bytes) => completions += from == pair.ClientNode && bytes[0] == PacketType.Completion ? 1 : 0;
        var (client, host) = pair.Establish();
        That(client.RemoteStaticKey.SequenceEqual(X25519.DerivePublicKey(pair.HostKey)) &&
             host.RemoteStaticKey.SequenceEqual(X25519.DerivePublicKey(pair.ClientKey)),
            "each side learns the other's static key");
        That(client.HandshakeHash.SequenceEqual(host.HandshakeHash), "both sides bind the same transcript");
        That(client.RemoteConnectionId == host.LocalConnectionId && host.RemoteConnectionId == client.LocalConnectionId,
            "each side addresses the other by the id it chose");
        That(pair.Host.ConnectionCount == 1 && pair.Host.HalfOpenCount == 0, "the host holds one established connection");
        pair.Network.RunFor(3_000_000);
        That(completions == 1, "the responder's first packet confirms the handshake, so the Completion goes once (" + completions + ")");
        That(pair.ClientDiagnostics.Count(TransportDiagnosticKind.Established) == 1 && pair.HostDiagnostics.Count(TransportDiagnosticKind.Established) == 1,
            "both sides report the connection by number");
        That(pair.Network.Spins == 0, "no endpoint asks to be polled for work it cannot do");
    }

    internal static void PinnedKeysRefuseAnyOtherPeer()
    {
        var pair = new TransportPair(2);
        var stranger = X25519.DerivePublicKey(TransportPair.Key(99, 1));
        var client = pair.Client.Connect(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now, stranger);
        pair.Network.RunFor(1_000_000);
        That(client.State == TransportConnectionState.Closed && client.CloseReason == TransportCloseReason.HandshakeFailed,
            "a responder with another key ends the attempt");
        That(pair.ClientDiagnostics.Reports.Any(report => report.Kind == TransportDiagnosticKind.HandshakeFailed &&
                                                          report.Message == "The peer's key did not match the expected key."),
            "the initiator reports why");
        pair.Network.RunFor(6_000_000);
        That(pair.Host.HalfOpenCount == 0 && pair.Host.Statistics.HandshakesAbandoned == 1 && !pair.Host.TryAccept(out _),
            "the responder abandons the half-open handshake");
        var (pinned, _) = pair.Establish(X25519.DerivePublicKey(pair.HostKey));
        That(pinned.State == TransportConnectionState.Established, "the right key passes the pin");
        Throws<ArgumentException>(() => pair.Client.Connect(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now, new byte[31]),
            "a pin must be a whole key");
    }

    internal static void TheHandshakeSurvivesTheLossOfEachMessage()
    {
        foreach (var type in new[] { PacketType.Initiation, PacketType.Response, PacketType.Completion })
        {
            var pair = new TransportPair(3 + type);
            var dropped = false;
            pair.Network.Drop = (_, _, bytes) =>
            {
                if (dropped || bytes[0] != type) return false;
                dropped = true;
                return true;
            };
            var (client, host) = pair.Establish();
            That(dropped, "the first " + type.ToString("X2") + " was lost");
            client.Send(ProviderChannel.Session, new byte[] { 1, 2, 3 }, 0, 3);
            byte[]? frame = null;
            That(pair.Network.RunUntil(() => host.TryReceive(out _, out frame), 3_000_000) && frame!.SequenceEqual(new byte[] { 1, 2, 3 }),
                "frames flow after losing the first " + type.ToString("X2"));
        }
    }

    internal static void CookiesAreDemandedUnderLoadAndBoundToThePath()
    {
        var pair = new TransportPair(4, host => host.AlwaysRequireCookie = true);
        byte[]? withCookie = null;
        pair.Network.Observe = (from, _, bytes) =>
        {
            if (from == pair.ClientNode && bytes[0] == PacketType.Initiation && bytes[10] == HandshakePackets.CookieSize) withCookie ??= bytes;
        };
        pair.Establish();
        That(pair.Host.Statistics.RetriesSent == 1 && withCookie != null, "the host demanded a cookie and the client returned it");

        // The cookie proves the client can receive at its own address; from anywhere else it is worthless.
        var spoofer = pair.Network.AddRaw("spoofer");
        pair.Network.Inject(spoofer, pair.HostNode, withCookie!);
        pair.Network.RunFor(100_000);
        That(spoofer.Inbox.Count == 0 && pair.Host.Statistics.InitiationsRefused == 1, "a cookie replayed from another path is refused");

        var loaded = new TransportPair(5, host => host.CookieHalfOpenThreshold = 1);
        var raw = loaded.Network.AddRaw("half-open");
        loaded.Network.Inject(raw, loaded.HostNode, Initiation(0x51, 5));
        loaded.Network.RunFor(50_000);
        That(loaded.Host.HalfOpenCount == 1 && loaded.Host.Statistics.RetriesSent == 0, "below the threshold no cookie is needed");
        loaded.Establish();
        That(loaded.Host.Statistics.RetriesSent == 1, "once handshakes are half-open the host demands cookies");

        var busy = new TransportPair(19, host => host.HandshakesPerSecond = 4);
        var sources = Enumerable.Range(0, 3).Select(index => busy.Network.AddRaw("source" + index)).ToList();
        for (var index = 0; index < sources.Count; index++) busy.Network.Inject(sources[index], busy.HostNode, Initiation(0xB0UL + (ulong)index, 20 + index));
        busy.Network.RunFor(50_000);
        That(sources.Select(source => source.Inbox.Single().Bytes[0]).SequenceEqual(new[] { PacketType.Response, PacketType.Response, PacketType.Retry }),
            "more than half the endpoint's handshake rate counts as load, and load demands cookies");
    }

    internal static void AFullEndpointTurnsAwayALateCompletion()
    {
        var network = new VirtualDatagramNetwork(18);
        var options = new TransportOptions { Random = new SeededTransportRandom(18), MaximumConnections = 1 };
        var endpoint = new TransportEndpoint(TransportPair.Key(18, 1), options, network.Now);
        var node = network.AddEndpoint("host", endpoint);
        var first = new RawTransportPeer(network, "first", 18);
        var second = new RawTransportPeer(network, "second", 19);
        first.BeginConnect(node);
        second.BeginConnect(node);
        network.RunFor(50_000);
        That(endpoint.HalfOpenCount == 2, "half-open handshakes do not count against the connection limit");
        first.FinishConnect(node);
        second.FinishConnect(node);
        network.RunFor(100_000);
        That(endpoint.TryAccept(out var accepted) && !endpoint.TryAccept(out _), "only the connection that fits is handed out");
        second.ReadAll();
        That(second.Received.Closes.SequenceEqual(new[] { CloseCode.Overloaded }), "the one that completed second is told the endpoint is full");
        network.RunFor(5_000_000);
        That(endpoint.ConnectionCount == 1 && accepted.State == TransportConnectionState.Established, "and is forgotten once it drains");
    }

    internal static void AnInitiatorTakesOneRetryPerAttempt()
    {
        var network = new VirtualDatagramNetwork(6);
        var endpoint = new TransportEndpoint(TransportPair.Key(6, 1), new TransportOptions { Random = new SeededTransportRandom(6) }, network.Now);
        var node = network.AddEndpoint("client", endpoint);
        var silent = network.AddRaw("silent");
        var connection = endpoint.Connect(VirtualDatagramNetwork.PathTo(silent), network.Now);
        network.RunFor(10_000);
        var first = Enumerable.Repeat((byte)0xAA, 16).ToArray();
        var second = Enumerable.Repeat((byte)0xBB, 16).ToArray();
        network.Inject(network.AddRaw("elsewhere"), node, Retry(connection.LocalConnectionId, second));
        network.Inject(silent, node, Retry(connection.LocalConnectionId, first));
        network.RunFor(20_000);
        network.Inject(silent, node, Retry(connection.LocalConnectionId, second));
        network.Inject(silent, node, Retry(connection.LocalConnectionId ^ 1, second));
        network.RunFor(1_000_000);
        var cookies = silent.Inbox.Select(item => item.Bytes).Where(bytes => bytes[10] == 16).Select(bytes => bytes.Skip(11).Take(16).ToArray()).ToList();
        That(cookies.Count >= 2 && cookies.All(cookie => cookie.SequenceEqual(first)), "the initiator keeps the first cookie from its path");
        That(endpoint.Statistics.DatagramsDropped == 3, "a Retry from another path, a second Retry and one for an unknown id are dropped");
    }

    internal static void BudgetsAndCapacityRefuseExcessHandshakes()
    {
        var pair = new TransportPair(7, host =>
        {
            host.HandshakesPerSecondPerSource = 2;
            host.MaximumConnections = 1;
        });
        var raw = pair.Network.AddRaw("flood");
        for (var index = 0; index < 3; index++) pair.Network.Inject(raw, pair.HostNode, Initiation(0x70UL + (ulong)index, 7));
        pair.Network.RunFor(50_000);
        That(raw.Inbox.Count == 2 && pair.Host.Statistics.InitiationsRefused == 1, "a source gets two handshakes a second here");
        pair.Network.RunFor(6_000_000);
        That(pair.Host.HalfOpenCount == 0 && pair.Host.Statistics.HandshakesAbandoned == 2, "unfinished handshakes are abandoned");

        pair.Establish();
        var second = pair.Network.AddRaw("second");
        pair.Network.Inject(second, pair.HostNode, Initiation(0x99, 8));
        pair.Network.RunFor(50_000);
        That(second.Inbox.Count == 0 && pair.Host.Statistics.InitiationsRefused == 2, "a full endpoint refuses new handshakes");
        Throws<InvalidOperationException>(() =>
        {
            var full = new TransportEndpoint(TransportPair.Key(7, 3), new TransportOptions { MaximumConnections = 1 }, 0);
            full.Connect(new TransportPath(new byte[] { 1 }, TransportPathKind.Direct), 0);
            full.Connect(new TransportPath(new byte[] { 2 }, TransportPathKind.Direct), 0);
        }, "a full endpoint refuses to connect");
    }

    internal static void AnUnansweredHandshakeTimesOut()
    {
        var network = new VirtualDatagramNetwork(8);
        var diagnostics = new RecordingTransportDiagnostics();
        var endpoint = new TransportEndpoint(TransportPair.Key(8, 1), new TransportOptions { Random = new SeededTransportRandom(8), Diagnostics = diagnostics }, network.Now);
        network.AddEndpoint("client", endpoint);
        var silent = network.AddRaw("silent");
        var start = network.Now;
        var connection = endpoint.Connect(VirtualDatagramNetwork.PathTo(silent), network.Now);
        network.RunFor(4_999_000);
        That(connection.State == TransportConnectionState.Handshaking && silent.Inbox.Count == 5,
            "the Initiation goes at 0, 0.25, 0.75, 1.75 and 3.75 seconds (" + silent.Inbox.Count + ")");
        network.RunFor(1_000);
        That(connection.State == TransportConnectionState.Closed && network.Now - start == 5_000_000 &&
             diagnostics.Reports.Single().Message == "The handshake timed out." && connection.CloseReason == TransportCloseReason.HandshakeTimedOut,
            "after five seconds the attempt ends, as a timeout rather than a failed proof");
        That(endpoint.ConnectionCount == 0, "and the endpoint forgets it");
    }

    internal static void RepliesNeverOutweighWhatProvokedThem()
    {
        var pair = new TransportPair(9);
        var raw = pair.Network.AddRaw("prober");
        var initiation = Initiation(0x90, 9);
        for (var index = 0; index < 3; index++) pair.Network.Inject(raw, pair.HostNode, initiation);
        pair.Network.Inject(raw, pair.HostNode, Initiation(0x91, 10));
        pair.Network.RunFor(100_000);
        var replies = raw.Inbox.Select(item => item.Bytes).ToList();
        That(replies.Count == 4 && replies.All(reply => reply.Length < HandshakePackets.InitiationSize && reply[0] == PacketType.Response),
            "each Initiation earns at most one smaller reply");
        That(replies[0].SequenceEqual(replies[1]) && replies[1].SequenceEqual(replies[2]), "a duplicate Initiation gets the cached Response");
        That(pair.Host.HalfOpenCount == 2, "duplicates create no new state");

        var cookied = new TransportPair(10, host => host.AlwaysRequireCookie = true);
        var other = cookied.Network.AddRaw("prober");
        cookied.Network.Inject(other, cookied.HostNode, Initiation(0x92, 11));
        cookied.Network.RunFor(50_000);
        That(other.Inbox.Single().Bytes.Length == HandshakePackets.RetrySize && cookied.Host.HalfOpenCount == 0,
            "a Retry is 25 bytes and keeps no state");
        var truncated = initiation.Take(1199).ToArray();
        var futureVersion = Initiation(0x93, 12);
        futureVersion[1] = 2;
        cookied.Network.Inject(other, cookied.HostNode, truncated);
        cookied.Network.Inject(other, cookied.HostNode, futureVersion);
        cookied.Network.RunFor(50_000);
        That(other.Inbox.Count == 1, "anything short of a full Initiation, or of another version, earns nothing");
    }

    internal static void HandshakeMessagesFromAnotherPathAreIgnored()
    {
        foreach (var type in new[] { PacketType.Response, PacketType.Completion })
        {
            var pair = new TransportPair(16 + type);
            byte[]? held = null;
            pair.Network.Drop = (_, _, bytes) =>
            {
                if (held != null || bytes[0] != type) return false;
                held = bytes;
                return true;
            };
            var client = pair.Client.Connect(VirtualDatagramNetwork.PathTo(pair.HostNode), pair.Network.Now);
            That(pair.Network.RunUntil(() => held != null, 1_000_000), "the first " + type.ToString("X2") + " is held back");
            var receiver = type == PacketType.Response ? pair.ClientNode : pair.HostNode;
            var dropped = receiver.Endpoint!.Statistics.DatagramsDropped;
            pair.Network.Inject(pair.Network.AddRaw("stray"), receiver, held!);
            pair.Network.RunFor(20_000);
            var waiting = type == PacketType.Response ? client.State == TransportConnectionState.Handshaking : pair.Host.HalfOpenCount == 1;
            That(waiting && receiver.Endpoint.Statistics.DatagramsDropped == dropped + 1,
                "a genuine " + type.ToString("X2") + " arriving from another path is ignored");
            TransportConnection? host = null;
            That(pair.Network.RunUntil(() => client.State == TransportConnectionState.Established && (host != null || pair.Host.TryAccept(out host)), 5_000_000),
                "the handshake completes on the real path");
        }
    }

    internal static void APeerWhoseWindowCannotHoldAFrameIsRefused()
    {
        var network = new VirtualDatagramNetwork(17);
        var endpoint = new TransportEndpoint(TransportPair.Key(17, 1), new TransportOptions { Random = new SeededTransportRandom(17) }, network.Now);
        var node = network.AddEndpoint("host", endpoint);
        new RawTransportPeer(network, "raw", 17).Connect(node, window: 1_000);
        network.RunFor(100_000);
        That(!endpoint.TryAccept(out _) && endpoint.HalfOpenCount == 0 && endpoint.Statistics.HandshakesAbandoned == 1,
            "a window smaller than the largest frame could never deliver one, so the handshake fails");
    }

    /// <summary>Builds a valid Initiation from a throwaway initiator, without a cookie.</summary>
    internal static byte[] Initiation(ulong connectionId, int seed)
    {
        using var noise = new NoiseHandshakeXX(true, TransportPair.Key(seed, 5), HandshakePackets.Prologue(connectionId));
        var datagram = new byte[HandshakePackets.InitiationSize];
        var writer = new PacketWriter();
        writer.Reset(datagram, 0, datagram.Length);
        HandshakePackets.WriteInitiation(writer, connectionId, null, noise.WriteMessage(Array.Empty<byte>()));
        return datagram;
    }

    private static byte[] Retry(ulong connectionId, byte[] cookie)
    {
        var datagram = new byte[HandshakePackets.RetrySize];
        var writer = new PacketWriter();
        writer.Reset(datagram, 0, datagram.Length);
        HandshakePackets.WriteRetry(writer, connectionId, cookie);
        return datagram;
    }
}
