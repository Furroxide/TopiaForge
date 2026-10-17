extern alias provider;

using System.Net;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Internet sessions end to end over the NAT simulator and the fake TopiaForge service, each player composed as the game
/// composes it and driven through the internal SPI: hosting with an invite, joining by invite code and by server id,
/// relay-only through symmetric NATs, a direct upgrade only when the joiner chose direct play, and a host that quits
/// taking its listing with it.
/// </summary>
internal static class InternetSessionTests
{
    internal static void AnInvitedJoinerReachesTheHostRelayOnlyThroughSymmetricNats()
    {
        using var rig = new InternetRig(7_101);
        var host = rig.AddPeer("host", NatBehaviour.Symmetric, "198.51.100.10", "192.168.1.10");
        var joiner = rig.AddPeer("joiner", NatBehaviour.Symmetric, "198.51.100.20", "10.0.0.20");
        var invite = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite));
        That(invite.Code.Length == 10 && invite.Display == invite.Code.Substring(0, 5) + "-" + invite.Code.Substring(5) && invite.Link == "topiaforge://join/" + invite.Code,
            "the host's status shows its invite");
        var serverId = rig.Service.Sessions.Keys.Single();
        That(!host.Internet.Listed && !rig.Service.IsListed(serverId), "an invite-only session is never in the browser");

        rig.Join(joiner, invite.Display);
        That(rig.Run(() => joiner.Internet.JoinRoute != InternalConnectionRoute.None, 5_000_000), "the joiner's route shows once the NAT layer has placed the connection");
        var status = joiner.Internet;
        That(status.JoinRoute == InternalConnectionRoute.Relayed && !status.HostAllowsDirect, "the joiner plays through the relay, and knows the host hides its address too (" + status.JoinRoute + ", " + status.HostAllowsDirect + ")");
        That(rig.Run(() => host.Internet.RelayedPlayers == 1, 5_000_000) && host.Internet.DirectPlayers == 0, "the host counts one relayed player and no direct one");

        // Relay-only on both sides: nothing either machine sent went anywhere but the relay.
        foreach (var peer in new[] { host, joiner })
        {
            var elsewhere = peer.Factory.SentTo.Where(to => !Plain(to.Address).Equals(NatScenario.ServerAddress)).ToList();
            That(elsewhere.Count == 0, peer.Name + " sends only to the relay (" + elsewhere.Count + " datagrams elsewhere: " + string.Join(", ", elsewhere.Distinct()) + ")");

            // Each layer holds on its own, so neither depends on the other to keep the address hidden.
            That(peer.Factory.Networks.Count > 0 && peer.Factory.Networks.All(network => network.Agent.Options.RelayOnly && network.Stack.Endpoint.Options.RelayOnly),
                peer.Name + "'s NAT agent and transport are each relay-only");
            That(peer.AddressListings == 0, peer.Name + " never listed its own addresses (" + peer.AddressListings + ")");
        }

        That(rig.Scenario.Signaling.Withheld == 0, "the service had nothing to strip: each side withheld its own candidates first");
        That(rig.Service.Count("POST", "/v1/join-tickets") == 1 && rig.Service.Requests.Any(request => request.Path == "/v1/join-tickets" &&
             System.Text.Encoding.UTF8.GetString(request.Body!).Contains("\"relayOnly\":true", StringComparison.Ordinal)), "the ticket asked for carries relay-only");

        // The session is real: the joiner is a participant on the host, and leaving returns it to solo play.
        That(host.Provider.Snapshot.Participants.Count(participant => participant.IsConnected) == 2, "the host sees two players");
        joiner.Control.Leave();
        That(rig.Run(() => joiner.Status.Role == InternalSessionRole.Solo && host.Provider.Snapshot.Participants.Count(participant => participant.IsConnected) == 1, 10_000_000),
            "the joiner leaves and both play on");
    }

    internal static void ADirectUpgradeHappensOnlyWhenTheJoinerChoseIt()
    {
        using var rig = new InternetRig(7_102);
        var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.30", "192.168.1.30");
        var joiner = rig.AddPeer("joiner", NatBehaviour.FullCone, "198.51.100.40", "10.0.0.40");
        var invite = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite) { AllowDirect = true });

        // The host shows its address, but the joiner kept Hide my IP on: the session stays on the relay for good.
        rig.Join(joiner, invite.Code);
        That(rig.Run(() => joiner.Internet.JoinRoute != InternalConnectionRoute.None, 5_000_000) && joiner.Internet.HostAllowsDirect, "the joiner learns the host would take a direct path");
        rig.Run(() => false, 20_000_000);
        That(joiner.Internet.JoinRoute == InternalConnectionRoute.Relayed && host.Internet.RelayedPlayers == 1 && host.Internet.DirectPlayers == 0,
            "a relay-only joiner never upgrades, however long it plays (" + joiner.Internet.JoinRoute + ")");
        That(joiner.Factory.SentTo.All(to => Plain(to.Address).Equals(NatScenario.ServerAddress)), "and sends to nothing but the relay");
        var first = joiner.Factory.Networks.Single();
        That(first.Agent.Options.RelayOnly && first.Stack.Endpoint.Options.RelayOnly && joiner.AddressListings == 0,
            "its NAT agent and its transport are each relay-only, and it never listed its own addresses");
        That(host.AddressListings > 0, "while the host, which allows direct connections, offers its own");
        joiner.Control.Leave();
        That(rig.Run(() => joiner.Status.Role == InternalSessionRole.Solo, 5_000_000), "it leaves");

        // The same player allows a direct connection for this session: it starts on the relay and upgrades.
        rig.Join(joiner, invite.Code, new InternalJoinSettings { AllowDirect = true });
        That(rig.Run(() => joiner.Internet.JoinRoute == InternalConnectionRoute.Direct, 30_000_000), "a joiner that chose direct play upgrades to a direct path (" + joiner.Internet.JoinRoute + ")");
        That(rig.Run(() => host.Internet.DirectPlayers == 1, 5_000_000), "and the host counts a direct player");
        var chosen = joiner.Factory.Networks.Last();
        That(!ReferenceEquals(chosen, first) && !chosen.Agent.Options.RelayOnly && !chosen.Stack.Endpoint.Options.RelayOnly && joiner.AddressListings > 0,
            "the join that chose direct play turned relay-only off in both layers and offered its own addresses, for itself alone");
        joiner.Control.Leave();
        That(rig.Run(() => joiner.Status.Role == InternalSessionRole.Solo, 5_000_000), "it leaves again");

        // Choosing direct play is the player's own: a relay-only host keeps its address hidden, so no path is ever direct.
        using var hidden = new InternetRig(7_103);
        var quiet = hidden.AddPeer("host", NatBehaviour.FullCone, "198.51.100.50", "192.168.1.50");
        var eager = hidden.AddPeer("joiner", NatBehaviour.FullCone, "198.51.100.60", "10.0.0.60");
        var code = hidden.Host(quiet, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite)).Code;
        hidden.Join(eager, code, new InternalJoinSettings { AllowDirect = true });
        hidden.Run(() => false, 20_000_000);
        That(eager.Internet.JoinRoute == InternalConnectionRoute.Relayed && !eager.Internet.HostAllowsDirect && quiet.Internet.DirectPlayers == 0,
            "a joiner that allows direct play stays relayed to a host that does not (" + eager.Internet.JoinRoute + ")");
        That(quiet.Factory.SentTo.All(to => Plain(to.Address).Equals(NatScenario.ServerAddress)), "and the relay-only host sends to nothing but the relay");
    }

    internal static void AHostThatQuitsTakesItsListingWithIt()
    {
        using var rig = new InternetRig(7_104);
        var host = rig.AddPeer("host", NatBehaviour.PortRestricted, "198.51.100.70", "192.168.1.70");
        var joiner = rig.AddPeer("joiner", NatBehaviour.AddressRestricted, "198.51.100.80", "10.0.0.80");
        rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Public));
        var serverId = rig.Service.Sessions.Keys.Single();
        That(host.Internet.Listed && rig.Service.IsListed(serverId), "a public session is in the browser");

        // Joining by server id: the listing's own link, with no invite.
        rig.Join(joiner, "topiaforge://join/server/" + serverId);
        That(joiner.Status.Server?.ServerId == serverId && rig.Service.Requests.Any(request => request.Path == "/v1/servers/" + serverId), "the join found the server by its id");
        That(rig.Service.Requests.Any(request => request.Path == "/v1/join-tickets" && System.Text.Encoding.UTF8.GetString(request.Body!).Contains("\"invite\":null", StringComparison.Ordinal)),
            "and took a ticket without an invite");

        // Quitting: the provider closes, every player's session ends with the host's reason, and the listing is deleted.
        host.Provider.Dispose();
        That(rig.Service.Count("DELETE", "/v1/hosts/" + serverId) == 1 && !rig.Service.IsListed(serverId) && rig.Service.Sessions[serverId].Ended,
            "the host's listing was deleted while it closed");
        That(rig.Run(() => joiner.Status.JoinPhase == InternalJoinPhase.Ended, 15_000_000), "the joiner's session ends");
        That(joiner.Status.JoinFailure == InternalLiveFailure.HostEnded && joiner.Status.Role == InternalSessionRole.Solo,
            "because the host ended it, and the joiner is back in solo play (" + joiner.Status.JoinFailure + ")");
        var again = joiner.Control.Query(joiner.Target("topiaforge://join/server/" + serverId), false);
        That(rig.Run(() => again.IsCompleted, 5_000_000) && again.Failure == InternalLiveFailure.ServerNotListed, "and the server is not listed any more (" + again.Failure + ")");
    }

    internal static void StoppingHostingDeletesTheListingAndPlaysOn()
    {
        using var rig = new InternetRig(7_105);
        var host = rig.AddPeer("host", NatBehaviour.PortRestricted, "198.51.100.90", "192.168.1.90");
        var invite = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite));
        var serverId = rig.Service.Sessions.Keys.Single();
        host.Control.StopHosting();
        That(rig.Run(() => rig.Service.Sessions[serverId].Ended, 5_000_000) && rig.Service.Count("DELETE", "/v1/hosts/" + serverId) == 1, "stopping deletes the listing");
        That(host.Internet.ListingPhase == InternalListingPhase.Off && host.Internet.Invite == null && host.Status.Role == InternalSessionRole.Solo,
            "and the host plays on alone, with no invite to show");

        var late = rig.AddPeer("late", NatBehaviour.FullCone, "198.51.100.91", "10.0.0.91");
        var failed = late.Control.Join(late.Target(invite.Code), new InternalJoinSettings());
        That(failed.Succeeded && rig.Run(() => late.Status.JoinPhase == InternalJoinPhase.Ended, 5_000_000) && late.Status.JoinFailure == InternalLiveFailure.InviteNotFound,
            "and its invite no longer works (" + late.Status.JoinFailure + ")");

        // Hosting again registers afresh, with a new invite.
        var fresh = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite));
        That(fresh.Code != invite.Code && !rig.Service.Sessions[serverId].Ended, "hosting again registers a new session with a new invite");
    }

    internal static void ALostLinkReconnectsThroughTheServiceWithinTheGrace()
    {
        using var rig = new InternetRig(7_106);
        var host = rig.AddPeer("host", NatBehaviour.PortRestricted, "198.51.100.95", "192.168.1.95");
        var joiner = rig.AddPeer("joiner", NatBehaviour.Symmetric, "198.51.100.96", "10.0.0.96");
        var invite = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite));
        rig.Join(joiner, invite.Code);
        var participant = host.Provider.Snapshot.Participants.Single(candidate => !candidate.IsHost && candidate.IsConnected).Id;
        var tickets = rig.Service.Count("POST", "/v1/join-tickets");

        // The joiner's network drops everything for twelve seconds: the link dies, and the join reconnects on its own.
        var nat = rig.Scenario.NatOf(joiner.Site);
        nat.BlocksUdp = true;
        That(rig.Run(() => joiner.Status.JoinPhase == InternalJoinPhase.Reconnecting, 20_000_000), "the lost link starts a reconnection (" + joiner.Status.JoinPhase + ")");
        rig.Run(() => false, 2_000_000);
        nat.BlocksUdp = false;
        That(rig.Run(() => joiner.Status.JoinPhase == InternalJoinPhase.InSession, 30_000_000), "and it is back in the session within the grace (" + joiner.Status.JoinPhase + ", " + joiner.Status.JoinFailure + ")");
        That(host.Provider.Snapshot.Participants.Any(candidate => candidate.Id.Equals(participant) && candidate.IsConnected), "as the same participant");
        That(rig.Service.Count("POST", "/v1/join-tickets") == tickets, "with the ticket it had: a reconnection asks for no new one");
        That(joiner.Factory.SentTo.All(to => Plain(to.Address).Equals(NatScenario.ServerAddress)), "and still through nothing but the relay");
    }

    internal static IPAddress Plain(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
