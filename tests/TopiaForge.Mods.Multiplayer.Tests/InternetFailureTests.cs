extern alias provider;

using System.Net;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.ControlPlane;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// What goes wrong with Internet sessions, through the SPI over the fake service: relay-only honoured whatever it costs
/// when the relay is unavailable, every join failure with its own code and this game's own words, and a game with no
/// service configured contacting nothing at all.
/// </summary>
internal static class InternetFailureTests
{
    internal static void RelayOnlyIsHonouredWhenTheRelayIsUnavailable()
    {
        using var rig = new InternetRig(7_301);
        var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.200", "192.168.1.200");
        var joiner = rig.AddPeer("joiner", NatBehaviour.FullCone, "198.51.100.201", "10.0.0.201");
        var invite = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite) { AllowDirect = true });

        // The kill switch is engaged: a relay-only join stops before anything connects, and says why.
        rig.Service.KillSwitch = true;
        var status = Fail(rig, joiner, invite.Code, new InternalJoinSettings());
        That(status.JoinFailure == InternalLiveFailure.RelayUnavailable && status.JoinDetail.Contains("Hide my IP", StringComparison.Ordinal) && status.Internet.HostAllowsDirect,
            "a relay-only join fails as relay unavailable, and the host would take a direct path (" + status.JoinFailure + ")");
        That(joiner.Factory.Networks.Count == 0 && rig.Service.Count("POST", "/v1/join-tickets") == 0, "and no socket opened, no ticket was asked for");

        // The service's status offers the relay, but the mint is refused: the join still never goes direct.
        rig.Service.KillSwitch = false;
        rig.Service.Hostile = request => request.Path == "/v1/turn/credentials" && request.Bearer!.StartsWith("tfj1_", StringComparison.Ordinal) ? rig.Service.Error("relay-unavailable") : null;
        status = Fail(rig, joiner, invite.Code, new InternalJoinSettings());
        That(status.JoinFailure == InternalLiveFailure.RelayUnavailable && joiner.Factory.Networks.Count == 0,
            "a refused mint ends a relay-only join before any socket opens (" + status.JoinFailure + ")");
        foreach (var refusal in new[] { "ticket-limit", "upstream-failed" })
        {
            rig.Service.Hostile = request => request.Path == "/v1/turn/credentials" && request.Bearer!.StartsWith("tfj1_", StringComparison.Ordinal) ? rig.Service.Error(refusal) : null;
            That(Fail(rig, joiner, invite.Code, new InternalJoinSettings()).JoinFailure == InternalLiveFailure.RelayUnavailable && joiner.Factory.Networks.Count == 0,
                "so does " + refusal);
        }

        // Only a player who allowed direct play for this session goes on without the relay, and only to a host that takes it.
        rig.Service.Hostile = null;
        rig.Service.KillSwitch = true;
        using var open = new InternetRig(7_302);
        var exposed = open.AddPeer("host", NatBehaviour.FullCone, "198.51.100.210", "192.168.1.210");
        var direct = open.AddPeer("joiner", NatBehaviour.FullCone, "198.51.100.211", "10.0.0.211");
        var code = open.Host(exposed, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite) { AllowDirect = true }).Code;
        open.Service.KillSwitch = true;
        That(Fail(open, direct, code, new InternalJoinSettings()).JoinFailure == InternalLiveFailure.RelayUnavailable, "the kill switch stops a relay-only join to it too");
        open.Join(direct, code, new InternalJoinSettings { AllowDirect = true });
        That(open.Run(() => direct.Internet.JoinRoute == InternalConnectionRoute.Direct, 10_000_000) && !direct.Internet.RelayReady,
            "but a player who allowed direct play joins on a direct path, with no relay at all (" + direct.Internet.JoinRoute + ")");

        // A relay-only host the service gives no relay waits, unreachable, and never shows its address instead.
        using var hidden = new InternetRig(7_303);
        var waiting = hidden.AddPeer("host", NatBehaviour.FullCone, "198.51.100.220", "192.168.1.220");
        hidden.Service.Hostile = request => request.Path == "/v1/turn/credentials" && request.Bearer!.StartsWith("tfh1_", StringComparison.Ordinal) ? hidden.Service.Error("relay-unavailable") : null;
        That(waiting.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite)).Succeeded, "hosting starts");
        That(hidden.Run(() => waiting.Internet.ListingPhase == InternalListingPhase.Online && waiting.Internet.ListingFailure == InternalLiveFailure.RelayUnavailable, 10_000_000),
            "a relay-only host without the relay is registered but says it is unreachable (" + waiting.Internet.ListingFailure + ")");
        hidden.Run(() => false, 60_000_000);
        That(!waiting.Internet.RelayReady && waiting.Factory.SentTo.All(to => InternetSessionTests.Plain(to.Address).Equals(NatScenario.ServerAddress)),
            "and contacts nothing but the relay, which it keeps asking for");
        var late = hidden.AddPeer("joiner", NatBehaviour.FullCone, "198.51.100.221", "10.0.0.221");
        var lateStatus = Fail(hidden, late, waiting.Internet.Invite!.Code, new InternalJoinSettings(), 30_000_000);
        That(lateStatus.JoinFailure == InternalLiveFailure.HostOffline, "a joiner meanwhile finds the host offline (" + lateStatus.JoinFailure + ")");
        hidden.Service.Hostile = null;
        That(hidden.Run(() => waiting.Internet.RelayReady && waiting.Internet.ListingFailure == InternalLiveFailure.None, 400_000_000), "the relay comes once the service gives it");
    }

    internal static void JoinFailuresNameTheirCause()
    {
        using var rig = new InternetRig(7_304);
        var host = rig.AddPeer("host", NatBehaviour.PortRestricted, "198.51.100.230", "192.168.1.230");
        var joiner = rig.AddPeer("joiner", NatBehaviour.PortRestricted, "198.51.100.231", "10.0.0.231");
        var invite = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite));
        var serverId = rig.Service.Sessions.Keys.Single();

        Expect(rig, joiner, "ABCDE-12345", InternalLiveFailure.InviteNotFound, "an unknown invite");
        Expect(rig, joiner, "topiaforge://join/server/" + new string('e', 64), InternalLiveFailure.ServerNotListed, "an unknown server");
        Expect(rig, joiner, "topiaforge://join/server/" + serverId, InternalLiveFailure.ServerNotListed, "an invite-only server asked for by id");

        rig.Service.Signaling = false;
        Expect(rig, joiner, invite.Code, InternalLiveFailure.ServiceDisabled, "Internet joining turned off");
        rig.Service.Signaling = true;
        rig.Service.Invites = false;
        Expect(rig, joiner, invite.Code, InternalLiveFailure.ServiceDisabled, "invites turned off");
        rig.Service.Invites = true;

        rig.Service.Hostile = request => request.Path == "/v1/invites/resolve" ? rig.Service.Error("rate-limited", 30) : null;
        Expect(rig, joiner, invite.Code, InternalLiveFailure.ServiceRateLimited, "a rate limit");
        That(joiner.Status.Internet.RetryAfterSeconds == 30, "and the joiner is told the delay the service named (" + joiner.Status.Internet.RetryAfterSeconds + ")");
        rig.Service.Hostile = request => request.Path == "/v1/join-tickets" ? rig.Service.Error("rate-limited", 45) : null;
        Expect(rig, joiner, invite.Code, InternalLiveFailure.ServiceRateLimited, "a rate-limited ticket");
        That(joiner.Status.Internet.RetryAfterSeconds == 45, "with that refusal's own delay (" + joiner.Status.Internet.RetryAfterSeconds + ")");
        rig.Service.Hostile = _ => ControlPlaneResponse.Failed(ControlPlaneTransportFailure.Unreachable);
        Expect(rig, joiner, invite.Code, InternalLiveFailure.ServiceUnavailable, "a service nobody reaches");
        That(joiner.Status.Internet.RetryAfterSeconds == 0, "and a failure that named no delay shows none");
        rig.Service.Hostile = _ => ControlPlaneResponse.Failed(ControlPlaneTransportFailure.Tls);
        Expect(rig, joiner, invite.Code, InternalLiveFailure.ServiceUnavailable, "a certificate that does not verify");
        rig.Service.Hostile = request => request.Path == "/v1/join-tickets" ? rig.Service.Error("not-found") : null;
        Expect(rig, joiner, invite.Code, InternalLiveFailure.InviteNotFound, "an invite that ended between its resolution and the ticket");
        rig.Service.Hostile = request => request.Path == "/v1/invites/resolve" ? ControlPlaneResponse.Answered(200, System.Text.Encoding.UTF8.GetBytes("{\"serverId\":1}"), true, null) : null;
        Expect(rig, joiner, invite.Code, InternalLiveFailure.ServiceUnavailable, "a resolution that does not read");
        rig.Service.Hostile = null;

        // The host withdraws the invite while the joiner is still on the way: its ticket stops holding, and it says so.
        var withdrawn = joiner.Control.Join(joiner.Target(invite.Code), new InternalJoinSettings());
        That(withdrawn.Succeeded && rig.Run(() => rig.Scenario.Signaling.OpenPeerSockets > 0, 10_000_000), "the joiner's signaling socket is open");
        That(host.Control.RevokeInvite(invite.Code).Succeeded, "the host withdraws the invite");
        That(rig.Run(() => joiner.Status.JoinPhase == InternalJoinPhase.Ended, 30_000_000) && joiner.Status.JoinFailure == InternalLiveFailure.InviteWithdrawn,
            "the joiner learns the invite was withdrawn (" + joiner.Status.JoinFailure + ")");
    }

    internal static void WithoutAServiceNothingIsContacted()
    {
        using var rig = new InternetRig(7_305);
        var player = rig.AddPeer("offline", NatBehaviour.FullCone, "198.51.100.240", "192.168.1.240", service: false);
        That(!player.Internet.ServiceConfigured, "no service is configured");
        foreach (var visibility in new[] { InternalSessionVisibility.Public, InternalSessionVisibility.Invite })
        {
            var refused = player.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, visibility));
            That(!refused.Succeeded && refused.ErrorCode == ModErrorCode.Unavailable && refused.ErrorMessage == provider::TopiaForge.Multiplayer.InternalControl.NoService,
                visibility + " hosting needs the service and says so");
        }

        foreach (var text in new[] { "ABCDE-12345", "topiaforge://join/server/" + new string('a', 64) })
        {
            var joined = player.Control.Join(player.Target(text), new InternalJoinSettings());
            That(!joined.Succeeded && joined.ErrorCode == ModErrorCode.Unavailable, "'" + text + "' cannot be joined without the service");
            var query = player.Control.Query(player.Target(text), false);
            That(query.IsCompleted && query.Failure == InternalLiveFailure.ServiceNotConfigured, "nor looked up");
        }

        rig.Run(() => false, 2_000_000);
        That(rig.Service.Requests.Count == 0 && player.Factory.Networks.Count == 0, "nothing reached the service, and no socket opened");

        // An origin the manager names must read as one: anything else leaves Internet play off, and says so without the text.
        foreach (var (origin, configured) in new[] { ("https://mp.example.org", true), ("http://mp.example.org", false), ("https://203.0.113.9", false), ("https://mp.example.org/", false) })
        {
            var peer = new InternetPeerWithOrigin(rig, origin);
            That(peer.Control.GetStatus().Internet.ServiceConfigured == configured, "'" + origin + "' " + (configured ? "configures" : "does not configure") + " the service");
            That(configured || peer.Runtime.RecentDiagnostics.Any(record => record.Code == "internet.origin-invalid" && !record.Text.Contains("example", StringComparison.Ordinal) &&
                                                                             !record.Text.Contains("203.0.113.9", StringComparison.Ordinal)),
                "a refused origin is reported without its text");
            peer.Dispose();
        }
    }

    /// <summary>
    /// Every outcome a request to the service can have maps to its own code, for an invite and for a server, and every code
    /// the Internet flows add has a sentence of its own: none falls through to another's words.
    /// </summary>
    internal static void ServiceOutcomesMapToTheirOwnCodes()
    {
        var expected = new Dictionary<ServiceFailure, (InternalLiveFailure Server, InternalLiveFailure Invite)>
        {
            [ServiceFailure.None] = (InternalLiveFailure.None, InternalLiveFailure.None),
            [ServiceFailure.NotConfigured] = (InternalLiveFailure.ServiceNotConfigured, InternalLiveFailure.ServiceNotConfigured),
            [ServiceFailure.Cancelled] = (InternalLiveFailure.Cancelled, InternalLiveFailure.Cancelled),
            [ServiceFailure.FeatureDisabled] = (InternalLiveFailure.ServiceDisabled, InternalLiveFailure.ServiceDisabled),
            [ServiceFailure.RateLimited] = (InternalLiveFailure.ServiceRateLimited, InternalLiveFailure.ServiceRateLimited),
            [ServiceFailure.NotFound] = (InternalLiveFailure.ServerNotListed, InternalLiveFailure.InviteNotFound),
            [ServiceFailure.Unauthorized] = (InternalLiveFailure.ServerNotListed, InternalLiveFailure.InviteWithdrawn),
            [ServiceFailure.ServerBlocked] = (InternalLiveFailure.ServerBlocked, InternalLiveFailure.ServerBlocked),
            [ServiceFailure.RelayUnavailable] = (InternalLiveFailure.RelayUnavailable, InternalLiveFailure.RelayUnavailable),
            [ServiceFailure.TicketLimit] = (InternalLiveFailure.RelayUnavailable, InternalLiveFailure.RelayUnavailable),
            [ServiceFailure.InviteLimit] = (InternalLiveFailure.InviteLimit, InternalLiveFailure.InviteLimit)
        };
        foreach (var refused in new[]
                 {
                     ServiceFailure.InvalidRequest, ServiceFailure.InvalidDescriptor, ServiceFailure.UnsupportedVisibility, ServiceFailure.ChallengeInvalid,
                     ServiceFailure.ProofInvalid, ServiceFailure.MethodNotAllowed, ServiceFailure.StaleDescriptor, ServiceFailure.PayloadTooLarge,
                     ServiceFailure.UnsupportedMediaType, ServiceFailure.Refused
                 })
        {
            expected[refused] = (InternalLiveFailure.ServiceRefused, InternalLiveFailure.ServiceRefused);
        }

        foreach (var failure in Enum.GetValues<ServiceFailure>())
        {
            var (server, invite) = expected.TryGetValue(failure, out var named) ? named : (InternalLiveFailure.ServiceUnavailable, InternalLiveFailure.ServiceUnavailable);
            That((InternalLiveFailure)(int)InternetFailures.OfService(failure) == server && (InternalLiveFailure)(int)InternetFailures.OfService(failure, invite: true) == invite,
                failure + " maps to " + server + " for a server and " + invite + " for an invite");
        }

        var fallback = InternetFailures.Detail(provider::TopiaForge.Multiplayer.Network.LiveFailure.None);
        var sentences = new HashSet<string>(StringComparer.Ordinal);
        for (var code = (int)InternalLiveFailure.ServiceUnavailable; code <= (int)InternalLiveFailure.InviteLimit; code++)
        {
            if (code == (int)InternalLiveFailure.ContentUnreadable) continue;
            var failure = (provider::TopiaForge.Multiplayer.Network.LiveFailure)code;
            That(Enum.IsDefined(typeof(InternalLiveFailure), code) && ((InternalLiveFailure)code).ToString() == failure.ToString(), failure + " is the SPI's code " + code + " too");
            var sentence = InternetFailures.Detail(failure);
            That(sentence.Length > 0 && sentence != fallback && sentences.Add(sentence), failure + " has a sentence of its own");
        }

        That(InternetFailures.Detail(provider::TopiaForge.Multiplayer.Network.LiveFailure.RelayUnavailable, hostAllowsDirect: false) !=
             InternetFailures.Detail(provider::TopiaForge.Multiplayer.Network.LiveFailure.RelayUnavailable), "a relay-only host's join is told no direct path would help");
    }

    /// <summary>A player composed with no fake service but an origin in the manager's configuration, as the game reads it.</summary>
    private sealed class InternetPeerWithOrigin : IDisposable
    {
        internal InternetPeerWithOrigin(InternetRig rig, string origin)
        {
            var files = new MemoryDataFiles();
            var content = new provider::TopiaForge.Multiplayer.RuntimeContent("2545", "0.2.0");
            var diagnostics = new provider::TopiaForge.Multiplayer.RoutedDiagnostics(null);
            var setup = new provider::TopiaForge.Multiplayer.Network.LiveSessionsSetup(files, content) { Factory = new SteppedNetworkFactory(rig.Scenario.Network, "origin") };
            Provider = new provider::TopiaForge.Multiplayer.MultiplayerProvider(LiveRig.ProviderId, diagnostics, () => rig.Now, setup);
            Control = new provider::TopiaForge.Multiplayer.InternalControl(Provider, content, diagnostics);
            Runtime = new TopiaForge.ModManager.MultiplayerRuntimeHost("2545", "0.2.0", new RecordingMultiplayerLog()) { ServiceOrigin = origin };
            Control.AttachRuntime(Runtime);
        }

        internal provider::TopiaForge.Multiplayer.MultiplayerProvider Provider { get; }

        internal provider::TopiaForge.Multiplayer.InternalControl Control { get; }

        internal TopiaForge.ModManager.MultiplayerRuntimeHost Runtime { get; }

        public void Dispose()
        {
            Control.Dispose();
            Provider.Dispose();
        }
    }

    private static void Expect(InternetRig rig, InternetPeer peer, string target, InternalLiveFailure failure, string about)
    {
        var status = Fail(rig, peer, target, new InternalJoinSettings());
        That(status.JoinFailure == failure, about + " fails as " + failure + " (" + status.JoinFailure + ": " + status.JoinDetail + ")");
        That(status.JoinDetail.Length > 0 && status.JoinDetail == provider::TopiaForge.Multiplayer.ControlPlane.InternetFailures.Detail(
                 (provider::TopiaForge.Multiplayer.Network.LiveFailure)(int)failure, status.Internet.HostAllowsDirect),
            about + " is told in this game's own words");
        That(status.Role == InternalSessionRole.Solo, about + " leaves the player in solo play");
    }

    private static InternalLiveStatus Fail(InternetRig rig, InternetPeer peer, string target, InternalJoinSettings settings, long maximumMicros = 15_000_000)
    {
        var started = peer.Control.Join(peer.Target(target), settings);
        That(started.Succeeded, "the join starts (" + started.ErrorMessage + ")");
        That(rig.Run(() => peer.Status.JoinPhase == InternalJoinPhase.Ended || peer.Status.JoinPhase == InternalJoinPhase.InSession, maximumMicros), "the join settles");
        var status = peer.Status;
        That(status.JoinPhase == InternalJoinPhase.Ended, "the join ends (" + status.JoinPhase + ")");
        return status;
    }
}
