extern alias provider;

using System.Diagnostics;
using System.Text;
using provider::TopiaForge.Multiplayer.ControlPlane;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The signaling grammar against the control plane's own recorded transcript, its strictness and bounds, and the
/// WebSocket client against a loopback server: the upgrade, frames both ways, close codes and abuse.
/// </summary>
internal static class NatSignalingTests
{
    /// <summary>
    /// The control plane's signaling transcript (ControlPlaneApi.md, section 11), recorded by the service's own tests:
    /// what clients send, which a client must write byte for byte, and what the service sends, which it must read.
    /// </summary>
    private static (int Step, string From, string Frame)[]? transcript;

    private static (int Step, string From, string Frame)[] Transcript => transcript ??= LoadTranscript();

    /// <summary>Gets the peer id the service gave the joining side in the transcript, from its welcome.</summary>
    private static string PeerId => Decode(Frame(2), out var welcome) ? welcome.Peer! : throw new InvalidOperationException("The transcript's peer welcome does not read.");

    internal static void SignalFramesMatchTheControlPlaneTranscript()
    {
        That(Transcript.Length == 15, "the shared fixture holds the whole transcript (" + Transcript.Length + " steps)");
        foreach (var (step, from, frame) in Transcript)
        {
            var bytes = Encoding.UTF8.GetBytes(frame);
            if (from == "service")
            {
                That(SignalCodec.TryDecode(bytes, false, out _), "the client reads step " + step);
                continue;
            }

            That(SignalCodec.TryDecode(bytes, true, out var message), "the service's strict reading accepts step " + step);
            That(Text(SignalCodec.Encode(message)) == frame, "and the client writes step " + step + " back byte for byte");
        }

        // The client writes exactly what the service recorded, from fresh messages built field by field.
        That(Decode(Frame(4), out var offer) && Text(SignalCodec.Encode(SignalMessage.Offer(Copy(offer.Ice!), offer.Candidates.Select(Copy).ToList()))) == Frame(4), "an offer is written byte for byte");
        That(Decode(Frame(7), out var answer) && Text(SignalCodec.Encode(SignalMessage.Answer(answer.Peer!, Copy(answer.Ice!), answer.Candidates.Select(Copy).ToList()))) == Frame(7), "so is an answer");
        That(Decode(Frame(9), out var trickle) && Text(SignalCodec.Encode(SignalMessage.Trickle(null, Copy(trickle.Candidate!)))) == Frame(9), "a trickled candidate");
        That(Decode(Frame(11), out var punch) && Text(SignalCodec.Encode(SignalMessage.Punch(punch.Peer, punch.DelayMs))) == Frame(11), "a punch");
        That(Text(SignalCodec.Encode(SignalMessage.Bye(null, SignalByeReason.Done))) == Frame(13), "and a goodbye");

        That(Decode(Frame(1), out var hostWelcome) && hostWelcome.Type == SignalType.Welcome && hostWelcome.Role == SignalRole.Host && hostWelcome.Peers.Count == 0, "the host's welcome reads");
        That(Decode(Frame(2), out var peerWelcome) && peerWelcome.Role == SignalRole.Peer && peerWelcome.Peer?.Length == 16 && peerWelcome.Online && !peerWelcome.RelayOnly, "and a peer's");
        That(Decode(Frame(3), out var joined) && joined.Type == SignalType.Joined && joined.Peer == PeerId && joined.RelayOnly, "joined carries the peer's relay-only choice");
        That(Decode(Frame(5), out var notice) && notice.Type == SignalType.Notice && notice.Withheld == 3, "a notice counts what was withheld: host, server-reflexive and the mislabelled relay");
        That(Decode(Frame(6), out var forwarded) && forwarded.Peer == PeerId && forwarded.Candidates.Single().Kind == IceCandidateKind.Relayed &&
             offer.Candidates.Any(candidate => candidate.Address == forwarded.Candidates[0].Address), "a forwarded offer names its peer and keeps only the relay candidate inside the ranges");
        That(Decode(Frame(8), out var reachesPeer) && reachesPeer.Peer == null && reachesPeer.Ice!.Ufrag == answer.Ice!.Ufrag, "an answer reaches the peer without a peer id");
        That(Decode(Frame(10), out var trickled) && trickled.Peer == PeerId && trickled.Candidate!.Address == trickle.Candidate!.Address, "a trickled candidate reaches the host with the peer named");
        That(Decode(Frame(12), out var punched) && punched.Peer == null && punched.DelayMs == 250, "a punch carries its delay");
        That(Decode(Frame(14), out var bye) && bye.Type == SignalType.Bye && bye.Peer == PeerId && bye.Reason == SignalByeReason.Done, "a peer's goodbye reaches the host");
        That(Decode(Frame(15), out var left) && left.Type == SignalType.Left && left.Peer == PeerId, "left names the peer");
    }

    internal static void RelayCredentialsMatchTheControlPlaneExchanges()
    {
        var exchanges = NetworkFixtures.Load(Path.Combine("control-plane", "exchanges.json")).GetProperty("exchanges").EnumerateArray()
            .Where(exchange => exchange.GetProperty("request").Text("path") == "/v1/turn/credentials").ToList();
        That(exchanges.Count == 2, "the recording mints relay credentials for the joiner and the host (" + exchanges.Count + ")");
        foreach (var exchange in exchanges)
        {
            var body = Encoding.UTF8.GetBytes(exchange.GetProperty("response").Text("body"));
            That(IceServerSet.TryParse(body, out var set), exchange.Text("id") + ": the recorded answer parses");
            That(set.HasRelay && set.TtlSeconds == 600 && set.TurnServers.All(server => server.Host == "turn.cloudflare.com"),
                exchange.Text("id") + ": with its credential, its lifetime, and only DNS names");
            That(set.TurnServers.Count == 1 && set.TurnServers[0].Port == 3478 && set.StunServers.Count == 2,
                exchange.Text("id") + ": keeping the UDP relay and both STUN ports, and leaving TCP and TLS for later");
        }
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
        using var client = Open(server.Port, serverId, "tfj1_ticket");
        That(Wait(() => server.Upgraded && client.State == SignalingState.Open), "the client upgrades");
        That(server.Path == "/v1/signal/" + serverId && server.Authorization == "Bearer tfj1_ticket", "to the room's path, with the ticket in the header and not the URL");
        server.SendText(Frame(2));
        That(Wait(() => client.TryReceive(out var message) && message.Type == SignalType.Welcome && message.Peer == PeerId), "a service frame arrives decoded");
        That(client.TrySend(SignalMessage.Bye(null, SignalByeReason.Done)), "a frame is queued");
        That(Wait(() => server.Received.TryPeek(out var text) && text == Frame(13)), "and reaches the service exactly as written");
        server.SendText("{not json");
        server.SendText(Frame(12));
        That(Wait(() => client.TryReceive(out var message) && message.Type == SignalType.Punch), "a malformed frame is skipped, the next one read");
        server.SendClose(4411);
        That(Wait(() => client.State == SignalingState.Closed) && client.CloseReason == SignalingCloseReason.Expired, "a lifetime close reads as expired (" + client.CloseReason + ")");
        That(!client.TrySend(SignalMessage.Punch(null, 1)), "nothing queues on a closed socket");

        using var polite = new LoopbackWebSocketServer();
        using var leaving = Open(polite.Port, serverId, "tfh1_token");
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
                     (server => server.SendClose(4408), SignalingCloseReason.RateLimited),
                     (server => server.SendClose(4403), SignalingCloseReason.Revoked)
                 })
        {
            using var server = new LoopbackWebSocketServer();
            using var client = Open(server.Port, serverId, "tfj1_ticket");
            That(Wait(() => client.State == SignalingState.Open), "the client connects");
            attack(server);
            That(Wait(() => client.State == SignalingState.Closed) && client.CloseReason == expected, "the client closes as " + expected + " (" + client.CloseReason + ")");
        }

        using (var refusing = new LoopbackWebSocketServer(401))
        using (var refused = Open(refusing.Port, serverId, "tfj1_stale"))
        {
            That(Wait(() => refused.State == SignalingState.Closed) && refused.CloseReason == SignalingCloseReason.Refused, "a refused upgrade reads as refused (" + refused.CloseReason + ")");
        }

        var closedPort = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        closedPort.Start();
        var port = ((System.Net.IPEndPoint)closedPort.LocalEndpoint).Port;
        closedPort.Stop();
        using (var nowhere = Open(port, serverId, "tfj1_ticket", TimeSpan.FromSeconds(2)))
        {
            That(Wait(() => nowhere.State == SignalingState.Closed) && nowhere.CloseReason == SignalingCloseReason.Unreachable, "nothing listening reads as unreachable (" + nowhere.CloseReason + ")");
        }

        That(ControlPlaneOrigin.TryParse("https://control.example.test", out _) && SignalingUpgrade.Path(serverId) == "/v1/signal/" + serverId,
            "the room is the server's path on the service's https origin");
        That(!ControlPlaneOrigin.TryParse("http://control.example.test", out _), "plain http is refused off this machine");
        That(!ControlPlaneOrigin.TryParse("https://control.example.test/v2", out _), "an origin with a path is refused");
        Throws<ArgumentException>(() => SignalingUpgrade.Path("ABC"), "a malformed server id is refused");
        Throws<ArgumentException>(() => Open(port, serverId, "two words"), "a credential with a space is refused");
    }

    /// <summary>Opens a signaling socket to a loopback room over the HTTPS client's own connection, as the game does.</summary>
    private static WebSocketSignalingClient Open(int port, string serverId, string credential, TimeSpan? connectTimeout = null) =>
        SignalingUpgrade.Open(new HttpsControlPlaneTransport(ControlPlaneOrigin.Loopback(port)), serverId, credential, connectTimeout);

    private static (int Step, string From, string Frame)[] LoadTranscript() =>
        NetworkFixtures.Load(Path.Combine("control-plane", "signaling.json")).GetProperty("transcript").EnumerateArray()
            .Select(step => (step.GetProperty("step").GetInt32(), step.Text("from"), step.Text("frame"))).ToArray();

    private static string Frame(int step) => Transcript.Single(entry => entry.Step == step).Frame;

    private static IceCredentials Copy(IceCredentials ice) => new(ice.Ufrag, ice.Password);

    private static SignalCandidate Copy(SignalCandidate candidate) => new(candidate.Kind, candidate.Address, candidate.Port, candidate.Priority);

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
