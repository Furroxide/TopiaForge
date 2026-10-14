extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Sessions over signaling: reconnecting sockets, every way a join fails, the service's notices, and a host with several peers.</summary>
internal static class NatSessionTests
{
    internal static void APeerReconnectsWhenItsSignalingLifetimeEnds()
    {
        var (scenario, host, joiner, joinerLink, hostLink) = NatMatrixTests.Session(800, NatBehaviour.Symmetric, NatBehaviour.Symmetric, configure: setup =>
        {
            setup.Signaling.PeerLifetimeMicros = 60_000_000;
            setup.Renewable = true;
        }, ttlSeconds: 150);
        for (var round = 0; round < 10; round++)
        {
            scenario.Network.RunFor(30_000_000);
            That(scenario.Exchange(joinerLink, hostLink, (byte)round, 8_000_000), "frames flow at " + (scenario.Network.Now / 1_000_000) + " s");
        }

        That(scenario.Signaling.PeerConnections >= 4, "the joiner reopened signaling each time its socket's lifetime ended (" + scenario.Signaling.PeerConnections + ")");
        That(joiner.Agent.Status.Failure == NatFailure.None && joiner.Agent.Status.Stage == NatJoinStage.Connected, "and the join never failed (" + joiner.Agent.Status + ")");
        That(host.Agent.Status.Rotations >= 2 && joiner.Agent.Status.Rotations >= 2, "relay candidates trickled across the reconnections kept rotations working");
    }

    internal static void JoinsFailWithAClearReason()
    {
        Expect(810, NatFailure.NoAnswer, setup => setup.Signaling.Swallow = message => message.Type == SignalType.Answer);
        Expect(811, NatFailure.HostEnded, null, afterJoin: setup =>
        {
            // Once the joiner's socket is in the room, the host's session ends: the service closes it with 4410.
            setup.Network.RunFor(100_000);
            setup.Signaling.EndSession();
        });

        // A host that leaves while checks cannot succeed says goodbye, and the joiner stops waiting at once.
        var scenario = new NatScenario(812);
        var hostHome = scenario.Behind("host router", NatBehaviour.Symmetric, "198.51.100.10");
        var joinerHome = scenario.Behind("joiner router", NatBehaviour.Symmetric, "198.51.100.20");
        foreach (var nat in scenario.Router.Internet.Nats.Values) scenario.Turn.Forbidden.Add(nat.PublicAddress);
        var host = scenario.AddPeer("host", hostHome, "192.168.1.10", 27250, false, 1);
        var joiner = scenario.AddPeer("joiner", joinerHome, "192.168.2.20", 27250, false, 2);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        var attempt = scenario.StartJoin(joiner);
        That(scenario.Network.RunUntil(() => host.Agent.Sessions.Count == 1 && host.Agent.Sessions[0].Checker.Started, 5_000_000), "the host answered and started checking");
        host.Agent.Stop(scenario.Network.Now);
        That(scenario.Network.RunUntil(() => attempt.State == ConnectAttemptState.Failed, 3_000_000), "the joiner gives up within moments of the goodbye");
        That(joiner.Agent.Status.Failure == NatFailure.HostEnded, "because the host ended the session (" + joiner.Agent.Status + ")");
    }

    internal static void TheServiceNoticesAndErrorsAreCounted()
    {
        // The joiner's ticket says relay-only, but this agent was not told: the service withholds what it sent, and says so.
        var scenario = new NatScenario(820);
        var hostHome = scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10");
        var joinerHome = scenario.Behind("joiner router", NatBehaviour.FullCone, "198.51.100.20");
        var host = scenario.AddPeer("host", hostHome, "192.168.1.10", 27250, false, 1);
        var joiner = scenario.AddPeer("joiner", joinerHome, "192.168.2.20", 27250, false, 2);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        var attempt = scenario.StartJoin(joiner, ticketRelayOnly: true);
        scenario.Connect(host, joiner, attempt);
        That(scenario.Signaling.Withheld == 2 && joiner.Agent.Status.WithheldByService == 2, "the host and server-reflexive candidates were withheld and counted");
        That(joiner.Diagnostics.Saw(NatDiagnosticKind.ServiceWithheldCandidates), "with a diagnostic");
        That(host.Agent.Sessions.SelectMany(session => session.Checker.RemoteCandidates).All(candidate => candidate.Kind != provider::TopiaForge.Multiplayer.NatTraversal.Ice.IceCandidateKind.Host),
            "and the host never saw them");
        scenario.Signaling.SendToPeer(NatScenario.PeerId, new SignalMessage(SignalType.Error) { Error = SignalErrorCode.InvalidMessage });
        scenario.Network.RunFor(500_000);
        That(joiner.Diagnostics.Saw(NatDiagnosticKind.SignalingRefusedFrame) && attempt.State == ConnectAttemptState.Established, "an error frame is reported and changes nothing else");
    }

    internal static void AHostServesSeveralPeers()
    {
        var scenario = new NatScenario(830);
        // Behind a port-restricted NAT, the host reaches cone peers directly but a symmetric one only through the relay.
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.PortRestricted, "198.51.100.10"), "192.168.1.10", 27250, false, 1);
        var behaviours = new[] { NatBehaviour.FullCone, NatBehaviour.PortRestricted, NatBehaviour.Symmetric };
        var joiners = new List<NatPeerNode>();
        var attempts = new List<ConnectAttempt>();
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        for (var index = 0; index < behaviours.Length; index++)
        {
            var home = scenario.Behind("joiner router " + index, behaviours[index], "198.51.100." + (20 + index));
            var joiner = scenario.AddPeer("joiner " + index, home, "192.168.2." + (20 + index), 27250, false, 2 + index);
            joiners.Add(joiner);
            attempts.Add(scenario.StartJoin(joiner, peerId: "000000000000000" + (index + 1)));
        }

        var links = new List<NetworkLink>();
        That(scenario.Network.RunUntil(() =>
        {
            while (host.Stack.TryAccept(out var link)) links.Add(link);
            return links.Count == 3 && attempts.All(attempt => attempt.State == ConnectAttemptState.Established);
        }, 30_000_000), "all three join");
        scenario.Network.RunFor(10_000_000);
        That(host.Agent.Status.Sessions == 3 && host.Agent.Status.DirectSessions == 2 && host.Agent.Status.RelayedSessions == 1,
            "the host counts two direct sessions and one relayed, for the symmetric NAT (" + host.Agent.Status + ")");

        var before = scenario.Signaling.Sent.Count;
        host.Agent.AddMappedCandidate(new IPEndPoint(IPAddress.Parse("198.51.100.10"), 27250), scenario.Network.Now);
        var trickled = scenario.Signaling.Sent.Skip(before).Where(frame => frame.Role == SignalRole.Host && frame.Frame.Contains("\"type\":\"candidate\"", StringComparison.Ordinal)).ToList();
        That(trickled.Count == 3 && trickled.All(frame => frame.Frame.Contains("\"kind\":\"srflx\"", StringComparison.Ordinal)), "a mapped port is trickled to every peer");

        joiners[0].Agent.Stop(scenario.Network.Now);
        attempts[0].Link!.Close();
        That(scenario.Network.RunUntil(() => host.Agent.Status.Sessions == 2, 90_000_000), "a peer that leaves is forgotten (" + host.Agent.Status + ")");
        for (var index = 1; index < joiners.Count; index++)
        {
            var identity = Convert.ToHexString(provider::TopiaForge.Multiplayer.Transport.Crypto.X25519.DerivePublicKey(TransportPair.Key(830, 2 + index))).ToLowerInvariant();
            var hostSide = links.Single(link => link.PeerIdentity == identity);
            That(scenario.Exchange(attempts[index].Link!, hostSide, (byte)(70 + index)), "joiner " + index + " carries on");
        }
    }

    private static void Expect(int seed, NatFailure expected, Action<NatScenario>? configure, Action<NatScenario>? afterJoin = null)
    {
        var scenario = new NatScenario(seed);
        configure?.Invoke(scenario);
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, false, 1);
        var joiner = scenario.AddPeer("joiner", scenario.Behind("joiner router", NatBehaviour.FullCone, "198.51.100.20"), "192.168.2.20", 27250, false, 2);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        var attempt = scenario.StartJoin(joiner);
        afterJoin?.Invoke(scenario);
        That(scenario.Network.RunUntil(() => attempt.State == ConnectAttemptState.Failed, 30_000_000), "the join fails as " + expected + " (" + joiner.Agent.Status + ")");
        That(joiner.Agent.Status.Failure == expected && joiner.Agent.Status.Stage == NatJoinStage.Failed, "naming the reason " + expected + " (" + joiner.Agent.Status + ")");
    }
}
