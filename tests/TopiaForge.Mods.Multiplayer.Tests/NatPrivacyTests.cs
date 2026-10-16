extern alias provider;

using System.Net;
using System.Reflection;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Every textual form an address could take in a log line, and a check that none appears.</summary>
internal static class AddressLeaks
{
    internal static readonly IPAddress[] ScenarioAddresses =
    {
        IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.2.20"), IPAddress.Parse("198.51.100.10"),
        IPAddress.Parse("198.51.100.20"), IPAddress.Parse("198.51.100.100"), IPAddress.Parse("100.64.0.10"),
        IPAddress.Parse("100.64.0.20"), NatScenario.ServerAddress
    };

    internal static void AssertNone(IEnumerable<string> texts, IEnumerable<IPAddress> addresses, string what)
    {
        var forms = addresses.SelectMany(Forms).Distinct().ToList();
        foreach (var text in texts)
        {
            foreach (var form in forms) That(!text.Contains(form, StringComparison.OrdinalIgnoreCase), what + " names no address (one appeared)");
        }
    }

    private static IEnumerable<string> Forms(IPAddress address)
    {
        var plain = NatAddresses.Unmap(address);
        yield return plain.ToString();
        yield return NatAddresses.Format(plain);
        yield return Convert.ToHexString(plain.GetAddressBytes());
        if (plain.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) yield return "::ffff:" + plain;
    }
}

/// <summary>
/// Privacy, the hard rule: relay-only withholds host and server-reflexive candidates without the service's help and
/// never sends anywhere but its relay, no status, diagnostic, exception or printed object names an address, and an agent
/// started without a control-plane plan contacts nothing.
/// </summary>
internal static class NatPrivacyTests
{
    internal static void RelayOnlyNeverLeaksACandidate()
    {
        foreach (var (hostRelayOnly, joinerRelayOnly, seed) in new[] { (false, true, 400), (true, false, 401), (true, true, 402) })
        {
            var name = (hostRelayOnly ? "relay-only host" : "host") + " and " + (joinerRelayOnly ? "relay-only joiner" : "joiner");
            var (scenario, host, joiner, joinerLink, hostLink) = NatMatrixTests.Session(seed, NatBehaviour.FullCone, NatBehaviour.FullCone,
                hostRelayOnly, joinerRelayOnly, setup => setup.Signaling.Strip = false);
            That(scenario.Exchange(joinerLink, hostLink, 1), name + ": the session works over the relay");
            scenario.Network.RunFor(30_000_000);
            That(NatScenario.RouteOf(joiner) == NatRoute.Relayed && NatScenario.RouteOf(host) == NatRoute.Relayed, name + ": and never leaves it");
            foreach (var (peer, hidden, other) in new[] { (joiner, joinerRelayOnly, host), (host, hostRelayOnly, joiner) })
            {
                if (!hidden) continue;
                var role = ReferenceEquals(peer, host) ? SignalRole.Host : SignalRole.Peer;
                var frames = scenario.Signaling.Sent.Where(frame => frame.Role == role).Select(frame => frame.Frame).ToList();
                That(frames.Count > 0, name + ": the hidden side signaled");
                foreach (var frame in frames)
                {
                    That(SignalCodec.TryDecode(System.Text.Encoding.UTF8.GetBytes(frame), true, out var message), name + ": its frames are valid");
                    var candidates = message.Type == SignalType.Candidate ? new[] { message.Candidate! } : message.Candidates.ToArray();
                    That(candidates.All(candidate => candidate.Kind == IceCandidateKind.Relayed), name + ": every candidate it sent is a relay candidate");
                }

                var own = new[] { peer.Address.Address, scenario.NatOf(SiteOf(scenario, peer)).PublicAddress };
                AddressLeaks.AssertNone(frames, own, name + ": its signaling");
                That(peer.SentTo.All(target => NatAddresses.SameAddress(target.Address, NatScenario.ServerAddress)), name + ": it sent datagrams to its relay and nowhere else");
                That(!other.HeardFrom.Any(source => own.Any(address => NatAddresses.SameAddress(address, source.Address))),
                    name + ": the other side never heard from the hidden side's own address");
                var learned = other.Agent.Sessions.SelectMany(session => session.Checker.RemoteCandidates).ToList();
                That(!learned.Any(candidate => own.Any(address => NatAddresses.SameAddress(address, candidate.EndPoint.Address))),
                    name + ": and never learned it as a candidate");
            }
        }
    }

    internal static void ThePrivacyGateWithholdsOnItsOwn()
    {
        var host = new IceCandidate(IceCandidateKind.Host, new IPEndPoint(IPAddress.Parse("192.168.1.10"), 27250), 1);
        var reflexive = new IceCandidate(IceCandidateKind.ServerReflexive, new IPEndPoint(IPAddress.Parse("198.51.100.10"), 40000), 1);
        var relay = new IceCandidate(IceCandidateKind.Relayed, new IPEndPoint(NatScenario.ServerAddress, 50000), 1);
        var peerReflexive = new IceCandidate(IceCandidateKind.PeerReflexive, new IPEndPoint(IPAddress.Parse("198.51.100.10"), 41000), 1);
        var loopback = new IceCandidate(IceCandidateKind.Relayed, new IPEndPoint(IPAddress.Loopback, 50001), 1);
        var all = new[] { host, reflexive, relay, peerReflexive, loopback };
        var hidden = NatPrivacy.Signalable(all, true, false);
        That(hidden.Count == 1 && hidden[0].Kind == IceCandidateKind.Relayed && hidden[0].Address == "203.0.113.1", "relay-only lets the relay candidate out and nothing else");
        var shown = NatPrivacy.Signalable(all, false, false);
        That(shown.Select(candidate => candidate.Kind).SequenceEqual(new[] { IceCandidateKind.Host, IceCandidateKind.ServerReflexive, IceCandidateKind.Relayed }),
            "otherwise host, server-reflexive and relay go, but never a peer-reflexive or loopback address");
        That(NatPrivacy.Signalable(all, false, true).Count == 4, "loopback is let out only where single-machine tests ask for it");
        var many = Enumerable.Range(1, 20).Select(index => new IceCandidate(IceCandidateKind.Relayed, new IPEndPoint(NatScenario.ServerAddress, 50000 + index), 1));
        That(NatPrivacy.Signalable(many, true, false).Count == SignalCodec.MaximumCandidates, "and never more than the grammar's 16");
    }

    internal static void RelayOnlyGathersNothingButTheRelay()
    {
        var (scenario, host, joiner, _, _) = NatMatrixTests.Session(403, NatBehaviour.FullCone, NatBehaviour.FullCone, true, true);
        foreach (var peer in new[] { host, joiner })
        {
            That(peer.Agent.LocalCandidates().All(candidate => candidate.Kind == IceCandidateKind.Relayed), peer.Name + ": a relay-only agent holds no host or server-reflexive candidate at all");
            That(peer.Agent.SocketBase == null, peer.Name + ": and has no socket base to check from");
            That(scenario.Lookups.Where(lookup => lookup.Peer == peer.Name).All(lookup => lookup.Host == NatScenario.TurnHost), peer.Name + ": it never looked up the STUN server");
        }

        That(scenario.Turn.Bindings == 0, "and no Binding request ever reached a server");

        // Whatever asks, a relay-only agent sends nothing anywhere but its own TURN server.
        while (joiner.Agent.TryDequeueDatagram(out var queued)) joiner.Agent.Release(queued);
        joiner.Agent.SendRaw(UdpPaths.For(new IPEndPoint(IPAddress.Parse("198.51.100.99"), 4000)), new byte[20], 20);
        That(!joiner.Agent.TryDequeueDatagram(out _), "a relay-only agent refuses to send to any address but its relay's");
    }

    internal static void NothingNamesAnAddressEvenWhenTraversalFails()
    {
        var texts = new List<string>();
        var (scenario, host, joiner, joinerLink, hostLink) = NatMatrixTests.Session(410, NatBehaviour.PortRestricted, NatBehaviour.PortRestricted);
        scenario.Network.RunUntil(() => NatScenario.RouteOf(joiner) == NatRoute.Direct, 15_000_000);
        Collect(texts, host, joiner);

        // Failures: UDP blocked, no answer, a host offline, and no route at all because the relay forbids every peer.
        texts.AddRange(FailedJoin(411, setup => setup.Router.Internet.Nats.Values.First(nat => nat.Name == "joiner router").BlocksUdp = true, NatFailure.UdpBlocked));
        texts.AddRange(FailedJoin(412, null, NatFailure.HostOffline, startHost: false));
        texts.AddRange(FailedJoin(413, setup =>
        {
            foreach (var address in AddressLeaks.ScenarioAddresses) setup.Turn.Forbidden.Add(address);
            foreach (var nat in setup.Router.Internet.Nats.Values) setup.Turn.Forbidden.Add(nat.PublicAddress);
        }, NatFailure.NoRoute, symmetric: true));

        // Misuse throws, and its messages are fixed text.
        var agent = joiner.Agent;
        texts.Add(Message(() => agent.StartJoin(scenario.Plan(joiner, () => scenario.Signaling.ConnectPeer(NatScenario.PeerId, false)), new ConnectAttempt(), null, scenario.Network.Now)));
        texts.Add(Message(() => TurnRelayPath.For(1, null!)));
        texts.Add(Message(() => new IceCredentials("x", "short")));
        texts.Add(Message(() => IceServerSet.Create(Array.Empty<IceServerAddress>(), new[] { new IceServerAddress("turn.example.test", 3478) }, "user name", "secret", 600)));
        texts.Add(Message(() => WebSocketSignalingClient.Endpoint(new Uri("http://198.51.100.10:8787"), new string('a', 64))));

        // Every object that holds an address prints without it.
        foreach (var session in joiner.Agent.Sessions.Concat(host.Agent.Sessions))
        {
            foreach (var pair in session.Checker.Pairs) texts.Add(pair + " " + pair.Local + " " + pair.Remote + " " + pair.Path);
            texts.Add(session.Checker.Local.ToString());
        }

        texts.Add(new SignalCandidate(IceCandidateKind.Host, "192.168.2.20", 27250, 1).ToString());
        texts.Add(SignalMessage.Offer(IceCredentials.Generate(new SeededTransportRandom(1)), Array.Empty<SignalCandidate>()).ToString());
        texts.Add(UdpPaths.For(new IPEndPoint(IPAddress.Parse("198.51.100.20"), 40000)).ToString());
        texts.Add(TurnRelayPath.For(3, new IPEndPoint(IPAddress.Parse("198.51.100.20"), 40000)).ToString());
        texts.Add(new IceServerAddress("turn.example.test", 3478) + " " + StunTransactionId.Read(new byte[12], 0));
        That(texts.Count > 40, "the probe saw plenty of text (" + texts.Count + ")");
        AddressLeaks.AssertNone(texts, AddressLeaks.ScenarioAddresses, "every status, diagnostic, exception and printed object");
    }

    internal static void StatusCarriesOnlyEnumsAndCounts()
    {
        var properties = typeof(NatStatus).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        That(properties.Length >= 10, "the status has its members");
        foreach (var property in properties)
        {
            var type = property.PropertyType;
            That(type.IsEnum || type == typeof(bool) || type == typeof(int), "NatStatus." + property.Name + " is an enum, a flag or a count");
        }

        var exposed = typeof(NatAgent).Assembly.GetTypes().Where(type => type.Namespace != null && type.Namespace.StartsWith("TopiaForge.Multiplayer.NatTraversal", StringComparison.Ordinal) && type.IsPublic);
        That(!exposed.Any(), "nothing in the NAT layer is public: mods cannot reach it");
    }

    internal static void DefaultOptionsNeverGoDirect()
    {
        That(new NatAgentOptions().RelayOnly && new TransportOptions().RelayOnly, "both option types hide the player unless told otherwise");

        // Nobody chose direct play: both players' agents and transports keep their defaults, behind cone NATs that would
        // otherwise go direct at once, and the service's own stripping is off.
        var scenario = new NatScenario(430);
        scenario.Signaling.Strip = false;
        var hostHome = scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10");
        var joinerHome = scenario.Behind("joiner router", NatBehaviour.FullCone, "198.51.100.20");
        var host = scenario.AddDefaultPeer("host", hostHome, "192.168.1.10", 27250, 1);
        var joiner = scenario.AddDefaultPeer("joiner", joinerHome, "192.168.2.20", 27250, 2);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        var attempt = scenario.StartJoin(joiner);
        var (joinerLink, hostLink) = scenario.Connect(host, joiner, attempt);
        That(scenario.Exchange(joinerLink, hostLink, 1), "the session works through the relay");
        scenario.Network.RunFor(30_000_000);
        That(NatScenario.RouteOf(joiner) == NatRoute.Relayed && NatScenario.RouteOf(host) == NatRoute.Relayed, "and never goes direct");
        foreach (var peer in new[] { host, joiner })
        {
            That(peer.Agent.RelayOnly && peer.Stack.Endpoint.Options.RelayOnly, peer.Name + ": runs relay-only");
            That(peer.SentTo.All(target => NatAddresses.SameAddress(target.Address, NatScenario.ServerAddress)), peer.Name + ": sends to its relay and nowhere else");
            That(scenario.Lookups.Where(lookup => lookup.Peer == peer.Name).All(lookup => lookup.Host == NatScenario.TurnHost), peer.Name + ": never looks up the STUN server");
            That(peer.Agent.LocalCandidates().All(candidate => candidate.Kind == IceCandidateKind.Relayed) && peer.Agent.SocketBase == null,
                peer.Name + ": holds nothing but its relay candidate, and no socket base to check from");
        }

        That(scenario.Turn.Bindings == 0, "no STUN Binding request ever reached a server");
        That(scenario.Signaling.Sent.All(frame => !frame.Frame.Contains("\"kind\":\"host\"", StringComparison.Ordinal) && !frame.Frame.Contains("\"kind\":\"srflx\"", StringComparison.Ordinal)),
            "and no host or server-reflexive candidate was ever signaled");
    }

    internal static void NoControlPlaneMeansNoContact()
    {
        var agent = new NatAgent(new NatAgentOptions { Random = new SeededTransportRandom(420) });
        var inner = new RecordingSocket();
        var socket = new NatDatagramSocket(inner, agent, () => 1_000_000);
        var service = new NatNetworkService(agent, inner);
        var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(420, 1), new TransportOptions { Random = new SeededTransportRandom(421) }, 0));

        var stunRequest = new byte[20];
        new StunMessageWriter().Begin(stunRequest, StunMethod.Binding, StunClass.Request, StunTransactionId.Read(new byte[12], 0));
        inner.Arrivals.Enqueue((stunRequest, UdpPaths.For(new IPEndPoint(IPAddress.Parse("198.51.100.20"), 40000))));
        inner.Arrivals.Enqueue((new byte[] { 0x40, 0x00, 0x00, 0x01, 0xE8 }, UdpPaths.For(new IPEndPoint(IPAddress.Parse("203.0.113.1"), 3478))));
        inner.Arrivals.Enqueue((new byte[] { 0xE4, 1, 2 }, UdpPaths.For(new IPEndPoint(IPAddress.Parse("192.168.1.10"), 27250))));
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];
        That(socket.TryReceive(buffer, out _, out var stunPath) && stunPath == null, "an idle agent answers no STUN");
        That(socket.TryReceive(buffer, out _, out var channelPath) && channelPath == null, "and unwraps no ChannelData");
        That(socket.TryReceive(buffer, out var count, out var transportPath) && transportPath != null && count == 3, "while transport datagrams pass straight through");
        for (var turn = 0; turn < 100; turn++) service.Turn(stack, 1_000_000 + (turn * 100_000));
        That(inner.Sent.Count == 0 && service.NextTimeoutMicros() == long.MaxValue, "it sends nothing and waits for nothing");
        That(agent.Status.Gathering == NatGathering.Idle && agent.Status.Stage == NatJoinStage.Idle, "and stays idle");
        Throws<ArgumentNullException>(() => NatJoin.Start(null!, NatPlumbing.Wrap(new RecordingSocket(), new NatAgentOptions(), () => 0), null!, null), "a join needs a network");
        var thread = new NetworkThread("TopiaForge test idle", new RecordingSocket(), stack, () => 0);
        Throws<ArgumentNullException>(() => NatJoin.Start(thread, NatPlumbing.Wrap(new RecordingSocket(), new NatAgentOptions(), () => 0), null!, null), "and a control-plane plan");
        Throws<ArgumentNullException>(() => NatHost.Start(thread, NatPlumbing.Wrap(new RecordingSocket(), new NatAgentOptions(), () => 0), null!, true), "as does hosting");
        thread.Dispose();
        service.Dispose();
        That(inner.Sent.Count == 0, "and stopping it says nothing either");
    }

    private static IEnumerable<string> FailedJoin(int seed, Action<NatScenario>? configure, NatFailure expected, bool startHost = true, bool symmetric = false)
    {
        var scenario = new NatScenario(seed);
        var behaviour = symmetric ? NatBehaviour.Symmetric : NatBehaviour.FullCone;
        var hostHome = scenario.Behind("host router", behaviour, "198.51.100.10");
        var joinerHome = scenario.Behind("joiner router", behaviour, "198.51.100.20");
        var host = scenario.AddPeer("host", hostHome, "192.168.1.10", 27250, false, 1);
        var joiner = scenario.AddPeer("joiner", joinerHome, "192.168.2.20", 27250, false, 2);
        configure?.Invoke(scenario);
        if (startHost) scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        var attempt = scenario.StartJoin(joiner);
        var statuses = new List<string>();
        That(scenario.Network.RunUntil(() =>
        {
            statuses.Add(joiner.Agent.Status.ToString());
            return attempt.State == ConnectAttemptState.Failed;
        }, 60_000_000), "the join fails as " + expected + " (" + joiner.Agent.Status + ")");
        That(joiner.Agent.Status.Failure == expected, "with the reason " + expected + " (" + joiner.Agent.Status + ")");
        var texts = new List<string>(statuses.Distinct());
        Collect(texts, host, joiner);
        return texts;
    }

    private static void Collect(List<string> texts, NatPeerNode host, NatPeerNode joiner)
    {
        foreach (var peer in new[] { host, joiner })
        {
            texts.Add(peer.Agent.Status.ToString());
            texts.AddRange(peer.Diagnostics.Reports.Select(report => report.Kind + " " + report.Message));
        }
    }

    private static string Message(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception.GetType().Name + ": " + exception.Message;
        }

        throw new InvalidOperationException("Assertion failed: the misuse was refused");
    }

    private static SimulatedSite SiteOf(NatScenario scenario, NatPeerNode peer) =>
        scenario.Router.Internet.Nats.Values.First(nat => nat.Name == (peer.Name == "host" ? "host router" : "joiner router")).Inside;

    /// <summary>A socket that records what goes out and hands over what a test queued.</summary>
    internal sealed class RecordingSocket : IDatagramSocket
    {
        internal Queue<(byte[] Bytes, TransportPath Path)> Arrivals { get; } = new();

        internal List<(TransportPath Path, byte[] Bytes)> Sent { get; } = new();

        public int LocalPort => 27250;

        public bool WaitReadable(long timeoutMicros) => Arrivals.Count > 0;

        public bool TryReceive(byte[] buffer, out int count, out TransportPath? path)
        {
            count = 0;
            path = null;
            if (!Arrivals.TryDequeue(out var arrival)) return false;
            if (arrival.Bytes.Length > buffer.Length) return true;
            arrival.Bytes.CopyTo(buffer, 0);
            count = arrival.Bytes.Length;
            path = arrival.Path;
            return true;
        }

        public void Send(TransportPath path, byte[] buffer, int offset, int count) => Sent.Add((path, buffer.AsSpan(offset, count).ToArray()));

        public void Dispose()
        {
        }
    }
}
