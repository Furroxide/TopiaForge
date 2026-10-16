extern alias provider;

using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A session started again on the same agent and socket signals as the first did: a host that stopped and hosts again
/// within its registration answers the next joiner, relay-only and direct, and a player whose join ended joins again.
/// </summary>
internal static class NatRestartTests
{
    internal static void ARestartedHostAnswersTheNextJoiner()
    {
        foreach (var (relayOnly, seed) in new[] { (true, 1200), (false, 1201) })
        {
            var name = relayOnly ? "relay-only" : "direct";
            var scenario = new NatScenario(seed);
            var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, relayOnly, 1);
            var first = scenario.AddPeer("joiner a", scenario.Behind("joiner a router", NatBehaviour.FullCone, "198.51.100.20"), "192.168.2.20", 27250, relayOnly, 2);
            var second = scenario.AddPeer("joiner b", scenario.Behind("joiner b router", NatBehaviour.FullCone, "198.51.100.30"), "192.168.3.30", 27250, relayOnly, 3);
            scenario.StartHost(host);
            scenario.Network.RunFor(500_000);
            var (firstLink, firstHostSide) = scenario.Connect(host, first, scenario.StartJoin(first, peerId: "a1a1a1a1a1a1a1a1"));
            That(scenario.Exchange(firstLink, firstHostSide, 1), name + ": the first joiner plays");

            // The host stops hosting, as when its NatHost is disposed, and hosts again on the same agent and socket.
            host.Agent.Stop(scenario.Network.Now);
            scenario.Network.RunFor(2_000_000);
            var opens = host.Agent.SignalingOpens;
            scenario.StartHost(host);
            scenario.Network.RunFor(500_000);
            var attempt = scenario.StartJoin(second, peerId: "b2b2b2b2b2b2b2b2");
            var (secondLink, secondHostSide) = scenario.Connect(host, second, attempt);
            That(host.Agent.SignalingOpens == opens + 1 && host.Agent.Status.Sessions == 1,
                name + ": the restarted host read its new socket and answered the second joiner (" + host.Agent.Status + ")");
            That(scenario.Exchange(secondLink, secondHostSide, 2), name + ": and frames flow");
            That(host.Agent.RelayOnly == relayOnly && (relayOnly || host.Agent.SocketBase != null), name + ": hosting as the first session did");
            That(Gatherings(host) == 2, name + ": and it gathered afresh before answering (" + Gatherings(host) + " gatherings)");
        }
    }

    internal static void APlayerJoinsAgainOnTheSameAgent()
    {
        foreach (var (relayOnly, seed) in new[] { (true, 1210), (false, 1211) })
        {
            var name = relayOnly ? "relay-only" : "direct";
            var (scenario, host, joiner, joinerLink, _) = NatMatrixTests.Session(seed, NatBehaviour.FullCone, NatBehaviour.FullCone, relayOnly, relayOnly);

            // The player leaves, and its join ends; the host forgets the session.
            joiner.Agent.Stop(scenario.Network.Now);
            joinerLink.Close();
            That(scenario.Network.RunUntil(() => host.Agent.Status.Sessions == 0, 90_000_000), name + ": the host lets the first session go (" + host.Agent.Status + ")");

            // The same player joins again with the same ticket, on the same agent and socket.
            var opens = joiner.Agent.SignalingOpens;
            var (link, hostSide) = scenario.Connect(host, joiner, scenario.StartJoin(joiner));
            That(joiner.Agent.SignalingOpens > opens && joiner.Agent.Status.Stage == NatJoinStage.Connected,
                name + ": the second join signaled and connected (" + joiner.Agent.Status + ")");
            That(scenario.Exchange(link, hostSide, 3), name + ": and frames flow");
            That(Gatherings(joiner) == 2, name + ": after gathering afresh (" + Gatherings(joiner) + " gatherings)");
        }
    }

    private static int Gatherings(NatPeerNode peer) => peer.Diagnostics.Reports.Count(report => report.Kind == NatDiagnosticKind.GatheringComplete);
}
