extern alias provider;

using System.Diagnostics;
using System.Text;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The signaling grammar against the control plane's own recorded transcript, its strictness and bounds, and the
/// WebSocket client against a loopback server: the upgrade, frames both ways, close codes and abuse.
/// </summary>
internal static class NatSignalingTests
{
    private const string PeerId = "74aea50c644d302f";

    // The control plane's signaling transcript (ControlPlaneApi.md, section 11), frame for frame. Steps 4, 7, 9, 11 and
    // 13 are what clients send; the rest are what the service sends.
    private static readonly (int Step, string From, string Frame)[] Transcript =
    {
        (1, "service", """{"peers":[],"role":"host","type":"welcome"}"""),
        (2, "service", """{"hostOnline":true,"hostRelayOnly":false,"peer":"74aea50c644d302f","role":"peer","type":"welcome"}"""),
        (3, "service", """{"peer":"74aea50c644d302f","relayOnly":true,"type":"joined"}"""),
        (4, "peer", """{"candidates":[{"address":"192.168.1.20","kind":"host","port":27250,"priority":2130706431},{"address":"198.51.100.77","kind":"srflx","port":40000,"priority":1694498815},{"address":"203.0.113.50","kind":"relay","port":50000,"priority":16777215}],"ice":{"pwd":"0123456789abcdefABCDEF","ufrag":"abcd1234"},"type":"offer"}"""),
        (5, "service", """{"code":"candidates-withheld","type":"notice","withheld":2}"""),
        (6, "service", """{"candidates":[{"address":"203.0.113.50","kind":"relay","port":50000,"priority":16777215}],"ice":{"pwd":"0123456789abcdefABCDEF","ufrag":"abcd1234"},"peer":"74aea50c644d302f","type":"offer"}"""),
        (7, "host", """{"candidates":[{"address":"203.0.113.9","kind":"srflx","port":27250,"priority":1694498815}],"ice":{"pwd":"ZYXWVUTSRQPONMLKJIHGFE","ufrag":"wxyz5678"},"peer":"74aea50c644d302f","type":"answer"}"""),
        (8, "service", """{"candidates":[{"address":"203.0.113.9","kind":"srflx","port":27250,"priority":1694498815}],"ice":{"pwd":"ZYXWVUTSRQPONMLKJIHGFE","ufrag":"wxyz5678"},"type":"answer"}"""),
        (9, "peer", """{"candidate":{"address":"203.0.113.51","kind":"relay","port":50001,"priority":16777214},"type":"candidate"}"""),
        (10, "service", """{"candidate":{"address":"203.0.113.51","kind":"relay","port":50001,"priority":16777214},"peer":"74aea50c644d302f","type":"candidate"}"""),
        (11, "host", """{"delayMs":250,"peer":"74aea50c644d302f","type":"punch"}"""),
        (12, "service", """{"delayMs":250,"type":"punch"}"""),
        (13, "peer", """{"reason":"done","type":"bye"}"""),
        (14, "service", """{"peer":"74aea50c644d302f","reason":"done","type":"bye"}"""),
        (15, "service", """{"peer":"74aea50c644d302f","type":"left"}""")
    };

    internal static void SignalFramesMatchTheControlPlaneTranscript()
    {
        Replay(Transcript, "the embedded transcript");

        // The control plane records the same transcript as a shared fixture; once it is in the tree, it is replayed too.
        System.Text.Json.JsonElement? shared = null;
        try
        {
            shared = NetworkFixtures.Load(Path.Combine("control-plane", "signaling.json"));
        }
        catch (Exception exception) when (exception is FileNotFoundException || exception is DirectoryNotFoundException)
        {
            shared = null;
        }

        if (shared != null)
        {
            var steps = shared.Value.GetProperty("transcript").EnumerateArray()
                .Select(step => (step.GetProperty("step").GetInt32(), step.Text("from"), step.Text("frame"))).ToArray();
            Replay(steps, "the shared fixture");
        }

        // The client writes exactly what the service recorded.
        var peerIce = new IceCredentials("abcd1234", "0123456789abcdefABCDEF");
        var hostIce = new IceCredentials("wxyz5678", "ZYXWVUTSRQPONMLKJIHGFE");
        var offer = SignalMessage.Offer(peerIce, new[]
        {
            new SignalCandidate(IceCandidateKind.Host, "192.168.1.20", 27250, 2130706431),
            new SignalCandidate(IceCandidateKind.ServerReflexive, "198.51.100.77", 40000, 1694498815),
            new SignalCandidate(IceCandidateKind.Relayed, "203.0.113.50", 50000, 16777215)
        });
        That(Text(SignalCodec.Encode(offer)) == Transcript[3].Frame, "an offer is written byte for byte");
        That(Text(SignalCodec.Encode(SignalMessage.Answer(PeerId, hostIce, new[] { new SignalCandidate(IceCandidateKind.ServerReflexive, "203.0.113.9", 27250, 1694498815) }))) == Transcript[6].Frame,
            "so is an answer");
        That(Text(SignalCodec.Encode(SignalMessage.Trickle(null, new SignalCandidate(IceCandidateKind.Relayed, "203.0.113.51", 50001, 16777214)))) == Transcript[8].Frame, "a trickled candidate");
        That(Text(SignalCodec.Encode(SignalMessage.Punch(PeerId, 250))) == Transcript[10].Frame, "a punch");
        That(Text(SignalCodec.Encode(SignalMessage.Bye(null, SignalByeReason.Done))) == Transcript[12].Frame, "and a goodbye");

        That(Decode(Transcript[0].Frame, out var hostWelcome) && hostWelcome.Type == SignalType.Welcome && hostWelcome.Role == SignalRole.Host && hostWelcome.Peers.Count == 0, "the host's welcome reads");
        That(Decode(Transcript[1].Frame, out var peerWelcome) && peerWelcome.Role == SignalRole.Peer && peerWelcome.Peer == PeerId && peerWelcome.Online && !peerWelcome.RelayOnly, "and a peer's");
        That(Decode(Transcript[2].Frame, out var joined) && joined.Type == SignalType.Joined && joined.RelayOnly, "joined carries the peer's relay-only choice");
        That(Decode(Transcript[4].Frame, out var notice) && notice.Type == SignalType.Notice && notice.Withheld == 2, "a notice counts what was withheld");
        That(Decode(Transcript[5].Frame, out var forwarded) && forwarded.Peer == PeerId && forwarded.Candidates.Single().Kind == IceCandidateKind.Relayed, "a forwarded offer names its peer");
        That(Decode(Transcript[7].Frame, out var answer) && answer.Peer == null && answer.Ice!.Ufrag == "wxyz5678", "an answer reaches the peer without a peer id");
        That(Decode(Transcript[11].Frame, out var punch) && punch.DelayMs == 250, "a punch carries its delay");
        That(Decode(Transcript[14].Frame, out var left) && left.Type == SignalType.Left && left.Peer == PeerId, "left names the peer");
    }

    internal static void SignalFramesAreReadStrictlyAndBounded()
    {
        var bad = new[]
        {
            """{"type":"punch","delayMs":250}""",
            """{ "delayMs":250,"type":"punch"}""",
            """{"delayMs":10001,"type":"punch"}""",
            """{"delayMs":-1,"type":"punch"}""",
            """{"reason":"bored","type":"bye"}""",
            """{"type":"dance"}""",
            """{"peer":"74AEA50C644D302F","type":"left"}""",
            """{"peer":"74aea50c644d302","type":"left"}""",
            """{"candidate":{"address":"192.168.001.020","kind":"host","port":1,"priority":1},"type":"candidate"}""",
            """{"candidate":{"address":"2001:DB8::1","kind":"host","port":1,"priority":1},"type":"candidate"}""",
            """{"candidate":{"address":"::ffff:1.2.3.4","kind":"host","port":1,"priority":1},"type":"candidate"}""",
            """{"candidate":{"address":"203.0.113.9","kind":"prflx","port":1,"priority":1},"type":"candidate"}""",
            """{"candidate":{"address":"203.0.113.9","kind":"host","port":0,"priority":1},"type":"candidate"}""",
            """{"candidate":{"address":"203.0.113.9","kind":"host","port":65536,"priority":1},"type":"candidate"}""",
            """{"candidate":{"address":"203.0.113.9","kind":"host","port":1,"priority":4294967296},"type":"candidate"}""",
            """{"candidates":[],"ice":{"pwd":"short","ufrag":"abcd1234"},"type":"answer"}""",
            """{"candidates":[],"ice":{"pwd":"0123456789abcdefABCDEF","ufrag":"ab-1234"},"type":"answer"}""",
            """{"candidates":[],"ice":{"extra":"x","pwd":"0123456789abcdefABCDEF","ufrag":"abcd1234"},"type":"answer"}""",
            """{"hostOnline":true,"hostRelayOnly":false,"role":"peer","type":"welcome"}"""
        };
        foreach (var frame in bad) That(!SignalCodec.TryDecode(Encoding.UTF8.GetBytes(frame), false, out _), "a malformed frame reads as nothing");

        var seventeen = "{\"candidates\":[" + string.Join(",", Enumerable.Range(1, 17).Select(index => "{\"address\":\"203.0.113." + index + "\",\"kind\":\"relay\",\"port\":1,\"priority\":1}")) +
                        "],\"ice\":{\"pwd\":\"0123456789abcdefABCDEF\",\"ufrag\":\"abcd1234\"},\"type\":\"answer\"}";
        That(!SignalCodec.TryDecode(Encoding.UTF8.GetBytes(seventeen), false, out _), "an answer carries at most 16 candidates");
        That(!SignalCodec.TryDecode(new byte[8193], false, out _) && !SignalCodec.TryDecode(null, false, out _), "a frame past 8 KiB is refused");

        var extra = """{"delayMs":250,"future":true,"type":"punch"}""";
        That(SignalCodec.TryDecode(Encoding.UTF8.GetBytes(extra), false, out var lenient) && lenient.DelayMs == 250, "members the service adds later are ignored when reading");
        That(!SignalCodec.TryDecode(Encoding.UTF8.GetBytes(extra), true, out _), "while the strict reading the service uses refuses them");
        That(Decode("""{"code":"mystery","type":"error"}""", out var error) && error.Error == SignalErrorCode.Unknown, "an unknown error code still reads, as unknown");
        Throws<ArgumentException>(() => SignalCodec.Encode(SignalMessage.Punch(null, 10_001)), "a punch past ten seconds is never written");
        Throws<ArgumentException>(() => SignalCodec.Encode(SignalMessage.Punch("NOT-A-PEER", 1)), "nor a bad peer id");
        Throws<ArgumentException>(() => new SignalCandidate(IceCandidateKind.PeerReflexive, "203.0.113.9", 1, 1), "nor a peer-reflexive candidate");
    }

    internal static void TheWebSocketClientSpeaksTheRoomProtocol()
    {
        using var server = new LoopbackWebSocketServer();
        var serverId = new string('a', 64);
        using var client = WebSocketSignalingClient.Open(new Uri("http://127.0.0.1:" + server.Port), serverId, "tfj1_ticket");
        That(Wait(() => server.Upgraded && client.State == SignalingState.Open), "the client upgrades");
        That(server.Path == "/v1/signal/" + serverId && server.Authorization == "Bearer tfj1_ticket", "to the room's path, with the ticket in the header and not the URL");
        server.SendText(Transcript[1].Frame);
        That(Wait(() => client.TryReceive(out var message) && message.Type == SignalType.Welcome && message.Peer == PeerId), "a service frame arrives decoded");
        That(client.TrySend(SignalMessage.Bye(null, SignalByeReason.Done)), "a frame is queued");
        That(Wait(() => server.Received.TryPeek(out var text) && text == Transcript[12].Frame), "and reaches the service exactly as written");
        server.SendText("{not json");
        server.SendText(Transcript[11].Frame);
        That(Wait(() => client.TryReceive(out var message) && message.Type == SignalType.Punch), "a malformed frame is skipped, the next one read");
        server.SendClose(4411);
        That(Wait(() => client.State == SignalingState.Closed) && client.CloseReason == SignalingCloseReason.Expired, "a lifetime close reads as expired (" + client.CloseReason + ")");
        That(!client.TrySend(SignalMessage.Punch(null, 1)), "nothing queues on a closed socket");

        using var polite = new LoopbackWebSocketServer();
        using var leaving = WebSocketSignalingClient.Open(new Uri("http://127.0.0.1:" + polite.Port), serverId, "tfh1_token");
        That(Wait(() => leaving.State == SignalingState.Open), "a second client connects");
        leaving.TrySend(SignalMessage.Bye(PeerId, SignalByeReason.Done));
        leaving.Close();
        That(Wait(() => polite.ClosedByClient == 1000) && polite.Received.Count == 1, "closing sends what was queued, then a normal close");
    }

    internal static void TheWebSocketClientRefusesAbuseAndNamesFailures()
    {
        var serverId = new string('b', 64);
        foreach (var (attack, expected) in new (Action<LoopbackWebSocketServer>, SignalingCloseReason)[]
                 {
                     (server => server.SendText(new string('x', 9000)), SignalingCloseReason.Invalid),
                     (server => server.SendBinary(new byte[] { 1, 2, 3 }), SignalingCloseReason.Invalid),
                     (server => server.SendClose(4409), SignalingCloseReason.Replaced),
                     (server => server.SendClose(4410), SignalingCloseReason.SessionEnded),
                     (server => server.SendClose(4408), SignalingCloseReason.RateLimited)
                 })
        {
            using var server = new LoopbackWebSocketServer();
            using var client = WebSocketSignalingClient.Open(new Uri("http://127.0.0.1:" + server.Port), serverId, "tfj1_ticket");
            That(Wait(() => client.State == SignalingState.Open), "the client connects");
            attack(server);
            That(Wait(() => client.State == SignalingState.Closed) && client.CloseReason == expected, "the client closes as " + expected + " (" + client.CloseReason + ")");
        }

        using (var refusing = new LoopbackWebSocketServer(401))
        using (var refused = WebSocketSignalingClient.Open(new Uri("http://127.0.0.1:" + refusing.Port), serverId, "tfj1_stale"))
        {
            That(Wait(() => refused.State == SignalingState.Closed) && refused.CloseReason == SignalingCloseReason.Refused, "a refused upgrade reads as refused (" + refused.CloseReason + ")");
        }

        var closedPort = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closedPort.Start();
        var port = ((System.Net.IPEndPoint)closedPort.LocalEndpoint).Port;
        closedPort.Stop();
        using (var nowhere = WebSocketSignalingClient.Open(new Uri("http://127.0.0.1:" + port), serverId, "tfj1_ticket", TimeSpan.FromSeconds(2)))
        {
            That(Wait(() => nowhere.State == SignalingState.Closed) && nowhere.CloseReason == SignalingCloseReason.Unreachable, "nothing listening reads as unreachable (" + nowhere.CloseReason + ")");
        }

        That(WebSocketSignalingClient.Endpoint(new Uri("https://control.example.test"), serverId).ToString() == "wss://control.example.test/v1/signal/" + serverId, "an https origin becomes wss");
        Throws<ArgumentException>(() => WebSocketSignalingClient.Endpoint(new Uri("http://control.example.test"), serverId), "plain http is refused off this machine");
        Throws<ArgumentException>(() => WebSocketSignalingClient.Endpoint(new Uri("https://control.example.test/v2"), serverId), "an origin with a path is refused");
        Throws<ArgumentException>(() => WebSocketSignalingClient.Endpoint(new Uri("https://control.example.test"), "ABC"), "a malformed server id is refused");
        Throws<ArgumentException>(() => WebSocketSignalingClient.Open(new Uri("https://control.example.test"), serverId, "two words"), "a credential with a space is refused");
    }

    private static void Replay((int Step, string From, string Frame)[] steps, string source)
    {
        foreach (var (step, from, frame) in steps)
        {
            var bytes = Encoding.UTF8.GetBytes(frame);
            if (from == "service")
            {
                That(SignalCodec.TryDecode(bytes, false, out _), source + ": the client reads step " + step);
                continue;
            }

            That(SignalCodec.TryDecode(bytes, true, out var message), source + ": the service's strict reading accepts step " + step);
            That(Text(SignalCodec.Encode(message)) == frame, source + ": and the client writes step " + step + " back byte for byte");
        }
    }

    private static bool Decode(string frame, out SignalMessage message) => SignalCodec.TryDecode(Encoding.UTF8.GetBytes(frame), false, out message);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static bool Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5_000)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }

        return condition();
    }
}
