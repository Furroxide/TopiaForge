extern alias provider;

using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The NAT matrix over the virtual network: cone and restricted NATs reach a direct path after starting on the relay,
/// symmetric NATs and carrier-grade NAT stay on the relay, blocked UDP says so, and a direct path that breaks falls back.
/// </summary>
internal static class NatMatrixTests
{
    internal static void ConeAndRestrictedNatsReachADirectPath()
    {
        var cases = new[]
        {
            (NatBehaviour.FullCone, NatBehaviour.FullCone, 300),
            (NatBehaviour.AddressRestricted, NatBehaviour.AddressRestricted, 301),
            (NatBehaviour.PortRestricted, NatBehaviour.PortRestricted, 302),
            (NatBehaviour.FullCone, NatBehaviour.PortRestricted, 303),
            (NatBehaviour.PortRestricted, NatBehaviour.AddressRestricted, 304)
        };
        foreach (var (hostNat, joinerNat, seed) in cases)
        {
            var name = hostNat + " host, " + joinerNat + " joiner";
            var (scenario, host, joiner, joinerLink, hostLink) = Session(seed, hostNat, joinerNat);
            That(joiner.Diagnostics.Saw(NatDiagnosticKind.StartedOnRelay) && !joiner.Diagnostics.Saw(NatDiagnosticKind.StartedDirect), name + ": the join starts on the relay");
            That(scenario.Exchange(joinerLink, hostLink, 1), name + ": frames flow over the relay");
            That(scenario.Network.RunUntil(() => NatScenario.RouteOf(joiner) == NatRoute.Direct && NatScenario.RouteOf(host) == NatRoute.Direct, 15_000_000),
                name + ": both sides upgrade to a direct path (" + NatScenario.RouteOf(joiner) + ", " + NatScenario.RouteOf(host) + ")");
            That(joiner.Diagnostics.Saw(NatDiagnosticKind.UpgradedToDirect) && joiner.Agent.Status.Route == NatRoute.Direct, name + ": the status says direct");
            That(scenario.Exchange(joinerLink, hostLink, 2), name + ": frames flow on the direct path");
            That(host.Agent.Status.DirectSessions == 1 && host.Agent.Status.RelayedSessions == 0, name + ": the host counts one direct session");
        }
    }

    internal static void SymmetricNatAndCgnatStayOnTheRelay()
    {
        foreach (var (hostNat, joinerNat, seed) in new[] { (NatBehaviour.PortRestricted, NatBehaviour.Symmetric, 310), (NatBehaviour.Symmetric, NatBehaviour.Symmetric, 311) })
        {
            var name = hostNat + " host, " + joinerNat + " joiner";
            var (scenario, host, joiner, joinerLink, hostLink) = Session(seed, hostNat, joinerNat);
            StaysRelayed(name, scenario, host, joiner, joinerLink, hostLink);
        }

        // Two players at home behind the same carrier-grade NAT, which does not hairpin: their server-reflexive addresses
        // share the carrier's public address, and their host addresses are each behind a home router.
        var cgnat = new NatScenario(312);
        var carrier = cgnat.Behind("carrier", NatBehaviour.PortRestricted, "198.51.100.100");
        var hostHome = cgnat.Behind("host router", NatBehaviour.PortRestricted, "100.64.0.10", carrier);
        var joinerHome = cgnat.Behind("joiner router", NatBehaviour.PortRestricted, "100.64.0.20", carrier);
        var cgnatHost = cgnat.AddPeer("host", hostHome, "192.168.1.10", 27250, false, 1);
        var cgnatJoiner = cgnat.AddPeer("joiner", joinerHome, "192.168.1.10", 27250, false, 2);
        cgnat.StartHost(cgnatHost);
        cgnat.Network.RunFor(500_000);
        var attempt = cgnat.StartJoin(cgnatJoiner);
        var (link, accepted) = cgnat.Connect(cgnatHost, cgnatJoiner, attempt);
        StaysRelayed("carrier-grade NAT", cgnat, cgnatHost, cgnatJoiner, link, accepted);
        That(cgnat.NatOf(carrier).Filtered + cgnat.Router.Undeliverable > 0, "the carrier NAT refused the hairpin attempts");
    }

    internal static void BlockedUdpGivesAClearStatus()
    {
        var scenario = new NatScenario(320);
        var hostHome = scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10");
        var joinerHome = scenario.Behind("joiner router", NatBehaviour.FullCone, "198.51.100.20");
        scenario.NatOf(joinerHome).BlocksUdp = true;
        var host = scenario.AddPeer("host", hostHome, "192.168.1.10", 27250, false, 1);
        var joiner = scenario.AddPeer("joiner", joinerHome, "192.168.2.20", 27250, false, 2);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        var attempt = scenario.StartJoin(joiner);
        That(scenario.Network.RunUntil(() => attempt.State == ConnectAttemptState.Failed, 15_000_000), "the join fails");
        var status = joiner.Agent.Status;
        That(status.Gathering == NatGathering.UdpBlocked && status.Failure == NatFailure.UdpBlocked && status.Stage == NatJoinStage.Failed,
            "with a status that says UDP is blocked (" + status + ")");
        That(joiner.Diagnostics.Saw(NatDiagnosticKind.UdpBlocked), "and a diagnostic that says so");
        That(scenario.Signaling.Sent.All(frame => frame.Role != provider::TopiaForge.Multiplayer.NatTraversal.Signaling.SignalRole.Peer),
            "no offer went out, since nothing could reach the joiner anyway");

        // A host whose network blocks UDP reports it too.
        var blockedHost = new NatScenario(321);
        var home = blockedHost.Behind("host router", NatBehaviour.FullCone, "198.51.100.10");
        blockedHost.NatOf(home).BlocksUdp = true;
        var lonely = blockedHost.AddPeer("host", home, "192.168.1.10", 27250, false, 1);
        blockedHost.StartHost(lonely);
        blockedHost.Network.RunFor(10_000_000);
        That(lonely.Agent.Status.Gathering == NatGathering.UdpBlocked, "a host behind blocked UDP reports it (" + lonely.Agent.Status + ")");
    }

    internal static void ADirectPathThatBreaksFallsBackToTheRelay()
    {
        var (scenario, host, joiner, joinerLink, hostLink) = Session(330, NatBehaviour.FullCone, NatBehaviour.FullCone);
        That(scenario.Network.RunUntil(() => NatScenario.RouteOf(joiner) == NatRoute.Direct && NatScenario.RouteOf(host) == NatRoute.Direct, 15_000_000), "the session upgrades");

        // The routers between the two homes now drop everything that does not pass the relay.
        scenario.Network.Drop = (from, to, _) => (from == host.Node && to == joiner.Node) || (from == joiner.Node && to == host.Node);
        That(scenario.Network.RunUntil(() => NatScenario.RouteOf(joiner) == NatRoute.Relayed && NatScenario.RouteOf(host) == NatRoute.Relayed, 20_000_000),
            "both sides move back to the relay (" + NatScenario.RouteOf(joiner) + ", " + NatScenario.RouteOf(host) + ")");
        That(joiner.Diagnostics.Saw(NatDiagnosticKind.FellBackToRelay) || host.Diagnostics.Saw(NatDiagnosticKind.FellBackToRelay), "a diagnostic reports the fallback");
        That(!joinerLink.IsClosed && !hostLink.IsClosed, "the connection survived");
        That(scenario.Exchange(joinerLink, hostLink, 3), "and frames flow over the relay again");
    }

    internal static void RelayedSessionsOutliveTheirCredentials()
    {
        foreach (var (secondPort, seed) in new[] { (true, 340), (false, 341) })
        {
            var name = secondPort ? "make before break" : "break before make";
            var (scenario, host, joiner, joinerLink, hostLink) = Session(seed, NatBehaviour.Symmetric, NatBehaviour.Symmetric, configure: setup =>
            {
                setup.Renewable = true;
                setup.SecondTurnPort = secondPort;
            }, ttlSeconds: 150);
            for (var round = 0; round < 14; round++)
            {
                scenario.Network.RunFor(30_000_000);
                That(scenario.Exchange(joinerLink, hostLink, (byte)(10 + round), 8_000_000), name + ": frames still flow at " + (scenario.Network.Now / 1_000_000) + " s");
            }

            That(host.Agent.Status.Rotations >= 2 && joiner.Agent.Status.Rotations >= 2,
                name + ": both relays moved to fresh credentials (" + host.Agent.Status.Rotations + ", " + joiner.Agent.Status.Rotations + ")");
            That(scenario.Turn.Allocations.Count == 2, name + ": the server holds exactly one allocation per player (" + scenario.Turn.Allocations.Count + ")");
            var largest = host.Agent.Sessions.Concat(joiner.Agent.Sessions).Max(session => session.Checker.Pairs.Count);
            That(largest <= 16, name + ": released bases and replaced relay candidates are forgotten, so pairs stay bounded (" + largest + ")");
            That(!joinerLink.IsClosed && !hostLink.IsClosed, name + ": the connection never dropped");
        }
    }

    private static void StaysRelayed(string name, NatScenario scenario, NatPeerNode host, NatPeerNode joiner, NetworkLink joinerLink, NetworkLink hostLink)
    {
        That(joiner.Diagnostics.Saw(NatDiagnosticKind.StartedOnRelay), name + ": the join starts on the relay");
        scenario.Network.RunFor(20_000_000);
        That(NatScenario.RouteOf(joiner) == NatRoute.Relayed && NatScenario.RouteOf(host) == NatRoute.Relayed,
            name + ": twenty seconds on, the session is still relayed (" + NatScenario.RouteOf(joiner) + ", " + NatScenario.RouteOf(host) + ")");
        var directSucceeded = joiner.Agent.Sessions.SelectMany(session => session.Checker.Pairs).Any(pair => !pair.IsRelayed && pair.State == CandidatePairState.Succeeded);
        That(!directSucceeded, name + ": no direct pair ever answered");
        That(scenario.Exchange(joinerLink, hostLink, 4), name + ": frames flow over the relay");
    }

    internal static (NatScenario Scenario, NatPeerNode Host, NatPeerNode Joiner, NetworkLink JoinerLink, NetworkLink HostLink) Session(
        int seed, NatBehaviour hostNat, NatBehaviour joinerNat, bool hostRelayOnly = false, bool joinerRelayOnly = false,
        Action<NatScenario>? configure = null, long ttlSeconds = 600)
    {
        var scenario = new NatScenario(seed, ttlSeconds);
        configure?.Invoke(scenario);
        var hostHome = scenario.Behind("host router", hostNat, "198.51.100.10");
        var joinerHome = scenario.Behind("joiner router", joinerNat, "198.51.100.20");
        var host = scenario.AddPeer("host", hostHome, "192.168.1.10", 27250, hostRelayOnly, 1);
        var joiner = scenario.AddPeer("joiner", joinerHome, "192.168.2.20", 27250, joinerRelayOnly, 2);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        var attempt = scenario.StartJoin(joiner);
        var (joinerLink, hostLink) = scenario.Connect(host, joiner, attempt);
        return (scenario, host, joiner, joinerLink, hostLink);
    }
}
