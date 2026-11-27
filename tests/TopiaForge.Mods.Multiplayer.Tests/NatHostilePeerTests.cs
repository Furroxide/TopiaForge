extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Hostile peers against a relay-only host on the NAT simulator, each staying within its own shares. Unless a test says
/// otherwise, every allocation relays from an address of its own, as the relay service's did in staging, so each new
/// joiner needs a permission of its own on the host's allocation. However many hostile identities there are, warm or
/// cold, at once or one after another, and whatever they name, a player who is connected stays connected through its
/// relay rotations and the next honest joiner gets in; a relay address an honest player shares with a hostile one stays
/// while the player uses it.
/// </summary>
internal static class NatHostilePeerTests
{
    private const long Second = 1_000_000;

    internal static void AFloodOfCandidatesFromOnePeerLeavesRoomForHonestJoiners()
    {
        var scenario = new NatScenario(3100);
        scenario.DistinctRelayAddresses();
        var host = Host(scenario);
        var first = Joiner(scenario, 0);
        var second = Joiner(scenario, 1);
        scenario.StartHost(host);
        scenario.Network.RunFor(Second);
        var (firstLink, firstHostSide) = scenario.Connect(host, first, scenario.StartJoin(first, peerId: "a1a1a1a1a1a1a1a1", prefix: "198.51.20"));
        That(scenario.Exchange(firstLink, firstHostSide, 1), "the first honest joiner plays");

        // A hostile peer floods 256 distinct candidate addresses over sixteen offers from one identity.
        var hostile = scenario.Signaling.ConnectPeer("ffffffffffffffff", false, "45.0.0");
        scenario.Network.RunFor(Second);
        var random = new SeededTransportRandom(77);
        for (var offer = 0; offer < 16; offer++)
        {
            var candidates = new List<SignalCandidate>();
            for (var index = 0; index < 16; index++) candidates.Add(new SignalCandidate(IceCandidateKind.ServerReflexive, "45.0." + offer + "." + (index + 1), 40000 + index, 1000u));
            hostile.TrySend(SignalMessage.Offer(IceCredentials.Generate(random), candidates));
            scenario.Network.RunFor(300_000);
        }

        scenario.Network.RunFor(2 * Second);
        var held = ClaimedBy(host, "ffffffffffffffff");
        That(held.Permissions <= 16, "each offer replaced the last, so the hostile peer holds one share at most (" + held.Permissions + ")");
        That(scenario.Exchange(firstLink, firstHostSide, 2), "the first joiner still plays: the flood took nothing it holds");
        var (secondLink, secondHostSide) = scenario.Connect(host, second, scenario.StartJoin(second, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.21"));
        That(scenario.Exchange(secondLink, secondHostSide, 3), "and a second honest joiner, on a relay address of its own, still gets in");
    }

    internal static void ASprayFromManyPortsLeavesRoomForHonestJoiners()
    {
        var scenario = new NatScenario(3110);
        scenario.DistinctRelayAddresses();
        var host = Host(scenario);
        var joiner = Joiner(scenario, 0);
        var attacker = NatHostileSender.On(scenario, "45.1.1.1");
        scenario.StartHost(host);
        scenario.Network.RunFor(2 * Second);

        // A hostile peer gets its one address permitted, then sprays the host's relay from three hundred source ports.
        NatHostileSender.OfferAndLeave(scenario, "ffffffffffffffff", "45.1.1", new List<SignalCandidate> { new(IceCandidateKind.ServerReflexive, "45.1.1.1", 40000, 1000u) }, new SeededTransportRandom(78));
        scenario.Network.RunFor(2 * Second);
        var relayed = host.Agent.Relay!.RelayedAddress!;
        for (var port = 0; port < 300; port++) attacker.Outbox.Enqueue((UdpPaths.For(new IPEndPoint(attacker.Address, 41000 + port)), UdpPaths.For(relayed), new byte[] { 0xE1, 1, 2, 3, 4, 5, 6, 7 }));
        scenario.Network.RunFor(3 * Second);
        That(NatTableQuotaTests.Unclaimed(host.Agent.Relay!, attacker.Address) <= 8, "the sprayer holds no more than eight pooled ports (" + NatTableQuotaTests.Unclaimed(host.Agent.Relay!, attacker.Address) + ")");

        var (link, hostSide) = scenario.Connect(host, joiner, scenario.StartJoin(joiner, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.20"));
        That(scenario.Exchange(link, hostSide, 1), "an honest joiner still gets in past the spray");
    }

    internal static void ChurnAcrossCreditCyclesKeepsAdmittingJoiners()
    {
        var scenario = new NatScenario(3120, ttlSeconds: 150) { Renewable = true };
        var host = Host(scenario);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);

        // Forty joiners come and go over several 150-second credential cycles; what they claimed goes as they do.
        var worst = 0;
        for (var index = 0; index < 40; index++)
        {
            var home = scenario.Behind("joiner router " + index, NatBehaviour.FullCone, "198.51." + (101 + (index % 120)) + ".20");
            var joiner = scenario.AddPeer("joiner " + index, home, "192.168.9." + (10 + (index % 200)), 27250, true, 4 + index);
            var peerId = "cc" + index.ToString("x14", System.Globalization.CultureInfo.InvariantCulture);
            var (link, hostSide) = scenario.Connect(host, joiner, scenario.StartJoin(joiner, peerId: peerId, prefix: "198.51." + (101 + (index % 120))));
            That(scenario.Exchange(link, hostSide, (byte)index), "joiner " + index + " plays through the churn");
            worst = Math.Max(worst, host.Agent.Relay?.Scopes ?? 0);
            joiner.Agent.Stop(scenario.Network.Now);
            link.Close();
            scenario.Network.RunFor(20 * Second);
        }

        That(worst <= 3, "the relay never served more than the sessions still running (worst " + worst + " scopes)");
    }

    internal static void HostilePeersTogetherLeaveRoomForTheNextJoiner()
    {
        // Thirty-one hostile identities from one /24, each within its own share: sixteen candidate addresses apiece, its
        // socket closed once its offer is through, so the room holds none of them. With the honest player that is every
        // session the host runs.
        var scenario = new NatScenario(5100);
        scenario.DistinctRelayAddresses();
        scenario.Signaling.Places = 10;
        scenario.Signaling.PerPrefix = 4;
        var host = Host(scenario);
        var first = Joiner(scenario, 0);
        var late = Joiner(scenario, 1);
        scenario.StartHost(host);
        scenario.Network.RunFor(Second);
        var (firstLink, firstHostSide) = scenario.Connect(host, first, scenario.StartJoin(first, peerId: "a1a1a1a1a1a1a1a1", prefix: "198.51.20"));
        var random = new SeededTransportRandom(91);
        for (var identity = 0; identity < 31; identity++) NatHostileSender.OfferAndLeave(scenario, HostileId(identity), "45.1.1", NatHostileSender.Spread(identity), random);
        scenario.Network.RunFor(12 * Second);
        That(host.Agent.Relay!.Scopes == 32 && NatTableQuotaTests.Permissions(host.Agent.Relay) >= 31 * 16, "every hostile identity holds its whole share (" + host.Agent.Relay.Scopes + " scopes)");
        That(scenario.Signaling.OpenPeerSockets == 0 && scenario.Signaling.Refusals == 0, "while the room holds none of them");

        var (lateLink, lateHostSide) = scenario.Connect(host, late, scenario.StartJoin(late, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.21"));
        That(scenario.Exchange(lateLink, lateHostSide, 1), "the next honest joiner still gets in");
        That(scenario.Exchange(firstLink, firstHostSide, 2), "and the first still plays");
    }

    internal static void HostilePortsLeaveRoomForTheNextJoiner()
    {
        // Thirty-one hostile identities each name twenty-four ports on one address, so the host checks, and claims a path
        // for, every one of them.
        var scenario = new NatScenario(5500);
        scenario.DistinctRelayAddresses();
        var host = Host(scenario);
        var late = Joiner(scenario, 1);
        scenario.StartHost(host);
        scenario.Network.RunFor(Second);
        var random = new SeededTransportRandom(95);
        for (var identity = 0; identity < 31; identity++)
        {
            var ports = Enumerable.Range(0, 24).Select(index => new SignalCandidate(IceCandidateKind.ServerReflexive, "45.200.0.1", 40000 + (identity * 24) + index, 1000u)).ToList();
            NatHostileSender.OfferAndLeave(scenario, HostileId(identity), "45.5.5", ports.Take(16).ToList(), random, ports.Skip(16).ToList());
        }

        scenario.Network.RunFor(12 * Second);
        That(NatTableQuotaTests.Peers(host.Agent.Relay!) >= 31 * 24, "every hostile identity holds its whole share of paths (" + NatTableQuotaTests.Peers(host.Agent.Relay!) + ")");
        var (link, hostSide) = scenario.Connect(host, late, scenario.StartJoin(late, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.21"));
        That(scenario.Exchange(link, hostSide, 1), "and the next honest joiner still gets in");
    }

    internal static void WarmHostilePeersNeverCutAPlayerOffAtItsRotation()
    {
        // Production credentials. A player joins; then thirty-one hostile identities take every other session and keep it
        // warm with checks. The player's relay rotates onto an address of its own, which needs a fresh permission on the
        // host's allocation, and the host's own rotation rebuilds that allocation's tables.
        var scenario = new NatScenario(5650, ttlSeconds: 600) { Renewable = true };
        scenario.DistinctRelayAddresses();
        var host = Host(scenario);
        var player = Joiner(scenario, 0);
        var late = Joiner(scenario, 1);
        var attacker = NatHostileSender.On(scenario, "45.200.0.1");
        scenario.StartHost(host);
        scenario.Network.RunFor(100 * Second);
        var (link, hostSide) = scenario.Connect(host, player, scenario.StartJoin(player, peerId: "a1a1a1a1a1a1a1a1", prefix: "198.51.20"));
        var random = new SeededTransportRandom(96);
        for (var identity = 0; identity < 31; identity++)
        {
            var ice = NatHostileSender.OfferAndLeave(scenario, HostileId(identity), "45.6.6", NatHostileSender.Spread(identity, own: "45.200.0.1", ownPort: 41000 + identity), random);
            attacker.Keep(host, HostileId(identity), ice, 41000 + identity, 10 * Second);
        }

        var began = scenario.Network.Now;
        var stalls = 0;
        while (scenario.Network.Now - began < 14 * 60 * Second)
        {
            scenario.Network.RunFor(5 * Second);
            if (!scenario.Exchange(link, hostSide, 3, 2 * Second)) stalls++;
        }

        That(player.Agent.Status.Rotations >= 1 && host.Agent.Status.Rotations >= 1 && attacker.ChecksSent > 31 * 60, "both relays rotated under the warm hostile sessions");
        That(!link.IsClosed && !hostSide.IsClosed && stalls == 0, "the player never lost its connection, nor missed an exchange (" + stalls + " stalls)");
        var (lateLink, lateHostSide) = scenario.Connect(host, late, scenario.StartJoin(late, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.21"));
        That(scenario.Exchange(lateLink, lateHostSide, 4), "and a late honest joiner gets in, a warm hostile session making way");
    }

    internal static void IdentityChurnLeavesRoomForTheNextJoiner()
    {
        // Forty-eight identities from one /24, one every five seconds, inside the control plane's twenty tickets a minute:
        // each offers sixteen addresses and leaves. What an identity claimed goes when its session ends, however recent.
        var scenario = new NatScenario(5200);
        scenario.DistinctRelayAddresses();
        scenario.Signaling.Places = 10;
        scenario.Signaling.PerPrefix = 4;
        var host = Host(scenario);
        var late = Joiner(scenario, 1);
        scenario.StartHost(host);
        scenario.Network.RunFor(Second);
        var random = new SeededTransportRandom(92);
        for (var identity = 0; identity < 48; identity++)
        {
            NatHostileSender.OfferAndLeave(scenario, HostileId(identity), "45.2.2", NatHostileSender.Spread(identity), random);
            scenario.Network.RunFor(5 * Second);
        }

        var live = host.Agent.Sessions.Count(session => session.State != NatSessionState.Ended && session.State != NatSessionState.Failed);
        That(host.Agent.Relay!.Scopes == live && NatTableQuotaTests.Permissions(host.Agent.Relay) <= live * 16,
            "the relay holds only what live sessions claim (" + host.Agent.Relay.Scopes + " scopes, " + live + " sessions, " + NatTableQuotaTests.Permissions(host.Agent.Relay) + " permissions)");
        var (link, hostSide) = scenario.Connect(host, late, scenario.StartJoin(late, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.21"));
        That(scenario.Exchange(link, hostSide, 1), "and the next honest joiner gets in while the churn is still fresh");
    }

    internal static void ASharedRelayAddressStaysWhileAnyPeerUsesIt()
    {
        // Every relay here shares one address, as relays may. A hostile peer names it first, then claims past its share.
        var scenario = new NatScenario(5300);
        var host = Host(scenario);
        var player = Joiner(scenario, 0);
        scenario.StartHost(host);
        scenario.Network.RunFor(2 * Second);
        var hostile = scenario.Signaling.ConnectPeer(HostileId(0), false, "45.3.3");
        scenario.Network.RunFor(100_000);
        hostile.TrySend(SignalMessage.Offer(IceCredentials.Generate(new SeededTransportRandom(93)), new List<SignalCandidate> { new(IceCandidateKind.Relayed, NatScenario.ServerAddress.ToString(), 60000, 1000u) }));
        scenario.Network.RunFor(Second);
        var (link, hostSide) = scenario.Connect(host, player, scenario.StartJoin(player, peerId: "a1a1a1a1a1a1a1a1", prefix: "198.51.20"));
        That(scenario.Exchange(link, hostSide, 1), "the player plays through a relay on the shared address");
        for (var index = 0; index < 16; index++)
        {
            hostile.TrySend(SignalMessage.Trickle(null, new SignalCandidate(IceCandidateKind.ServerReflexive, "45.30." + index + ".1", 40000, 1000u)));
            scenario.Network.RunFor(50_000);
        }

        scenario.Network.RunFor(Second);
        That(host.Agent.Relay!.IsPermitted(NatScenario.ServerAddress), "the shared address stays permitted while the player claims it");
        var flowed = 0;
        for (var round = 0; round < 6; round++)
        {
            scenario.Network.RunFor(5 * Second);
            flowed += scenario.Exchange(link, hostSide, (byte)(10 + round), 2 * Second) ? 1 : 0;
        }

        That(flowed == 6 && !link.IsClosed && !hostSide.IsClosed, "and the player keeps playing for thirty seconds after (" + flowed + " of 6 exchanges)");
    }

    internal static void HostileChecksNeverCrowdOutAnHonestJoiner()
    {
        // Thirty-one hostile identities name twenty-four ports each on the attacker's own address and never answer the
        // host's checks, but send one of their own from every port each second, so the host checks each port again as soon
        // as its last check gives up. The relay's allocation lasts a minute here, so it must refresh twice a minute.
        var scenario = new NatScenario(5850, ttlSeconds: 1800);
        scenario.DistinctRelayAddresses();
        scenario.Turn.LifetimeSeconds = 60;
        var host = Host(scenario);
        var first = Joiner(scenario, 0);
        var late = Joiner(scenario, 1);
        var attacker = NatHostileSender.On(scenario, "45.200.0.1");
        scenario.StartHost(host);
        scenario.Network.RunFor(Second);
        var (firstLink, firstHostSide) = scenario.Connect(host, first, scenario.StartJoin(first, peerId: "a1a1a1a1a1a1a1a1", prefix: "198.51.20"));
        var random = new SeededTransportRandom(99);
        for (var identity = 0; identity < 31; identity++)
        {
            var ports = Enumerable.Range(0, 24).Select(index => new SignalCandidate(IceCandidateKind.ServerReflexive, "45.200.0.1", 42000 + (identity * 24) + index, 1000u)).ToList();
            var ice = NatHostileSender.OfferAndLeave(scenario, HostileId(identity), "45.8." + (identity / 4), ports.Take(16).ToList(), random, ports.Skip(16).ToList());
            foreach (var port in ports) attacker.Keep(host, HostileId(identity), ice, port.Port, Second);
        }

        var refreshes = host.Agent.Relay!.Statistics.Refreshes;
        var most = 0;
        var mostForOne = 0;
        ConnectAttempt? attempt = null;
        var began = scenario.Network.Now;
        while (scenario.Network.Now - began < 120 * Second)
        {
            scenario.Network.RunFor(250_000);
            most = Math.Max(most, host.Agent.Stun.Outstanding);
            foreach (var session in host.Agent.Sessions) mostForOne = Math.Max(mostForOne, session.Checker.Pairs.Count(pair => pair.Check != null));
            if (attempt == null && scenario.Network.Now - began >= 30 * Second) attempt = scenario.StartJoin(late, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.21");
        }

        That(mostForOne <= 8, "no session waited on more than eight checks at once (" + mostForOne + ")");
        That(most <= NatAgent.CheckShares + 32, "so the STUN client stayed far below its limit (" + most + " of " + provider::TopiaForge.Multiplayer.NatTraversal.Stun.StunClient.MaximumOutstanding + ")");
        That(host.Agent.RelayUp && host.Agent.Relay!.Statistics.Refreshes >= refreshes + 3, "the relay kept refreshing its allocation throughout");
        That(attempt!.State == ConnectAttemptState.Established && !firstLink.IsClosed && scenario.Exchange(firstLink, firstHostSide, 1),
            "a late honest joiner got in, and the first still plays (" + attempt.State + ", " + late.Agent.Status + ")");
    }

    internal static void ASessionStillCheckingNeverMakesWay()
    {
        // Thirty-two identities offer at once, so every session the host runs is still within its checks.
        var scenario = new NatScenario(5750);
        scenario.DistinctRelayAddresses();
        var host = Host(scenario);
        scenario.StartHost(host);
        scenario.Network.RunFor(Second);
        var random = new SeededTransportRandom(98);
        var sockets = Enumerable.Range(0, 33).Select(identity => scenario.Signaling.ConnectPeer(HostileId(identity), false, "45.9." + identity)).ToList();
        scenario.Network.RunFor(100_000);
        for (var identity = 0; identity < 32; identity++) sockets[identity].TrySend(SignalMessage.Offer(IceCredentials.Generate(random), NatHostileSender.Spread(identity, 1)));
        scenario.Network.RunFor(500_000);
        var first = host.Agent.Sessions.ToList();
        That(LiveSessions(host) == 32 && first.Count == 32, "every session the host may run is taken (" + LiveSessions(host) + ")");

        // A thirty-third offer gets nothing while each of them is still checking, and pushes none of them out.
        sockets[32].TrySend(SignalMessage.Offer(IceCredentials.Generate(random), NatHostileSender.Spread(32, 1)));
        scenario.Network.RunFor(500_000);
        That(first.All(session => session.State != NatSessionState.Ended && session.State != NatSessionState.Failed) && host.Agent.Sessions.All(session => session.PeerId != HostileId(32)),
            "a session still within its checks never makes way");

        // Once their checks have run out with nothing found, the oldest makes way for the next offer.
        scenario.Network.RunFor(11 * Second);
        sockets[32].TrySend(SignalMessage.Offer(IceCredentials.Generate(random), NatHostileSender.Spread(32, 1)));
        scenario.Network.RunFor(500_000);
        That(host.Agent.Sessions.Any(session => session.PeerId == HostileId(32) && session.State != NatSessionState.Ended) && first[0].State == NatSessionState.Ended && LiveSessions(host) == 32,
            "the oldest made way once its checks ran out");
    }

    internal static void AnAllocationBeingReplacedGivesUpAnEndedSessionsClaims()
    {
        // Make before break: while the host's old allocation still carries the connections, one of its sessions ends.
        var scenario = new NatScenario(5900, ttlSeconds: 150) { Renewable = true };
        var host = Host(scenario);
        var player = Joiner(scenario, 0);
        var leaving = Joiner(scenario, 1);
        scenario.StartHost(host);
        scenario.Network.RunFor(Second);
        var (link, hostSide) = scenario.Connect(host, player, scenario.StartJoin(player, peerId: "a1a1a1a1a1a1a1a1", prefix: "198.51.20"));
        scenario.Connect(host, leaving, scenario.StartJoin(leaving, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.21"));
        That(scenario.Network.RunUntil(() => Retiring(host) != null, 120 * Second), "the host's relay rotates, making before it breaks");
        var retiring = Retiring(host)!;
        var session = host.Agent.Sessions.Single(candidate => candidate.PeerId == "b2b2b2b2b2b2b2b2");
        That(NatTableQuotaTests.Claims(retiring, session.Scope).Permissions > 0, "the session holds claims on the allocation being replaced");
        session.End(NatFailure.None, scenario.Network.Now, false);
        That(NatTableQuotaTests.Claims(retiring, session.Scope) == (0, 0) && NatTableQuotaTests.Claims(host.Agent.Relay!, session.Scope) == (0, 0),
            "both allocations give up what it claimed the moment it ends");
        That(scenario.Exchange(link, hostSide, 1, 10 * Second), "and the other player plays on");
    }

    internal static void WarmSessionsNeverKeepAJoinerOut()
    {
        // Thirty-two hostile identities take every session the host may run and keep each warm with checks.
        var scenario = new NatScenario(5700);
        scenario.DistinctRelayAddresses();
        var host = Host(scenario);
        var late = Joiner(scenario, 1);
        var attacker = NatHostileSender.On(scenario, "45.200.0.1");
        scenario.StartHost(host);
        scenario.Network.RunFor(Second);
        var random = new SeededTransportRandom(97);
        for (var identity = 0; identity < NatAgent.MaximumSessions; identity++)
        {
            var ice = NatHostileSender.OfferAndLeave(scenario, HostileId(identity), "45.7." + (identity / 16), new List<SignalCandidate> { new(IceCandidateKind.ServerReflexive, "45.200.0.1", 41000 + identity, 1000u) }, random);
            attacker.Keep(host, HostileId(identity), ice, 41000 + identity, 10 * Second);
            scenario.Network.RunFor(2 * Second);
        }

        scenario.Network.RunFor(60 * Second);
        That(LiveSessions(host) == 32, "every session the host may run is a warm hostile one (" + LiveSessions(host) + ")");
        var (link, hostSide) = scenario.Connect(host, late, scenario.StartJoin(late, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.21"));
        That(scenario.Exchange(link, hostSide, 1) && LiveSessions(host) == 32, "and an honest joiner still gets in, the oldest of them making way");
    }

    private static NatPeerNode Host(NatScenario scenario) =>
        scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);

    private static NatPeerNode Joiner(NatScenario scenario, int index) =>
        scenario.AddPeer("joiner " + index, scenario.Behind("joiner router " + index, NatBehaviour.FullCone, "198.51." + (20 + index) + ".20"), "192.168." + (20 + index) + ".20", 27250, true, 2 + index);

    private static string HostileId(int index) => "ee" + index.ToString("x14", System.Globalization.CultureInfo.InvariantCulture);

    private static TurnClient? Retiring(NatPeerNode host) =>
        (TurnClient?)typeof(NatAgent).GetField("retiring", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(host.Agent);

    private static int LiveSessions(NatPeerNode host) => host.Agent.Sessions.Count(session => session.State != NatSessionState.Ended && session.State != NatSessionState.Failed);

    /// <summary>What the live sessions answering one signaling peer claim on the host's relay.</summary>
    private static (int Permissions, int Paths) ClaimedBy(NatPeerNode host, string peerId)
    {
        var permissions = 0;
        var paths = 0;
        foreach (var session in host.Agent.Sessions)
        {
            if (session.PeerId != peerId || session.State == NatSessionState.Ended || session.State == NatSessionState.Failed) continue;
            var claims = NatTableQuotaTests.Claims(host.Agent.Relay!, session.Scope);
            permissions += claims.Permissions;
            paths += claims.Paths;
        }

        return (permissions, paths);
    }
}
