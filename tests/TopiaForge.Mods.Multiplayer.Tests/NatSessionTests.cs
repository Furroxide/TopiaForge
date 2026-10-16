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
        // The joiner waits for a host that comes online four seconds later, so it still needs signaling when its socket's
        // short lifetime ends, twice: each time it opens another at once.
        var scenario = new NatScenario(800);
        scenario.Signaling.PeerLifetimeMicros = 1_500_000;
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.Symmetric, "198.51.100.10"), "192.168.1.10", 27250, false, 1);
        var joiner = scenario.AddPeer("joiner", scenario.Behind("joiner router", NatBehaviour.Symmetric, "198.51.100.20"), "192.168.2.20", 27250, false, 2);
        var attempt = scenario.StartJoin(joiner);
        scenario.Network.RunFor(4_000_000);
        That(scenario.Signaling.PeerConnections >= 3, "the joiner reopened signaling each time its socket's lifetime ended (" + scenario.Signaling.PeerConnections + ")");
        scenario.StartHost(host);
        var (joinerLink, hostLink) = scenario.Connect(host, joiner, attempt);
        That(scenario.Exchange(joinerLink, hostLink, 1), "the join connects once the host comes online");
        That(scenario.Network.RunUntil(() => !joiner.Agent.SignalingOpen && scenario.Signaling.OpenPeerSockets == 0, 2_000_000),
            "and the joiner then closes its socket, so its lifetime never comes into it again");
        var opens = joiner.Agent.SignalingOpens;
        scenario.Network.RunFor(10_000_000);
        That(joiner.Agent.SignalingOpens == opens && joiner.Agent.Status.Stage == NatJoinStage.Connected, "it stays closed while there is nothing to say (" + joiner.Agent.Status + ")");
    }

    internal static void AJoinerReopensSignalingOnlyWhenItNeedsTo()
    {
        var (scenario, host, joiner, joinerLink, hostLink) = NatMatrixTests.Session(801, NatBehaviour.FullCone, NatBehaviour.FullCone, configure: setup => setup.Renewable = true, ttlSeconds: 150);
        That(scenario.Network.RunUntil(() => NatScenario.RouteOf(joiner) == NatRoute.Direct && NatScenario.RouteOf(host) == NatRoute.Direct, 15_000_000), "the session goes direct");
        That(scenario.Network.RunUntil(() => !joiner.Agent.SignalingOpen && scenario.Signaling.OpenPeerSockets == 0, 2_000_000), "and the joiner closed its socket once connected");
        Diagnosed(joiner, NatDiagnosticKind.SignalingParked, "with a diagnostic that says so");

        // Its relay rotates: it opens the socket, the host learns the new relay candidate, and it closes again.
        var opens = joiner.Agent.SignalingOpens;
        var rotations = joiner.Agent.Status.Rotations;
        That(scenario.Network.RunUntil(() => joiner.Agent.Status.Rotations > rotations, 150_000_000), "the joiner's relay rotates");
        That(scenario.Network.RunUntil(() => joiner.Agent.RelayUp && !joiner.Agent.SignalingOpen, 30_000_000) && joiner.Agent.SignalingOpens == opens + 1,
            "around which it opened its socket once and closed it again (" + (joiner.Agent.SignalingOpens - opens) + ")");
        var relayed = joiner.Agent.LocalCandidates().Single(candidate => candidate.Kind == provider::TopiaForge.Multiplayer.NatTraversal.Ice.IceCandidateKind.Relayed);
        That(scenario.Network.RunUntil(() => host.Agent.Sessions.Any(session => session.Checker.RemoteCandidates.Any(candidate => candidate.SameAddress(relayed.EndPoint))), 1_000_000),
            "what it queued before closing, its new relay candidate, reached the host");

        // Its direct path breaks while the relay is silent for four seconds, so its connection stalls: the joiner opens its
        // socket, and once the connection runs again, over the relay, it closes it.
        opens = joiner.Agent.SignalingOpens;
        scenario.Network.Drop = (from, to, _) => (from == host.Node && to == joiner.Node) || (from == joiner.Node && to == host.Node);
        scenario.Turn.Silent = true;
        That(scenario.Network.RunUntil(() => joiner.Agent.Sessions.Single(session => session.IsJoin).PathTrouble, 5_000_000), "the joiner's connection stalls");
        That(scenario.Network.RunUntil(() => joiner.Agent.SignalingOpens > opens, 1_000_000), "which makes it open its socket");
        scenario.Network.RunFor(1_000_000);
        scenario.Turn.Silent = false;
        That(scenario.Network.RunUntil(() => NatScenario.RouteOf(joiner) == NatRoute.Relayed && !joiner.Agent.SignalingOpen, 20_000_000),
            "and it closes it again once a path validates (" + NatScenario.RouteOf(joiner) + ")");
        That(!joinerLink.IsClosed && scenario.Exchange(joinerLink, hostLink, 5), "the connection never dropped");
        That(scenario.Signaling.Refusals == 0, "and the room never refused it");
    }

    internal static void AHostRotationReachesAParkedJoiner()
    {
        // Every allocation relays from an address of its own, so a joiner's permission for the host's relay never admits
        // the host's next one: with the joiner's socket closed, the host's new relay candidate reaches it only when the
        // joiner's stalled connection makes it reopen its socket, and the host, seeing it rejoin, sends its candidates again.
        // The joiner arrives halfway between the host's rotations, so each side rotates while the other's socket is settled.
        var scenario = new NatScenario(802, 300) { Renewable = true };
        scenario.DistinctRelayAddresses();
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        var joiner = scenario.AddPeer("joiner", scenario.Behind("joiner router", NatBehaviour.FullCone, "198.51.100.20"), "192.168.2.20", 27250, true, 2);
        scenario.StartHost(host);
        scenario.Network.RunFor(105_000_000);
        var (joinerLink, hostLink) = scenario.Connect(host, joiner, scenario.StartJoin(joiner));
        That(scenario.Network.RunUntil(() => !joiner.Agent.SignalingOpen, 2_000_000), "the joiner closed its socket once connected");

        var began = scenario.Network.Now;
        var opens = joiner.Agent.SignalingOpens;
        var hostRotations = host.Agent.Status.Rotations;
        var whileParked = 0;
        while (scenario.Network.Now - began < 12 * 60 * 1_000_000L)
        {
            for (var second = 0; second < 20; second++)
            {
                scenario.Network.RunFor(1_000_000);
                if (host.Agent.Status.Rotations == hostRotations) continue;
                hostRotations = host.Agent.Status.Rotations;
                if (!joiner.Agent.SignalingOpen) whileParked++;
            }

            That(scenario.Exchange(joinerLink, hostLink, 7, 5_000_000), "frames still flow at " + ((scenario.Network.Now - began) / 1_000_000) + " s");
        }

        That(whileParked >= 3 && joiner.Agent.Status.Rotations >= 3, "the host rotated while the joiner's socket was closed (" + whileParked + " times; the joiner rotated " + joiner.Agent.Status.Rotations + ")");
        That(!joinerLink.IsClosed && !hostLink.IsClosed, "and the connection never dropped");
        That(joiner.Agent.SignalingOpens >= opens + whileParked + joiner.Agent.Status.Rotations,
            "the joiner reopened its socket around its own rotations and each of the host's (" + (joiner.Agent.SignalingOpens - opens) + ")");
        var relays = scenario.Turn.Allocations.Select(allocation => allocation.Relay.Address).ToList();
        That(relays.Count >= 2 && relays.Distinct().Count() == relays.Count, "on relays that share no address");
    }

    internal static void AJoinerSaysAgainWhatItGatheredWithItsSocketClosed()
    {
        // Every allocation relays from an address of its own, and both NATs are port-restricted, so neither side hears from
        // an address it never sent to. The joiner plays over a direct path when its relay is lost, and the relay comes back
        // on a new address after the change no longer holds its socket open, so the new relay candidate is gathered with
        // the socket closed and goes nowhere. When the direct path later breaks, the stalled joiner opens its socket and
        // says everything it holds again, so the host learns the new relay candidate and both move to the relay.
        var (scenario, host, joiner, joinerLink, hostLink) = NatMatrixTests.Session(804, NatBehaviour.PortRestricted, NatBehaviour.PortRestricted,
            configure: setup => setup.DistinctRelayAddresses(), ttlSeconds: 1800);
        That(scenario.Network.RunUntil(() => NatScenario.RouteOf(joiner) == NatRoute.Direct && NatScenario.RouteOf(host) == NatRoute.Direct, 15_000_000), "the session goes direct");
        That(scenario.Network.RunUntil(() => !joiner.Agent.SignalingOpen, 2_000_000), "and the joiner closed its socket");

        scenario.Network.Drop = (from, to, _) => (from == joiner.Node && to == scenario.TurnNode) || (from == scenario.TurnNode && to == joiner.Node);
        That(scenario.Network.RunUntil(() => !joiner.Agent.RelayUp, 700_000_000), "the joiner's relay is lost at its next refresh");
        scenario.Network.RunFor(150_000_000);
        That(!joiner.Agent.SignalingOpen, "and its socket is closed again while the relay stays down, since the direct path carries on");
        var dropped = joiner.Agent.SignalsDropped;
        scenario.Network.Drop = null;
        That(scenario.Network.RunUntil(() => joiner.Agent.RelayUp, 60_000_000), "the relay comes back");
        var relayed = joiner.Agent.LocalCandidates().Single(candidate => candidate.Kind == provider::TopiaForge.Multiplayer.NatTraversal.Ice.IceCandidateKind.Relayed);
        scenario.Network.RunFor(2_000_000);
        That(joiner.Agent.SignalsDropped > dropped && !HostKnows(host, relayed.EndPoint), "its new relay candidate was gathered with the socket closed, and never reached the host");

        scenario.Network.Drop = (from, to, _) => (from == host.Node && to == joiner.Node) || (from == joiner.Node && to == host.Node);
        That(scenario.Network.RunUntil(() => HostKnows(host, relayed.EndPoint), 10_000_000), "the stalled joiner opened its socket and said its new relay candidate");
        That(scenario.Network.RunUntil(() => NatScenario.RouteOf(joiner) == NatRoute.Relayed && NatScenario.RouteOf(host) == NatRoute.Relayed, 20_000_000),
            "both sides moved to the relay (" + NatScenario.RouteOf(joiner) + ", " + NatScenario.RouteOf(host) + ")");
        That(!joinerLink.IsClosed && !hostLink.IsClosed && scenario.Exchange(joinerLink, hostLink, 6), "and the connection survived");
    }

    internal static void AConnectedJoinerTheRoomRefusesAsksAgainLater()
    {
        // A full room refuses a connected joiner's socket as it refuses anyone's, and the client cannot tell why. The joiner
        // does not depend on signaling, so it parks and asks again whenever it next needs to, rather than giving up for the
        // rest of the session: once a place is free, it is back.
        var (scenario, host, joiner, joinerLink, hostLink) = NatMatrixTests.Session(805, NatBehaviour.FullCone, NatBehaviour.FullCone, true, true);
        That(scenario.Network.RunUntil(() => !joiner.Agent.SignalingOpen && scenario.Signaling.OpenPeerSockets == 0, 2_000_000), "the joiner closed its socket once connected");
        scenario.Signaling.Places = 2;
        var others = new[] { scenario.Signaling.ConnectPeer("5e1f0c3a9b2d4e6f", true, "a"), scenario.Signaling.ConnectPeer("8c4b7a6d5e3f2a1b", true, "b") };
        scenario.Network.RunFor(100_000);
        That(scenario.Signaling.OpenPeerSockets == 2, "two other tickets fill the room");

        var refusals = scenario.Signaling.Refusals;
        var session = joiner.Agent.Sessions.Single(item => item.IsJoin);
        scenario.Turn.Silent = true;
        That(scenario.Network.RunUntil(() => session.PathTrouble, 5_000_000), "the relay goes quiet and the joiner's connection stalls");
        That(scenario.Network.RunUntil(() => scenario.Signaling.Refusals > refusals, 2_000_000), "so it asks for its socket, and the full room refuses it");
        scenario.Turn.Silent = false;
        That(scenario.Network.RunUntil(() => !session.PathTrouble && !joiner.Agent.SignalingOpen, 20_000_000), "the connection runs again without signaling");
        That(scenario.Exchange(joinerLink, hostLink, 1), "and frames flow");

        others[0].Close();
        scenario.Network.RunFor(60_000_000);
        That(scenario.Signaling.OpenPeerSockets == 1, "a place frees up");
        var opens = joiner.Agent.SignalingOpens;
        scenario.Turn.Silent = true;
        That(scenario.Network.RunUntil(() => scenario.Signaling.OpenPeerSockets == 2 && joiner.Agent.SignalingOpens > opens, 6_000_000),
            "the next stall puts the joiner back in the room (" + (joiner.Agent.SignalingOpens - opens) + " openings)");
        scenario.Turn.Silent = false;
        That(scenario.Network.RunUntil(() => !session.PathTrouble && !joiner.Agent.SignalingOpen, 20_000_000), "and once its connection runs again it leaves");
        That(!joinerLink.IsClosed && scenario.Exchange(joinerLink, hostLink, 2), "the connection never dropped");
    }

    private static bool HostKnows(NatPeerNode host, IPEndPoint candidate) =>
        host.Agent.Sessions.Any(session => session.Checker.RemoteCandidates.Any(remote => remote.SameAddress(candidate)));

    internal static void FiveJoinersFromOnePrefixStayWithinTheRoom()
    {
        // The documented limits: a room of maxPlayers + 2 places (8 + 2), and four from any one /24 at most. Five players
        // behind one public /24 join one host, one after another, and play for twenty minutes with credentials lasting 150
        // seconds, so every relay rotates many times.
        var scenario = new NatScenario(840, ttlSeconds: 150) { Renewable = true };
        scenario.Signaling.Places = 8 + 2;
        scenario.Signaling.PerPrefix = 4;
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.PortRestricted, "198.51.100.10"), "192.168.1.10", 27250, false, 1);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        var players = new List<(NatPeerNode Joiner, NetworkLink JoinerLink, NetworkLink HostLink)>();
        for (var index = 0; index < 5; index++)
        {
            var home = scenario.Behind("joiner router " + index, NatBehaviour.Symmetric, "203.0.113." + (21 + index));
            var joiner = scenario.AddPeer("joiner " + index, home, "192.168.2." + (20 + index), 27250, false, 2 + index);
            var attempt = scenario.StartJoin(joiner, peerId: "00000000000000a" + index, prefix: "203.0.113.0/24");
            var (joinerLink, hostLink) = scenario.Connect(host, joiner, attempt);
            players.Add((joiner, joinerLink, hostLink));
        }

        That(players.Count == 5 && scenario.Signaling.Refusals == 0, "all five joined from one /24, none refused a place (" + scenario.Signaling.Refusals + ")");
        var samples = 0;
        var open = 0;
        var start = scenario.Network.Now;
        while (scenario.Network.Now - start < 20 * 60 * 1_000_000L)
        {
            for (var second = 0; second < 30; second++)
            {
                scenario.Network.RunFor(1_000_000);

                samples++;
                open += scenario.Signaling.OpenPeerSockets;
            }

            for (var index = 0; index < players.Count; index++)
            {
                That(scenario.Exchange(players[index].JoinerLink, players[index].HostLink, (byte)index, 8_000_000),
                    "joiner " + index + " still plays at " + ((scenario.Network.Now - start) / 1_000_000) + " s");
            }
        }

        foreach (var (joiner, joinerLink, hostLink) in players)
        {
            That(!joinerLink.IsClosed && !hostLink.IsClosed && joiner.Agent.Status.Stage == NatJoinStage.Connected, joiner.Name + ": never dropped (" + joiner.Agent.Status + ")");
            That(joiner.Agent.Status.Rotations >= 15, joiner.Name + ": its relay rotated all along (" + joiner.Agent.Status.Rotations + ")");
            That(joiner.Agent.SignalingOpens <= joiner.Agent.Status.Rotations + 3,
                joiner.Name + ": it opened signaling for its join and around its rotations, not all session (" + joiner.Agent.SignalingOpens + " opens)");
        }

        That(scenario.Signaling.MostPeerSockets <= 4 && scenario.Signaling.MostFromOnePrefix <= 4, "the room never held more than the /24's four (" + scenario.Signaling.MostFromOnePrefix + ")");
        That(open < samples, "on average fewer than one peer socket was open (" + ((double)open / samples).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ")");
        That(host.Agent.Status.Sessions == 5 && scenario.Signaling.HostConnections == 1, "while the host kept its one socket all along");
    }

    private static void Diagnosed(NatPeerNode peer, NatDiagnosticKind kind, string what) => That(peer.Diagnostics.Saw(kind), what);

    internal static void JoinsFailWithAClearReason()
    {
        Expect(810, NatFailure.NoAnswer, setup => setup.Signaling.Swallow = message => message.Type == SignalType.Answer);
        Expect(811, NatFailure.HostEnded, null, afterJoin: setup =>
        {
            // Once the joiner's socket is in the room, the host's session ends: the service closes it with 4410.
            setup.Network.RunFor(100_000);
            setup.Signaling.EndSession();
        });
        Expect(813, NatFailure.TicketRevoked, setup => setup.Signaling.Swallow = message => message.Type == SignalType.Answer, afterJoin: setup =>
        {
            // The joiner's invite is withdrawn while it waits for an answer: the service closes its socket with 4403, for good.
            setup.Network.RunFor(300_000);
            setup.Signaling.Revoke(NatScenario.PeerId);
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

        // An error frame while the joiner is still in the room is reported and changes nothing else.
        scenario.Network.RunFor(100_000);
        scenario.Signaling.SendToPeer(NatScenario.PeerId, new SignalMessage(SignalType.Error) { Error = SignalErrorCode.InvalidMessage });
        scenario.Connect(host, joiner, attempt);
        That(joiner.Diagnostics.Saw(NatDiagnosticKind.SignalingRefusedFrame), "an error frame is reported, and the join connects all the same");
        That(scenario.Signaling.Withheld == 2 && joiner.Agent.Status.WithheldByService == 2, "the host and server-reflexive candidates were withheld and counted");
        That(joiner.Diagnostics.Saw(NatDiagnosticKind.ServiceWithheldCandidates), "with a diagnostic");
        That(host.Agent.Sessions.SelectMany(session => session.Checker.RemoteCandidates).All(candidate => candidate.Kind != provider::TopiaForge.Multiplayer.NatTraversal.Ice.IceCandidateKind.Host),
            "and the host never saw them");
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
