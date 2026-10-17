extern alias provider;

using System.Collections;
using System.Net;
using System.Reflection;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A session started again on the same agent and socket signals as the first did: a host that stopped and hosts again
/// within its registration answers the next joiner, relay-only and direct, and a player whose join ended joins again.
/// Starting again also forgets everything the last session learned or decided, so none of it skews the next: the
/// reconnection window, the relay's own state, and the credential timers all begin afresh.
/// </summary>
internal static class NatRestartTests
{
    private const long Second = 1_000_000;

    // R1c: the six-in-ten-minutes reconnection window the first session consumed must not carry into the next, or a
    // restarted host or joiner would give up signaling after fewer openings than it is owed.
    internal static void ARestartResetsTheReconnectionWindow()
    {
        var scenario = new NatScenario(1220);
        scenario.Signaling.PeerLifetimeMicros = 1_500_000;
        var joiner = scenario.AddPeer("joiner", scenario.Behind("joiner router", NatBehaviour.FullCone, "198.51.100.20"), "192.168.2.20", 27250, false, 2);

        // The host never comes online, so the join keeps needing its socket, which the short lifetime keeps closing:
        // each close, while the join is still connecting, counts against the window.
        scenario.StartJoin(joiner);
        scenario.Network.RunFor(4 * Second);
        That(Window(joiner) > 0, "the first session consumed reconnection-window slots (" + Window(joiner) + ")");

        joiner.Agent.Stop(scenario.Network.Now);
        scenario.Network.RunFor(Second);
        scenario.StartJoin(joiner);
        That(Window(joiner) == 0, "the restarted session begins with a fresh window (" + Window(joiner) + ")");
    }

    // R1d: the relay's own state — its targets, what worked, and the rotation count — must begin afresh on a restart.
    internal static void ARestartResetsRelayState()
    {
        var scenario = new NatScenario(1230, ttlSeconds: 150) { Renewable = true };
        scenario.DistinctRelayAddresses();
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        scenario.StartHost(host);
        That(scenario.Network.RunUntil(() => host.Agent.Status.Rotations >= 1, 200 * Second), "the first session's relay rotates at least once (" + host.Agent.Status.Rotations + ")");

        host.Agent.Stop(scenario.Network.Now);
        scenario.Network.RunFor(Second);
        scenario.StartHost(host);
        That(host.Agent.Status.Rotations == 0, "the restarted session carries over none of the relay's rotations (" + host.Agent.Status.Rotations + ")");
    }

    // R1e: the credential timers and what the server refused — the mint backoff, the "refused since ready" flag and the
    // failed-target list — must begin afresh, or a restart would mint slower or shun a server the last session soured.
    internal static void ARestartResetsCredentialState()
    {
        var scenario = new NatScenario(1240, ttlSeconds: 150) { Renewable = true };
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        scenario.StartHost(host);
        scenario.Network.RunFor(2 * Second);

        // Stand in for the state the first session would accrue: a soured server address, a grown mint backoff, and a
        // credential refused since the relay last came up.
        FailedTargets(host)[UdpPaths.For(new IPEndPoint(NatScenario.ServerAddress, 3478))] = scenario.Network.Now + 1_800_000_000;
        SetLong(host, "renewRetryDelay", 300_000_000);
        SetBool(host, "refusedSinceReady", false);

        host.Agent.Stop(scenario.Network.Now);
        scenario.Network.RunFor(Second);
        scenario.StartHost(host);
        That(FailedTargets(host).Count == 0, "the restarted session forgets soured server addresses (" + FailedTargets(host).Count + ")");
        That(GetLong(host, "renewRetryDelay") == 15_000_000, "and resets the mint backoff (" + GetLong(host, "renewRetryDelay") + ")");
        That(GetBool(host, "refusedSinceReady"), "and resets the refusal flag");
    }

    private static int Window(NatPeerNode peer) => (int)Field(peer, "reconnectsInWindow")!;

    private static IDictionary FailedTargets(NatPeerNode peer) => (IDictionary)Field(peer, "failedTargets")!;

    private static long GetLong(NatPeerNode peer, string name) => (long)Field(peer, name)!;

    private static bool GetBool(NatPeerNode peer, string name) => (bool)Field(peer, name)!;

    private static void SetLong(NatPeerNode peer, string name, long value) => FieldInfo(name).SetValue(peer.Agent, value);

    private static void SetBool(NatPeerNode peer, string name, bool value) => FieldInfo(name).SetValue(peer.Agent, value);

    private static object? Field(NatPeerNode peer, string name) => FieldInfo(name).GetValue(peer.Agent);

    private static FieldInfo FieldInfo(string name) => typeof(NatAgent).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

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
