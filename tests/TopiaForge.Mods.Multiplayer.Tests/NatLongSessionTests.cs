extern alias provider;

using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Long relay-only sessions on one TURN port, so every rotation breaks before it makes, on relays that give every
/// allocation an address of its own, so a new relay candidate reaches the other side only over signaling: a joiner
/// whose network drops for four seconds every minute for 35 minutes, and five joiners through half an hour of
/// 150-second credentials. The joiners arrive between the host's rotations, so the host's and their own come apart, and
/// every one of the host's stalls their connections. Each stays connected: a connected joiner reopens its socket
/// whenever its connection needs it, held back by nothing but spacing and backoff.
/// </summary>
internal static class NatLongSessionTests
{
    private const long Second = 1_000_000;

    internal static void AJoinerWhoseNetworkKeepsDroppingStaysConnected()
    {
        var scenario = new NatScenario(1300, ttlSeconds: 600) { Renewable = true, SecondTurnPort = false };
        scenario.DistinctRelayAddresses();
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        var joiner = scenario.AddPeer("joiner", scenario.Behind("joiner router", NatBehaviour.FullCone, "198.51.100.20"), "192.168.2.20", 27250, true, 2);
        scenario.StartHost(host);
        scenario.Network.RunFor(255 * Second);
        var (joinerLink, hostLink) = scenario.Connect(host, joiner, scenario.StartJoin(joiner));

        var dropUntil = 0L;
        scenario.Network.Drop = (from, to, _) => scenario.Network.Now < dropUntil && (from == joiner.Node || to == joiner.Node);
        var began = scenario.Network.Now;
        var opens = joiner.Agent.SignalingOpens;
        var drops = 0;
        var tag = (byte)0;
        while (scenario.Network.Now - began < 35 * 60 * Second)
        {
            dropUntil = scenario.Network.Now + (4 * Second);
            drops++;
            for (var step = 0; step < 6; step++)
            {
                scenario.Network.RunFor(10 * Second);
                That(scenario.Exchange(joinerLink, hostLink, ++tag, 8 * Second), "frames still flow at " + ((scenario.Network.Now - began) / Second) + " s");
            }
        }

        That(drops >= 35 && joiner.Agent.SignalingOpens - opens >= drops, "every drop stalled the connection, and the joiner opened its socket for each (" + drops + " drops, " + (joiner.Agent.SignalingOpens - opens) + " openings)");
        That(host.Agent.Status.Rotations >= 3 && joiner.Agent.Status.Rotations >= 3, "both relays rotated, breaking before making on one port (" + host.Agent.Status.Rotations + ", " + joiner.Agent.Status.Rotations + ")");
        That(!joinerLink.IsClosed && !hostLink.IsClosed, "and the connection never dropped");
    }

    internal static void FiveJoinersOnChangingRelaysStayConnected()
    {
        var scenario = new NatScenario(1310, ttlSeconds: 150) { Renewable = true, SecondTurnPort = false };
        scenario.DistinctRelayAddresses();
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        scenario.StartHost(host);
        scenario.Network.RunFor(30 * Second);
        var players = new List<(NatPeerNode Node, NetworkLink Link, NetworkLink HostSide)>();
        for (var index = 0; index < 5; index++)
        {
            var home = scenario.Behind("joiner router " + index, NatBehaviour.FullCone, "198.51.100." + (20 + index));
            var joiner = scenario.AddPeer("joiner " + index, home, "192.168.2." + (20 + index), 27250, true, 2 + index);
            var (link, hostSide) = scenario.Connect(host, joiner, scenario.StartJoin(joiner, peerId: "00000000000000" + (10 + index), prefix: "198.51." + (100 + index)));
            players.Add((joiner, link, hostSide));
        }

        var began = scenario.Network.Now;
        var tag = (byte)0;
        while (scenario.Network.Now - began < 30 * 60 * Second)
        {
            scenario.Network.RunFor(30 * Second);
            tag++;
            for (var index = 0; index < players.Count; index++)
            {
                That(scenario.Exchange(players[index].Link, players[index].HostSide, tag, 8 * Second),
                    "joiner " + index + " still plays at " + ((scenario.Network.Now - began) / Second) + " s, after " + host.Agent.Status.Rotations + " host rotations");
            }
        }

        That(host.Agent.Status.Rotations >= 25, "the host's relay rotated all along (" + host.Agent.Status.Rotations + ")");
        That(players.All(player => player.Node.Agent.Status.Rotations >= 25 && !player.Link.IsClosed && !player.HostSide.IsClosed),
            "and every joiner's, and none of the five connections ever dropped");
    }
}
