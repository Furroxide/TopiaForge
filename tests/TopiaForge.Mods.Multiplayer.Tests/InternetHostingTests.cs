extern alias provider;

using System.Text;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.ControlPlane;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A hosted session's life with the TopiaForge service, through the SPI over the fake service: heartbeats, a listing that
/// lapses and is registered again, rate limits and refusals that back off and name their cause, a degraded or hostile
/// service, the descriptor following the session's world, and the host's invites.
/// </summary>
internal static class InternetHostingTests
{
    internal static void HeartbeatsKeepTheListingAndALapseRegistersAgain()
    {
        using var rig = new InternetRig(7_201);
        var host = rig.AddPeer("host", NatBehaviour.PortRestricted, "198.51.100.110", "192.168.1.110");
        var first = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Public));
        var serverId = rig.Service.Sessions.Keys.Single();
        var beats = rig.Service.Count("POST", "/heartbeat");
        rig.Run(() => false, 95_000_000);
        That(rig.Service.Count("POST", "/heartbeat") - beats == 3 && rig.Service.IsListed(serverId) && host.Internet.ListingPhase == InternalListingPhase.Online,
            "a heartbeat every 30 seconds keeps the listing (" + (rig.Service.Count("POST", "/heartbeat") - beats) + ")");
        That(LastHeartbeatPlayers(rig) == 1, "and reports the players, the host's own included");

        // The service stops answering heartbeats; once a listing's lifetime passes without one, the session has ended.
        rig.Service.Hostile = request => request.Path.EndsWith("/heartbeat", StringComparison.Ordinal) ? rig.Service.Error("internal") : null;
        That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Retrying, 40_000_000) && host.Internet.ListingFailure == InternalLiveFailure.ServiceUnavailable,
            "a failing heartbeat is retried, and the status says the service is unavailable");
        That(rig.Run(() => rig.Service.Sessions[serverId].Ended, 100_000_000), "the service ends the session its heartbeats stopped for");
        rig.Service.Hostile = null;
        That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Online && !rig.Service.Sessions[serverId].Ended, 120_000_000),
            "the host registers again once the service answers (" + host.Internet.ListingPhase + ", " + host.Internet.ListingFailure + ")");
        That(host.Internet.Invite != null && host.Internet.Invite.Code != first.Code && rig.Service.Count("POST", "/v1/hosts") == 2,
            "with a new session's invite");

        // An operator ends the session: the next heartbeat is refused, and the host registers again after the spacing.
        var registrations = rig.Service.Count("POST", "/v1/hosts");
        rig.Service.Delist(serverId);
        That(rig.Run(() => rig.Service.Count("POST", "/v1/hosts") == registrations + 1 && host.Internet.ListingPhase == InternalListingPhase.Online, 120_000_000),
            "a refused heartbeat registers the session again");
    }

    internal static void RateLimitsAndRefusalsBackOffAndNameTheirCause()
    {
        // A registration refused with Retry-After waits that long, says so, and then goes through.
        using (var rig = new InternetRig(7_202))
        {
            var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.120", "192.168.1.120");
            var refusals = 0;
            rig.Service.Hostile = request => request.Path == "/v1/hosts" && refusals++ < 2 ? rig.Service.Error("rate-limited", 12) : null;
            That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite)).Succeeded, "hosting starts");
            That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Retrying, 10_000_000) &&
                 host.Internet.ListingFailure == InternalLiveFailure.ServiceRateLimited && host.Internet.RetryAfterSeconds == 12, "a rate limit is shown with its delay");
            var refusedAt = rig.Now;
            That(rig.Run(() => rig.Service.Count("POST", "/v1/hosts") == 2, 30_000_000) && rig.Now - refusedAt >= 12_000_000, "the next try waits the service's delay");
            That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Online, 30_000_000) && host.Internet.ListingFailure == InternalLiveFailure.None,
                "and the third registers");
        }

        // A rate limit from the platform in front of the service, a page and not the service's own error, is still one.
        using (var rig = new InternetRig(7_212))
        {
            var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.122", "192.168.1.122");
            var refusals = 0;
            rig.Service.Hostile = request => request.Path == "/v1/hosts" && refusals++ < 1
                ? ControlPlaneResponse.Answered(429, Encoding.ASCII.GetBytes("<html>error 1015: you are being rate limited</html>"), false, 12)
                : null;
            That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Public)).Succeeded, "hosting starts");
            That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Retrying || host.Internet.ListingPhase == InternalListingPhase.Failed, 10_000_000) &&
                 host.Internet.ListingPhase == InternalListingPhase.Retrying &&
                 host.Internet.ListingFailure == InternalLiveFailure.ServiceRateLimited && host.Internet.RetryAfterSeconds == 12,
                "a platform's rate-limit page is a rate limit with its delay, not a refusal (" + host.Internet.ListingPhase + ", " + host.Internet.ListingFailure + ")");
            var refusedAt = rig.Now;
            That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Online, 60_000_000) && rig.Now - refusedAt >= 12_000_000,
                "and the host registers once the delay is over");
        }

        // A 503 that names a delay is honoured as a rate limit is, over the host's own shorter backoff, from a fresh challenge.
        using (var rig = new InternetRig(7_209))
        {
            var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.125", "192.168.1.125");
            var refusals = 0;
            rig.Service.Hostile = request => request.Path == "/v1/hosts" && refusals++ < 1 ? rig.Service.Error("service-unavailable", 40) : null;
            That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite)).Succeeded, "hosting starts");
            That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Retrying, 10_000_000) &&
                 host.Internet.ListingFailure == InternalLiveFailure.ServiceUnavailable && host.Internet.RetryAfterSeconds == 40, "a 503 is shown with the delay it named");
            var refusedAt = rig.Now;
            var challenges = rig.Service.Count("POST", "/v1/hosts/challenge");
            That(rig.Run(() => rig.Service.Count("POST", "/v1/hosts/challenge") > challenges, 60_000_000) && rig.Now - refusedAt >= 40_000_000,
                "the next try waits the named delay");
            That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Online, 10_000_000) && rig.Service.Count("POST", "/v1/hosts/challenge") == challenges + 1,
                "and registers with one fresh challenge");
        }

        // A server an operator holds out is told so, and the host waits long before it asks again.
        using (var rig = new InternetRig(7_203))
        {
            var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.130", "192.168.1.130");
            rig.Service.Blocked.Add(new string('0', 64));
            rig.Service.Hostile = request => request.Path == "/v1/hosts" ? rig.Service.Error("server-blocked") : null;
            That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Public)).Succeeded, "hosting starts");
            That(rig.Run(() => host.Internet.ListingFailure == InternalLiveFailure.ServerBlocked, 10_000_000) && host.Internet.ListingPhase == InternalListingPhase.Retrying,
                "a held-out server says so");
            var attempts = rig.Service.Count("POST", "/v1/hosts");
            rig.Run(() => false, 300_000_000);
            That(rig.Service.Count("POST", "/v1/hosts") == attempts, "and does not ask again for many minutes");
        }

        // Hosting turned off on the service: the host waits and checks again, then registers once it is back.
        using (var rig = new InternetRig(7_204))
        {
            var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.140", "192.168.1.140");
            rig.Service.Hosting = false;
            That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite)).Succeeded, "hosting starts");
            That(rig.Run(() => host.Internet.ListingFailure == InternalLiveFailure.ServiceDisabled, 10_000_000) && rig.Service.Count("POST", "/v1/hosts/challenge") == 0,
                "a host the service turned hosting off for never asks for a challenge");
            rig.Service.Hosting = true;
            That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Online, 90_000_000), "and registers once hosting is back");
        }

        // A proof the service cannot verify is a refusal nothing can mend: the listing fails and stops asking.
        using (var rig = new InternetRig(7_205))
        {
            var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.150", "192.168.1.150");
            rig.Service.Hostile = request => request.Path == "/v1/hosts" ? rig.Service.Error("proof-invalid") : null;
            That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite)).Succeeded, "hosting starts");
            That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Failed, 10_000_000) && host.Internet.ListingFailure == InternalLiveFailure.ServiceRefused,
                "a refused proof fails the listing (" + host.Internet.ListingPhase + ", " + host.Internet.ListingFailure + ")");
            var asked = rig.Service.Requests.Count;
            rig.Run(() => false, 120_000_000);
            That(rig.Service.Requests.Count == asked && host.Status.HostPhase == InternalHostPhase.Hosting, "and nothing more is asked, while hosting carries on for the LAN");
        }
    }

    internal static void HostileAnswersNeverBreakTheHost()
    {
        using var rig = new InternetRig(7_206);
        var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.160", "192.168.1.160");
        var hostile = new Func<ControlPlaneRequest, ControlPlaneResponse?>[]
        {
            _ => ControlPlaneResponse.Answered(201, Encoding.UTF8.GetBytes("{\"serverId\":\"x\"}"), true, null),
            _ => ControlPlaneResponse.Answered(201, Encoding.UTF8.GetBytes("<html>hello</html>"), false, null),
            _ => ControlPlaneResponse.Answered(200, Encoding.UTF8.GetBytes("{}"), true, null),
            _ => ControlPlaneResponse.Failed(ControlPlaneTransportFailure.TooLarge),
            _ => ControlPlaneResponse.Failed(ControlPlaneTransportFailure.Malformed),
            _ => ControlPlaneResponse.Failed(ControlPlaneTransportFailure.TimedOut),
            _ => ControlPlaneResponse.Failed(ControlPlaneTransportFailure.Redirected)
        };
        var round = 0;
        rig.Service.Hostile = request => request.Path == "/v1/hosts" && round < hostile.Length ? hostile[round++](request) : null;
        That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite)).Succeeded, "hosting starts");
        That(rig.Run(() => host.Internet.ListingPhase == InternalListingPhase.Online, 900_000_000),
            "after " + hostile.Length + " hostile answers to its registration the host comes online (" + host.Internet.ListingPhase + ", " + host.Internet.ListingFailure + ")");
        That(round == hostile.Length && rig.Service.Count("POST", "/v1/hosts") == hostile.Length + 1, "each was a failed try, retried with backoff");
        That(host.Status.HostPhase == InternalHostPhase.Hosting, "and hosting itself never failed");
    }

    internal static void InvitesAreMadeAndWithdrawnThroughTheSpi()
    {
        using var rig = new InternetRig(7_207);
        var host = rig.AddPeer("host", NatBehaviour.PortRestricted, "198.51.100.170", "192.168.1.170");
        var joiner = rig.AddPeer("joiner", NatBehaviour.PortRestricted, "198.51.100.171", "10.0.0.171");
        var first = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite));
        That(host.Control.CreateInvite().Succeeded && rig.Run(() => host.Internet.Invite!.Code != first.Code, 5_000_000), "a new invite becomes the one shown");
        var second = host.Internet.Invite!;
        rig.Join(joiner, first.Display);
        That(joiner.Status.JoinPhase == InternalJoinPhase.InSession, "the first invite still works");

        That(host.Control.RevokeInvite(first.Display.ToLowerInvariant()).Succeeded, "an invite is withdrawn by its code, typed as people read it");
        That(rig.Run(() => rig.Service.Count("POST", "/invites/revoke") == 1, 5_000_000), "the service is told");
        var late = rig.AddPeer("late", NatBehaviour.FullCone, "198.51.100.172", "10.0.0.172");
        That(late.Control.Join(late.Target(first.Code), new InternalJoinSettings()).Succeeded &&
             rig.Run(() => late.Status.JoinPhase == InternalJoinPhase.Ended, 5_000_000) && late.Status.JoinFailure == InternalLiveFailure.InviteNotFound,
            "nobody joins with a withdrawn invite (" + late.Status.JoinFailure + ")");
        That(host.Internet.Invite?.Code == second.Code, "and the newer invite is still the one shown");
        That(late.Control.Join(late.Target(second.Code), new InternalJoinSettings()).Succeeded && rig.Run(() => late.Status.JoinPhase == InternalJoinPhase.InSession, 60_000_000),
            "while the newer one works");

        // Only a host registered with the service makes or withdraws invites.
        That(joiner.Control.CreateInvite().ErrorCode == ModErrorCode.NotAuthoritative && joiner.Control.RevokeInvite(second.Code).ErrorCode == ModErrorCode.NotAuthoritative,
            "a joined player is no host");
        That(host.Control.RevokeInvite("not a code").ErrorCode == ModErrorCode.InvalidArgument, "an invite code is checked before anything is sent");
        rig.Service.Hostile = request => request.Path.EndsWith("/invites", StringComparison.Ordinal) ? rig.Service.Error("invite-limit") : null;
        That(host.Control.CreateInvite().Succeeded && rig.Run(() => host.Internet.InviteFailure != InternalLiveFailure.None, 5_000_000) &&
             host.Internet.InviteFailure == InternalLiveFailure.InviteLimit && host.Internet.ListingFailure == InternalLiveFailure.None,
            "a refused invite request says so on its own, apart from the listing (" + host.Internet.InviteFailure + ")");
        That(host.Internet.Invite?.Code == second.Code, "and the current invite stays");
        rig.Service.Hostile = null;
        That(host.Control.CreateInvite().Succeeded && rig.Run(() => host.Internet.Invite?.Code != second.Code, 5_000_000) && host.Internet.InviteFailure == InternalLiveFailure.None,
            "the next invite that works clears it");
    }

    internal static void TheDescriptorFollowsTheSessionsWorld()
    {
        using var rig = new InternetRig(7_208);
        var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.180", "192.168.1.180");
        rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Public) { World = "Test City" });
        var serverId = rig.Service.Sessions.Keys.Single();
        rig.Run(() => false, 2_000_000);
        host.Provider.Live!.Host!.UpdateWorld("Zombies", "Night City");
        That(rig.Run(() => rig.Service.Count("PUT", "/descriptor") == 1, 40_000_000), "a new world is published to the service");
        var listed = rig.Service.Sessions[serverId].Signed.Descriptor;
        That(listed.World == "Night City" && listed.Gamemode == "Zombies", "and the service lists the newer descriptor");

        // The service turns the next one away for now: it is sent again after a heartbeat, never sooner than the delay the
        // service named, and never frame after frame.
        int Puts() => rig.Service.Count("PUT", "/descriptor");
        int Beats() => rig.Service.Count("POST", "/heartbeat");
        var puts = Puts();
        rig.Service.Hostile = request => request.Method == ControlPlaneMethod.Put ? rig.Service.Error("rate-limited", 45) : null;
        host.Provider.Live!.Host!.UpdateWorld("Zombies", "Day City");
        var limitedAt = rig.Now;
        rig.Run(() => false, 30_000_000);
        That(Puts() - puts == 1, "a rate-limited descriptor waits the delay the service named (" + (Puts() - puts) + " sent in 30 s)");
        That(rig.Run(() => Puts() - puts == 2, 60_000_000) && rig.Now - limitedAt >= 45_000_000, "and is sent again once the delay is over");
        rig.Service.Hostile = request => request.Method == ControlPlaneMethod.Put ? rig.Service.Error("internal") : null;
        puts = Puts();
        var beats = Beats();
        rig.Run(() => false, 90_000_000);
        That(Puts() - puts <= Beats() - beats + 1, "a failing service gets it again at most once a heartbeat (" + (Puts() - puts) + " for " + (Beats() - beats) + " heartbeats)");
        rig.Service.Hostile = null;
        That(rig.Run(() => rig.Service.Sessions[serverId].Signed.Descriptor.World == "Day City", 60_000_000), "and the service lists it once it takes it");
    }

    /// <summary>
    /// The host stops, or the game closes, while its registration's answer is still on the way: the service already holds
    /// the listing, and only that answer's token can delete it, so stopping lets the registration land and then deletes.
    /// </summary>
    internal static void AStopWhileRegisteringStillDeletesTheListing()
    {
        using (var rig = new InternetRig(7_210))
        {
            var held = new HeldAnswerTransport(rig.Service, request => request.Path == "/v1/hosts");
            rig.WrapTransport = _ => held;
            var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.185", "192.168.1.185");
            That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Public)).Succeeded, "hosting starts");
            That(rig.Run(() => held.Holding, 10_000_000), "the service takes the registration while its answer is still on the way");
            var serverId = rig.Service.Sessions.Keys.Single();
            That(rig.Service.IsListed(serverId), "so the server is listed already");
            host.Control.StopHosting();
            held.Release();
            That(rig.Run(() => rig.Service.Sessions[serverId].Ended, 10_000_000) && rig.Service.Count("DELETE", "/v1/hosts/" + serverId) == 1 && !rig.Service.IsListed(serverId),
                "stopping deletes the listing once the registration lands (" + rig.Service.Count("DELETE", "/v1/hosts/" + serverId) + " deletes)");
            That(held.Cancelled == 0 && host.Internet.ListingPhase == InternalListingPhase.Off, "and never gave the registration up");
        }

        using (var rig = new InternetRig(7_211))
        {
            var held = new HeldAnswerTransport(rig.Service, request => request.Path == "/v1/hosts");
            rig.WrapTransport = _ => held;
            var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.186", "192.168.1.186");
            That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Invite)).Succeeded, "hosting starts");
            That(rig.Run(() => held.Holding, 10_000_000), "the registration is on its way");
            var serverId = rig.Service.Sessions.Keys.Single();

            // The answer lands while the provider closes, inside the moment closing waits for the delete.
            _ = Task.Delay(100).ContinueWith(_ => held.Release(), TaskScheduler.Default);
            host.Provider.Dispose();
            That(rig.Service.Sessions[serverId].Ended && rig.Service.Count("DELETE", "/v1/hosts/" + serverId) == 1 && held.Cancelled == 0,
                "closing the game deletes the session the registration made, invite and all");
        }
    }

    private static int LastHeartbeatPlayers(InternetRig rig)
    {
        var last = rig.Service.Requests.Last(request => request.Path.EndsWith("/heartbeat", StringComparison.Ordinal));
        var text = Encoding.UTF8.GetString(last.Body!);
        return int.Parse(text.Substring(text.IndexOf(':') + 1).TrimEnd('}'), System.Globalization.CultureInfo.InvariantCulture);
    }
}
