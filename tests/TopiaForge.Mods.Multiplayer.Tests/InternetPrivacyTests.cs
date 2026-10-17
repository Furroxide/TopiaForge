extern alias provider;

using System.Net;
using System.Reflection;
using System.Text;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.ControlPlane;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// No address, and nothing the service wrote, leaves the Internet flows: not through the SPI's status, the runtime's log
/// and diagnostics, the NAT layer's reports, or anything the flows print. Tokens, tickets, relay credentials and invite
/// codes are never printed either; the one place a code appears is the host's own invite in the status.
/// </summary>
internal static class InternetPrivacyTests
{
    private const string ServiceText = "Sam at 203.0.113.250:27250 says the relay 198.51.100.249 is down";

    internal static void NothingTheInternetFlowsReportNamesAnAddress()
    {
        using var rig = new InternetRig(7_401);
        rig.Service.ErrorText = ServiceText;
        var host = rig.AddPeer("host", NatBehaviour.PortRestricted, "198.51.100.140", "192.168.7.14");
        var relayed = rig.AddPeer("relayed", NatBehaviour.Symmetric, "198.51.100.141", "10.7.0.41");
        var direct = rig.AddPeer("direct", NatBehaviour.FullCone, "198.51.100.142", "10.7.0.42");
        var stranger = rig.AddPeer("stranger", NatBehaviour.FullCone, "198.51.100.143", "10.7.0.43");

        // A refusal with the hostile text first, then a session with a relayed and a direct player, a failed join, a
        // rate-limited heartbeat, a withdrawn invite and the host quitting.
        var refusals = 0;
        rig.Service.Hostile = request => request.Path == "/v1/hosts" && refusals++ == 0 ? rig.Service.Error("rate-limited", 6) : null;
        var invite = rig.Host(host, new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Public) { AllowDirect = true });
        var serverId = rig.Service.Sessions.Keys.Single();
        rig.Join(relayed, invite.Code);
        rig.Join(direct, "topiaforge://join/server/" + serverId, new InternalJoinSettings { AllowDirect = true });
        That(rig.Run(() => direct.Internet.JoinRoute == InternalConnectionRoute.Direct, 30_000_000), "the direct player upgrades");
        var failed = stranger.Control.Join(stranger.Target("ZZZZZ-00000"), new InternalJoinSettings());
        That(failed.Succeeded && rig.Run(() => stranger.Status.JoinPhase == InternalJoinPhase.Ended, 10_000_000), "a stranger's unknown invite fails");
        rig.Service.Hostile = request => request.Path.EndsWith("/heartbeat", StringComparison.Ordinal) ? rig.Service.Error("rate-limited", 9) : null;
        rig.Run(() => host.Internet.ListingFailure == InternalLiveFailure.ServiceRateLimited, 40_000_000);
        rig.Service.Hostile = null;
        That(host.Control.CreateInvite().Succeeded && rig.Run(() => host.Internet.Invite!.Code != invite.Code, 40_000_000), "a second invite");
        That(host.Control.RevokeInvite(invite.Code).Succeeded, "the first is withdrawn");
        rig.Run(() => false, 5_000_000);
        var texts = new List<string>();
        foreach (var peer in rig.Peers) Collect(texts, peer);
        host.Provider.Dispose();
        rig.Run(() => relayed.Status.JoinPhase == InternalJoinPhase.Ended && direct.Status.JoinPhase == InternalJoinPhase.Ended, 20_000_000);
        foreach (var peer in rig.Peers) Collect(texts, peer);

        var secrets = new List<string> { "203.0.113.", "198.51.100.", "192.168.7.", "10.7.0.", "27250", "says", "is down", "Sam at", "tfh1_", "tfj1_", "relay-secret", "topiaforge-" };
        secrets.Add(invite.Code);
        secrets.Add(invite.Display);
        foreach (var text in texts)
        {
            foreach (var secret in secrets)
            {
                That(!text.Contains(secret, StringComparison.OrdinalIgnoreCase), "'" + secret + "' appears in nothing the flows report (" + text + ")");
            }
        }

        That(texts.Count > 40, "the probe read what the flows reported (" + texts.Count + " texts)");
    }

    /// <summary>What the client's own types print: their kind, never a token, a ticket, a credential, a code or an origin.</summary>
    internal static void TheClientsTypesPrintOnlyTheirKind()
    {
        var invite = new ServiceInvite("RJ9JMV1V09", 1);
        var values = new object[]
        {
            invite,
            new HostChallenge("AQAAAAB1ZqP4a8QNhigcvrIeP9PCvTJMOC23VpqoqEnDaEoiydNuPZA", 1),
            new HostRegistration(new string('a', 64), "tfh1_CP64OMr4OHh0LqD7rolXdIPbWh3jO81Y6ipv6p3fGX4", new string('b', 64), 1, 30, 90, true, invite),
            new ServiceJoinTicket("tfj1_AcXzkGAnHr0vItm_FVomv0WSdOMbeVFF", new string('a', 64), 1, true, false),
            new RelayCredentials(IceServerSet.Create(new[] { new IceServerAddress("stun.example.org", 3478) }, new[] { new IceServerAddress("turn.example.org", 3478) }, "user-7", "secret-7", 600), 1),
            new ServiceRoute(new string('a', 64), "RJ9JMV1V09", true, true),
            new ControlPlaneRequest(ControlPlaneMethod.Post, "/v1/hosts/" + new string('a', 64) + "/heartbeat", "tfh1_CP64OMr4", Encoding.UTF8.GetBytes("{}"), 64),
            ControlPlaneResponse.Answered(200, Encoding.UTF8.GetBytes("{\"x\":\"203.0.113.9\"}"), true, null),
            ControlPlaneOrigin.Loopback(8787),
            new InternalInvite("RJ9JMV1V09", "RJ9JM-V1V09", "topiaforge://join/RJ9JMV1V09", 1),
            ServiceResult<ServiceInvite>.Ok(invite)
        };
        That(ControlPlaneOrigin.TryParse("https://mp.example.org", out var origin), "an origin parses");
        foreach (var value in values.Append(origin))
        {
            var text = value.ToString() ?? string.Empty;
            foreach (var secret in new[] { "RJ9JM", "AQAAAAB1", "tfh1_", "tfj1_", "user-7", "secret-7", "example.org", "203.0.113.9", "8787", "aaaaaaaa", "heartbeat" })
            {
                That(!text.Contains(secret, StringComparison.Ordinal), value.GetType().Name + " prints no " + secret + " (" + text + ")");
            }
        }

        // The SPI's new types carry nothing shaped like an address; the scan of the whole SPI covers them too.
        foreach (var type in new[] { typeof(InternalInternetStatus), typeof(InternalInvite) })
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var name = member.Name.ToLowerInvariant();
                That(!name.Contains("address") && !name.Contains("endpoint") && !name.Contains("port") && !name.Contains("hostname"), type.Name + "." + member.Name + " is no address");
            }
        }
    }

    private static void Collect(List<string> texts, InternetPeer peer)
    {
        var status = peer.Control.GetStatus();
        texts.Add(status.JoinDetail);
        texts.AddRange(status.ContentProblems);
        texts.Add(status.ToString() ?? string.Empty);
        texts.Add(status.Internet.ToString() ?? string.Empty);
        texts.Add(status.Internet.ListingFailure.ToString());
        texts.Add(status.Internet.Invite?.ToString() ?? string.Empty);
        texts.AddRange(peer.Log.Lines);
        texts.AddRange(peer.Runtime.RecentDiagnostics.Select(record => record.Code + " " + record.Text));
        texts.AddRange(peer.Factory.Diagnostics.Reports.Select(report => report.Message));
        var live = peer.Provider.Live!;
        texts.Add(live.Host?.Internet?.ToString() ?? string.Empty);
        texts.Add(live.Join?.Internet?.Status.ToString() ?? string.Empty);
        texts.Add(live.Join?.Route?.ToString() ?? string.Empty);
        texts.Add(live.Join?.ToString() ?? string.Empty);
    }
}
