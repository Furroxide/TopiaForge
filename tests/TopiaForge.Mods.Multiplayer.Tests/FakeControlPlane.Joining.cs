extern alias provider;

using System.Text;
using provider::TopiaForge.Multiplayer.ControlPlane;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.Network;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The fake service's joining half: resolution, server detail, join tickets, relay credentials and signaling.</summary>
internal sealed partial class FakeControlPlane
{
    /// <summary>Gets the peer id of every ticket the service issued, in order, as its room names them.</summary>
    internal List<string> IssuedPeers { get; } = new();

    /// <summary>
    /// Opens the signaling room the way the service's upgrade does: a host token opens the host's socket, a ticket that
    /// still holds opens a peer's under the privacy the ticket carries, and anything else is refused before it opens.
    /// </summary>
    internal ISignalingClient OpenSignaling(string serverId, string credential)
    {
        lock (sessions)
        {
            Expire();
            var room = scenario.Signaling;
            if (!Signaling) return Refused(room);
            if (sessions.TryGetValue(serverId, out var session) && !session.Ended && credential == session.Token) return room.ConnectHost(session.RelayOnly);
            if (!tickets.TryGetValue(credential, out var ticket) || !Holds(ticket)) return Refused(room);
            return room.ConnectPeer(ticket.PeerId, ticket.RelayOnly);
        }
    }

    /// <summary>Withdraws a ticket's standing the way withdrawing its invite does, closing its socket with 4403.</summary>
    private void Withdraw(FakeHostSession session, string code)
    {
        foreach (var ticket in tickets.Values.Where(ticket => ticket.ServerId == session.ServerId && ticket.Invite == code)) scenario.Signaling.Revoke(ticket.PeerId);
    }

    private static ISignalingClient Refused(FakeSignalingService room)
    {
        var client = new FakeSignalingClient(room, SignalRole.Peer, "0000000000000000", true, string.Empty);
        client.Refuse();
        return client;
    }

    private ControlPlaneResponse Resolve(ControlPlaneRequest request)
    {
        if (!TryBody(request, out var body, "code")) return Error("invalid-request");
        if (!Invites) return Error("feature-disabled");
        if (!InviteCode.TryNormalize(Text(body, "code"), out var code)) return Error("not-found");
        var session = sessions.Values.FirstOrDefault(candidate => !candidate.Ended && candidate.Invites.TryGetValue(code, out var expires) && expires > Now);
        if (session == null) return Error("not-found");
        return Json(200, new JsonObjectNode()
            .Add("descriptor", Encoding.UTF8.GetString(session.Descriptor))
            .Add("descriptorSha256", session.Signed.Sha256)
            .Add("listing", Listing(session))
            .Add("serverId", session.ServerId));
    }

    private ControlPlaneResponse Detail(string serverId)
    {
        if (!sessions.TryGetValue(serverId, out var session) || session.Ended || session.Visibility != "public") return Error("not-found");
        return Json(200, new JsonObjectNode()
            .Add("descriptor", Encoding.UTF8.GetString(session.Descriptor))
            .Add("descriptorSha256", session.Signed.Sha256)
            .Add("listing", Listing(session)));
    }

    private ControlPlaneResponse Ticket(ControlPlaneRequest request)
    {
        if (!TryBody(request, out var body, "invite", "relayOnly", "serverId")) return Error("invalid-request");
        var serverId = Text(body, "serverId");
        var relayOnly = Member(body, "relayOnly") is JsonBooleanNode { Value: true };
        string? code = null;
        if (Member(body, "invite") is JsonStringNode invite && !InviteCode.TryNormalize(invite.Value, out code)) return Error("not-found");
        if (!sessions.TryGetValue(serverId, out var session) || session.Ended) return Error("not-found");

        // A public server needs no invite; an invite-only one needs one of its own live invites, and any invite given must be its own.
        if (code != null && !(session.Invites.TryGetValue(code, out var expires) && expires > Now)) return Error("not-found");
        if (code == null && session.Visibility != "public") return Error("not-found");
        var ticket = "tfj1_" + Random(106);
        var peer = PeerIdOf(ticket);
        tickets[ticket] = new FakeTicket(serverId, session.Epoch, code, relayOnly, peer, Now + (6L * 3_600 * 1_000_000));
        IssuedPeers.Add(peer);
        return Json(201, new JsonObjectNode()
            .Add("expiresAt", Seconds(Now + (6L * 3_600 * 1_000_000)))
            .Add("hostRelayOnly", session.RelayOnly)
            .Add("relayOnly", relayOnly)
            .Add("serverId", serverId)
            .Add("ticket", ticket));
    }

    private ControlPlaneResponse Mint(ControlPlaneRequest request)
    {
        if (!TryBody(request, out var body, "serverId")) return Error("invalid-request");
        if (!Relay || KillSwitch) return Error("relay-unavailable");
        var serverId = Text(body, "serverId");
        var credential = request.Bearer ?? string.Empty;
        var host = sessions.TryGetValue(serverId, out var session) && !session.Ended && credential == session.Token;
        if (!host && !(tickets.TryGetValue(credential, out var ticket) && ticket.ServerId == serverId && Holds(ticket))) return Error("unauthorized");
        if (Mints >= MintAllowance) return Error("rate-limited", 60);
        Mints++;
        var name = "topiaforge-" + Mints.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var password = "relay-secret-" + Mints.ToString(System.Globalization.CultureInfo.InvariantCulture);
        scenario.Turn.Users[name] = (password, Now + (scenario.TtlSeconds * 1_000_000L));
        var turnUrls = new List<JsonNode> { new JsonStringNode("turn:" + NatScenario.TurnHost + ":3478?transport=udp") };
        if (scenario.SecondTurnPort) turnUrls.Add(new JsonStringNode("turn:" + NatScenario.TurnHost + ":3479?transport=udp"));
        turnUrls.Add(new JsonStringNode("turns:" + NatScenario.TurnHost + ":5349?transport=tcp"));
        var servers = new JsonArrayNode(new List<JsonNode>
        {
            new JsonObjectNode().Add("urls", new JsonArrayNode(new List<JsonNode> { new JsonStringNode("stun:" + NatScenario.StunHost + ":3478") })),
            new JsonObjectNode().Add("credential", password).Add("urls", new JsonArrayNode(turnUrls)).Add("username", name)
        });
        return Json(201, new JsonObjectNode()
            .Add("expiresAt", Seconds(Now + (scenario.TtlSeconds * 1_000_000L)))
            .Add("iceServers", servers)
            .Add("ttlSeconds", scenario.TtlSeconds));
    }

    /// <summary>A ticket holds while its session lives in the same epoch and, when it came with an invite, while that invite is live.</summary>
    private bool Holds(FakeTicket ticket) =>
        ticket.ExpiresAt > Now && sessions.TryGetValue(ticket.ServerId, out var session) && !session.Ended && session.Epoch == ticket.Epoch &&
        (ticket.Invite == null ? session.Visibility == "public" : session.Invites.TryGetValue(ticket.Invite, out var expires) && expires > Now);

    /// <summary>The listing a server answer carries, schema <c>topiaforge.server-listing/1</c>, with no member that could hold an address.</summary>
    private JsonObjectNode Listing(FakeHostSession session)
    {
        var descriptor = session.Signed.Descriptor;
        return new JsonObjectNode()
            .Add("descriptorSha256", session.Signed.Sha256)
            .Add("gameBuild", descriptor.GameBuild)
            .Add("gamemode", descriptor.Gamemode)
            .Add("hostServesPackages", descriptor.HostServesPackages)
            .Add("maxPlayers", descriptor.MaxPlayers)
            .Add("name", descriptor.Name)
            .Add("packageCount", descriptor.Packages.Count)
            .Add("packageDigest", descriptor.PackageDigest)
            .Add("passwordRequired", descriptor.PasswordRequired)
            .Add("players", Math.Min(session.Players, descriptor.MaxPlayers))
            .Add("protocolVersion", descriptor.ProtocolVersion)
            .Add("relayOnly", session.RelayOnly)
            .Add("runtimeVersion", descriptor.RuntimeVersion)
            .Add("schema", ControlPlaneAnswers.ListingSchema)
            .Add("serverId", session.ServerId)
            .Add("updatedAt", Seconds(session.HeartbeatAt))
            .Add("visibility", session.Visibility)
            .Add("voice", descriptor.Voice)
            .Add("world", descriptor.World);
    }
}
