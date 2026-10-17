extern alias provider;

using System.Net;
using System.Net.Sockets;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A relay that is lost comes back. A rotation whose second address never answers falls back to the address that
/// worked; a lost release is sent again before anything allocates on its 5-tuple, and a 437 sends it once more; a
/// refused first allocation is retried; credentials keep being minted while the relay is down; a rotation keeps the
/// address family that worked; and a long session survives repeated outages. The sessions are relay-only on both
/// sides with credentials lasting 150 seconds, so a relay that stays down ends them.
/// </summary>
internal static class NatRelayRecoveryTests
{
    private const long Second = 1_000_000;
    private static readonly IPAddress HostRouter = IPAddress.Parse("198.51.100.10");

    internal static void ARotationFallsBackWhenTheSecondPortNeverAnswers()
    {
        foreach (var (lifts, seed) in new[] { (true, 1000), (false, 1001) })
        {
            var name = lifts ? "a second port blocked until 72 s" : "a second port that never answers";
            var (scenario, host, joiner, joinerLink, hostLink) = RelayOnlySession(seed, setup =>
            {
                setup.SecondTurnPort = true;
                setup.Router.Blackhole = to => to.Port == 3479 && (!lifts || setup.Network.Now < 72 * Second);
            });
            KeepsFlowing(name, scenario, joinerLink, hostLink, 14);
            That(scenario.Router.Blackholed > 0, name + ": a rotation tried the second port and lost it");
            Survived(name, scenario, host, joiner, joinerLink, hostLink);
        }
    }

    internal static void ALostReleaseIsSentAgainBeforeTheNextAllocation()
    {
        // One server address, so every rotation breaks before it makes. The first copy of each side's first release is lost.
        var lost = new HashSet<string>();
        var (scenario, host, joiner, joinerLink, hostLink) = RelayOnlySession(1010, setup =>
        {
            setup.SecondTurnPort = false;
            setup.Turn.LoseRequest = (from, bytes) => IsRelease(bytes) && lost.Add(NatAddresses.Format(from.Address));
        });
        KeepsFlowing("a lost release", scenario, joinerLink, hostLink, 14);
        That(lost.Count == 2, "each side's first release was lost on its way (" + lost.Count + ")");
        That(scenario.Turn.Releases >= host.Agent.Status.Rotations + joiner.Agent.Status.Rotations,
            "and every release reached the server in the end (" + scenario.Turn.Releases + " for " + (host.Agent.Status.Rotations + joiner.Agent.Status.Rotations) + " rotations)");
        That(scenario.Turn.Mismatches == 0, "so no allocation ever met the one it replaced (" + scenario.Turn.Mismatches + ")");
        Survived("a lost release", scenario, host, joiner, joinerLink, hostLink);
    }

    internal static void AnAllocationMeetingAStaleOneReleasesItAgain()
    {
        // Every copy of the host's first release is lost, so the server still holds that allocation when the next one
        // asks for the same 5-tuple: it meets a 437, the old release goes again, and the relay comes back.
        var (scenario, host, joiner, _, _) = RelayOnlySession(1020, setup =>
        {
            setup.SecondTurnPort = false;
            setup.Turn.LoseRequest = (from, bytes) => setup.Turn.Mismatches == 0 && IsRelease(bytes) && from.Address.Equals(HostRouter);
        });
        That(scenario.Network.RunUntil(() => scenario.Turn.Mismatches > 0, 120 * Second), "the host's next allocation met the one whose release was lost");
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 15 * Second), "and the relay came back once the release went again (" + host.Agent.Status + ")");
        scenario.Network.RunFor(5 * Second);
        That(scenario.Turn.Allocations.Count == 2, "the stale allocation is gone: one per player (" + scenario.Turn.Allocations.Count + ")");
        That(joiner.Agent.RelayUp, "the joiner's relay is unaffected");
    }

    internal static void ARefusedFirstAllocationIsRetried()
    {
        // A relay-only host's first authenticated Allocate meets 508 (the server is at capacity) once.
        var scenario = new NatScenario(1030);
        scenario.Turn.AllocateErrors.Enqueue(508);
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        var joiner = scenario.AddPeer("joiner", scenario.Behind("joiner router", NatBehaviour.FullCone, "198.51.100.20"), "192.168.2.20", 27250, true, 2);
        scenario.StartHost(host);
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 10 * Second), "the host's relay comes up after the refusal (" + host.Agent.Status + ")");
        That(host.Agent.AllocationsStarted == 2 && scenario.Turn.AllocateErrors.Count == 0, "on its second attempt (" + host.Agent.AllocationsStarted + ")");
        var attempt = scenario.StartJoin(joiner);
        var (joinerLink, hostLink) = scenario.Connect(host, joiner, attempt);
        That(scenario.Exchange(joinerLink, hostLink, 1), "and a joiner connects through both relays");
    }

    internal static void CredentialsAreMintedWhileTheRelayIsDown()
    {
        var scenario = new NatScenario(1040, ttlSeconds: 150) { Renewable = true };
        scenario.Turn.LifetimeSeconds = 60;
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        scenario.StartHost(host);
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 5 * Second), "the relay comes up");

        // The relay goes quiet for four minutes, longer than a credential lasts: the refresh times out and the relay is lost.
        scenario.Turn.Silent = true;
        That(scenario.Network.RunUntil(() => !host.Agent.RelayUp, 60 * Second), "the relay is lost");
        var minted = scenario.Mints;
        scenario.Network.RunFor(240 * Second);
        That(scenario.Mints >= minted + 3, "fresh credentials kept being minted while it was down (" + (scenario.Mints - minted) + ")");
        That(!host.Agent.RelayUp && host.Agent.AllocationsStarted > 4, "and allocations kept being tried (" + host.Agent.AllocationsStarted + ")");
        scenario.Turn.Silent = false;
        That(scenario.Network.RunUntil(() => host.Agent.RelayUp, 40 * Second), "the relay comes back once the server answers (" + host.Agent.Status + ")");
        var user = scenario.Turn.Allocations.Single().User;
        That(user == "topiaforge-" + scenario.Mints, "under the credential minted last, the old ones having expired (" + user + ")");
    }

    internal static void TargetsKeepTheFamilyThatWorkedAndEachServersOwnName()
    {
        var firstV4 = IPAddress.Parse("203.0.113.1");
        var firstV6 = IPAddress.Parse("2001:db8::1");
        var secondV4 = IPAddress.Parse("203.0.113.2");
        var set = IceServerSet.Create(Array.Empty<IceServerAddress>(),
            new[] { new IceServerAddress("a.example.test", 3478), new IceServerAddress("b.example.test", 53), new IceServerAddress("a.example.test", 3479) }, "user", "secret", 600);
        var asked = new List<string>();
        var servers = new RelayServers(set, host =>
        {
            asked.Add(host);
            return Task.FromResult(host == "a.example.test" ? new[] { firstV6, firstV4 } : new[] { secondV4 });
        });
        That(servers.IsCompleted && asked.SequenceEqual(new[] { "a.example.test", "b.example.test" }), "each server's own name is looked up, once");
        var v4First = Describe(servers.Targets(null, false));
        That(v4First.SequenceEqual(new[] { "203.0.113.1:3478", "203.0.113.2:53", "203.0.113.1:3479", "[2001:db8::1]:3478", "[2001:db8::1]:3479" }),
            "IPv4 first before anything worked, servers in order, each with its own name's addresses (" + string.Join(", ", v4First) + ")");
        var v6First = Describe(servers.Targets(AddressFamily.InterNetworkV6, false));
        That(v6First.SequenceEqual(new[] { "[2001:db8::1]:3478", "[2001:db8::1]:3479", "203.0.113.1:3478", "203.0.113.2:53", "203.0.113.1:3479" }),
            "the family that worked comes first (" + string.Join(", ", v6First) + ")");
        var failed = new RelayServers(set, host => host == "b.example.test" ? Task.FromException<IPAddress[]>(new SocketException(11001)) : null);
        That(failed.IsCompleted && failed.Targets(null, false).Count == 0, "names that do not resolve give no address, and nothing throws");
    }

    internal static void ARotationNeverMovesToTheOtherFamily()
    {
        // DNS lists an IPv6 address first, which goes nowhere here; the relay starts on IPv4, and every rotation stays there,
        // on the second port when there is one, and breaking before it makes when there is not.
        foreach (var (secondPort, seed) in new[] { (true, 1060), (false, 1061) })
        {
            var name = "an IPv6 address listed first" + (secondPort ? "" : ", one port");
            var (scenario, host, joiner, joinerLink, hostLink) = RelayOnlySession(seed, setup =>
            {
                setup.SecondTurnPort = secondPort;
                setup.TurnAddresses = new[] { IPAddress.Parse("2001:db8::1"), NatScenario.ServerAddress };
            });
            KeepsFlowing(name, scenario, joinerLink, hostLink, 8);
            That(host.SentTo.Concat(joiner.SentTo).All(target => NatAddresses.Unmap(target.Address).AddressFamily == AddressFamily.InterNetwork),
                name + ": no datagram ever went to the IPv6 address");
            Survived(name, scenario, host, joiner, joinerLink, hostLink);
        }
    }

    internal static void ALongSessionSurvivesRepeatedOutages()
    {
        var (scenario, host, joiner, joinerLink, hostLink) = RelayOnlySession(1050, setup => setup.SecondTurnPort = true);
        var blockedPort = 0;
        var blockedUntil = 0L;
        scenario.Router.Blackhole = to => to.Port == blockedPort && scenario.Network.Now < blockedUntil;
        var releasesToLose = 0;
        scenario.Turn.LoseRequest = (_, bytes) => IsRelease(bytes) && releasesToLose-- > 0;
        var start = scenario.Network.Now;
        var blocks = 0;
        var round = 0;
        while (scenario.Network.Now - start < 30 * 60 * Second)
        {
            // Every few minutes something goes wrong: the relay goes quiet for four seconds, the server port neither
            // allocation uses stops answering for two minutes (where the next rotation would go), the next release is
            // lost, or the next allocation is refused.
            switch (round % 8)
            {
                case 1:
                    scenario.Turn.Silent = true;
                    scenario.Network.RunFor(4 * Second);
                    scenario.Turn.Silent = false;
                    break;
                case 3:
                    var inUse = new[] { ServerPort(host), ServerPort(joiner) };
                    blockedPort = inUse.Contains(3478) ? (inUse.Contains(3479) ? 0 : 3479) : 3478;
                    blockedUntil = scenario.Network.Now + (120 * Second);
                    blocks += blockedPort == 0 ? 0 : 1;
                    break;
                case 5:
                    releasesToLose = 1;
                    break;
                case 7:
                    scenario.Turn.AllocateErrors.Enqueue(508);
                    break;
            }

            scenario.Network.RunFor(30 * Second);
            That(scenario.Exchange(joinerLink, hostLink, (byte)round, 8 * Second), "frames still flow at " + ((scenario.Network.Now - start) / Second) + " s");
            round++;
        }

        That(blocks >= 3 && scenario.Router.Blackholed > 0 && scenario.Turn.AllocateErrors.Count == 0 && releasesToLose <= 0,
            "every kind of outage happened (" + blocks + " blocks, " + scenario.Router.Blackholed + " datagrams lost to them)");
        That(host.Agent.Status.Rotations >= 20 && joiner.Agent.Status.Rotations >= 20,
            "both relays rotated all along (" + host.Agent.Status.Rotations + ", " + joiner.Agent.Status.Rotations + ")");
        Survived("half an hour of outages", scenario, host, joiner, joinerLink, hostLink);
    }

    /// <summary>Gets the TURN server port a player's current allocation uses.</summary>
    internal static int ServerPort(NatPeerNode peer) =>
        peer.Agent.Relay != null && UdpPaths.TryGetEndPoint(peer.Agent.Relay.Server, out var server) ? server.Port : 0;

    internal static (NatScenario Scenario, NatPeerNode Host, NatPeerNode Joiner, NetworkLink JoinerLink, NetworkLink HostLink) RelayOnlySession(int seed, Action<NatScenario> configure) =>
        NatMatrixTests.Session(seed, NatBehaviour.FullCone, NatBehaviour.FullCone, true, true, setup =>
        {
            setup.Renewable = true;
            configure(setup);
        }, ttlSeconds: 150);

    /// <summary>Runs thirty seconds at a time and checks frames still cross both ways after each.</summary>
    internal static void KeepsFlowing(string name, NatScenario scenario, NetworkLink joinerLink, NetworkLink hostLink, int rounds)
    {
        var start = scenario.Network.Now;
        for (var round = 0; round < rounds; round++)
        {
            scenario.Network.RunFor(30 * Second);
            That(scenario.Exchange(joinerLink, hostLink, (byte)(100 + round), 8 * Second), name + ": frames still flow at " + ((scenario.Network.Now - start) / Second) + " s");
        }
    }

    /// <summary>The links never dropped, both relays rotated, the server holds one allocation per player, and pairs stayed bounded.</summary>
    internal static void Survived(string name, NatScenario scenario, NatPeerNode host, NatPeerNode joiner, NetworkLink joinerLink, NetworkLink hostLink)
    {
        That(!joinerLink.IsClosed && !hostLink.IsClosed, name + ": the connection never dropped");
        That(host.Agent.Status.Rotations >= 2 && joiner.Agent.Status.Rotations >= 2,
            name + ": both relays moved to fresh credentials (" + host.Agent.Status.Rotations + ", " + joiner.Agent.Status.Rotations + ")");
        That(host.Agent.RelayUp && joiner.Agent.RelayUp, name + ": and both are up now");
        scenario.Network.RunFor(10 * Second);
        That(scenario.Turn.Allocations.Count == 2, name + ": the server holds exactly one allocation per player (" + scenario.Turn.Allocations.Count + ")");
        var largest = host.Agent.Sessions.Concat(joiner.Agent.Sessions).Max(session => session.Checker.Pairs.Count);
        That(largest <= 16, name + ": released bases and replaced relay candidates are forgotten, so pairs stay bounded (" + largest + ")");
    }

    /// <summary>Gets whether a datagram is a release: a Refresh asking for a lifetime of zero.</summary>
    internal static bool IsRelease(byte[] bytes) =>
        StunMessage.TryParse(bytes, 0, bytes.Length, out var message) && message.Method == StunMethod.Refresh && message.Class == StunClass.Request &&
        message.TryGetUInt32(StunAttribute.Lifetime, out var lifetime) && lifetime == 0;

    private static List<string> Describe(List<RelayTarget> targets) =>
        targets.Select(target => UdpPaths.TryGetEndPoint(target.Path, out var endpoint) ? Format(endpoint) : "?").ToList();

    private static string Format(IPEndPoint endpoint)
    {
        var address = NatAddresses.Unmap(endpoint.Address);
        return (address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + NatAddresses.Format(address) + "]" : NatAddresses.Format(address)) + ":" + endpoint.Port;
    }
}
