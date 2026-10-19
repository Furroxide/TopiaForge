extern alias provider;

using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ProviderJoinTarget = provider::TopiaForge.Multiplayer.ProviderJoinTarget;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The session a launch asks for, as the provider plans it from the runtime's launch session: host settings, or a join
/// target pinned by the server and descriptor the launcher verified, so the join never falls back to trust on first use
/// and refuses a server that changed since. Planning goes through the internal control, and only once a runtime attached.
/// </summary>
internal static class LaunchSessionPlanTests
{
    internal const string ServerId = "c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3";
    internal const string DescriptorSha256 = "0d4c3b2a1908f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c";

    private static InternalLaunchSession Join(InternalLaunchTarget target, bool allowDirect = false) => new("intent-1", "Sam", allowDirect, target: target);

    private static InternalLaunchSession Host(bool developerMode, InternalSessionVisibility visibility = InternalSessionVisibility.Public, int port = 0) =>
        new("intent-2", "Sam", true, new InternalLaunchHost("Sam's Sandbox", 6, visibility, true, true, true))
        {
            Port = port,
            RouterForwarding = true,
            DeveloperMode = developerMode
        };

    internal static void HostsAndJoinsAreMappedOntoLiveSessions()
    {
        var host = LaunchSessionPlan.From(Host(developerMode: true, port: 27260), hasService: true, out var unsupported);
        That(unsupported == LaunchSessionUnsupported.None && host!.Host!.Name == "Sam's Sandbox" && host.Host.MaxPlayers == 6 &&
             host.Host.Visibility == InternalSessionVisibility.Public && host.Host.AllowDirect && host.Host.Voice && host.Host.RouterForwarding &&
             host.Host.Password == null && host.PasswordRequired && host.ServePackages && host.DeveloperMode && host.Port == 27260 &&
             host.AllowDirect && host.DisplayName == "Sam" && host.IntentId == "intent-2" && host.Target == null,
            "a host opens with its options and its own game port; the password is typed in game, never staged");
        var live = new HostSettings("Changed since", 2) { AllowDirect = false, FallBackToAnyPort = true };
        host!.ApplyTo(live);
        That(live.Port == 27260 && !live.FallBackToAnyPort && live.AllowDirect && live.MapPorts,
            "hosting the plan uses exactly the launch's port and keeps the launch's own privacy");
        That(!LaunchSessionPlan.From(Host(developerMode: false), true, out _)!.ServePackages,
            "only developer mode serves packages, even when the session asks");
        That(LaunchSessionPlan.From(Host(developerMode: false), hasService: false, out unsupported) == null && unsupported == LaunchSessionUnsupported.NeedsService,
            "a public host needs the TopiaForge service");
        That(LaunchSessionPlan.From(Host(false, InternalSessionVisibility.Lan), hasService: false, out _) != null, "and a LAN host does not");

        var direct = LaunchSessionPlan.From(Join(InternalLaunchTarget.Direct("192.168.77.31", 41297, ServerId, DescriptorSha256)), false, out unsupported);
        var network = direct!.Target!.NetworkTarget;
        That(unsupported == LaunchSessionUnsupported.None && network.Kind == JoinTargetKind.Direct && network.ServerId == ServerId && network.Port == 41297 &&
             direct.Target.Kind == InternalJoinTargetKind.Direct && direct.Target.ServerId == ServerId && direct.Target.PlannedAllowDirect == false &&
             direct.DisplayName == "Sam" && direct.ExpectedDescriptorSha256 == DescriptorSha256 && direct.Host == null,
            "a direct target is pinned by the server and descriptor the launcher verified, so trust on first use never runs again");

        var lan = LaunchSessionPlan.From(Join(InternalLaunchTarget.Listing(ServerId, DescriptorSha256, lan: true)), false, out unsupported);
        That(unsupported == LaunchSessionUnsupported.None && lan!.Target!.NetworkTarget.Kind == JoinTargetKind.Lan && lan.Target.ServerId == ServerId,
            "a LAN listing is found again on the local network by its server id");

        That(LaunchSessionPlan.From(Join(InternalLaunchTarget.Invite("ABCDE12345", ServerId, DescriptorSha256)), false, out unsupported) == null &&
             unsupported == LaunchSessionUnsupported.NeedsService, "an invite needs the TopiaForge service");
        That(LaunchSessionPlan.From(Join(InternalLaunchTarget.Listing(ServerId, DescriptorSha256, lan: false)), false, out unsupported) == null &&
             unsupported == LaunchSessionUnsupported.NeedsService, "and so does an Internet listing");
        var invite = LaunchSessionPlan.From(Join(InternalLaunchTarget.Invite("ABCDE12345", ServerId, DescriptorSha256)), true, out _);
        That(invite!.Target!.NetworkTarget.Kind == JoinTargetKind.Invite && invite.Target.InviteCode == "ABCDE12345" && invite.Target.NetworkTarget.ServerId == ServerId &&
             invite.Target.ToString() == "invite", "an invite is planned through the service, pinned to the server, and never printed");
        That(LaunchSessionPlan.From(null, true, out unsupported) == null && unsupported == LaunchSessionUnsupported.Invalid, "no session plans nothing");
        That(LaunchSessionPlan.From(Join(InternalLaunchTarget.Direct("not a host!", 41297, ServerId, DescriptorSha256)), true, out unsupported) == null &&
             unsupported == LaunchSessionUnsupported.Invalid, "a session the network layer cannot read plans nothing");
    }

    internal static void APlanNeverShowsItsAddress()
    {
        var session = Join(InternalLaunchTarget.Direct("203.0.113.77", 47819, ServerId, DescriptorSha256), allowDirect: true);
        var plan = LaunchSessionPlan.From(session, false, out _)!;
        foreach (var text in new[] { plan.ToString(), plan.Target!.ToString(), plan.Target.Description, session.ToString(), session.Target!.ToString() })
        {
            That(!text.Contains("203.0.113.77", StringComparison.Ordinal) && !text.Contains("47819", StringComparison.Ordinal),
                "a launch session and its plan name no address (" + text + ")");
        }
    }

    /// <summary>
    /// The runtime hands the session to the provider as a call on its internal control, which only the reserved provider
    /// publishes: nothing static holds it. The control plans only for an attached runtime, and keeps the launch's privacy
    /// on the hosting and the join it plans whatever settings they start with.
    /// </summary>
    internal static void TheControlPlansALaunchedSession()
    {
        using var rig = new ControlRig(7_711, PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a'));
        var detached = new ControlPeer("detached", rig.Network, rig.Resolver, rig.PackageRoot, Array.Empty<PackageSpec>());
        var refused = detached.Control.PlanLaunchSession(Join(InternalLaunchTarget.Listing(ServerId, DescriptorSha256, lan: true)));
        That(!refused.Succeeded && refused.ErrorCode == ModErrorCode.Unavailable, "without an attached runtime nothing is planned");
        detached.Control.Dispose();
        detached.Provider.Dispose();

        var peer = rig.AddPeer("joiner", PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a'));
        var planned = peer.Control.PlanLaunchSession(Join(InternalLaunchTarget.Direct("192.168.1.20", 27250, ServerId, DescriptorSha256)));
        That(planned.TryGetValue(out var plan) && plan!.Target is ProviderJoinTarget target && target.ExpectedDescriptorSha256 == DescriptorSha256 &&
             plan.Target.Kind == InternalJoinTargetKind.Direct && !plan.AllowDirect && plan.Host == null,
            "a join is planned as a target pinned by the descriptor the launcher verified");
        var invite = peer.Control.PlanLaunchSession(Join(InternalLaunchTarget.Invite("ABCDE12345", ServerId, DescriptorSha256)));
        That(!invite.Succeeded && invite.ErrorCode == ModErrorCode.Unavailable && invite.ErrorMessage.Contains("TopiaForge service", StringComparison.Ordinal),
            "an invite without a configured service is refused in this game's words");
        var unreadable = peer.Control.PlanLaunchSession(Join(InternalLaunchTarget.Direct("-bad-", 27250, ServerId, DescriptorSha256)));
        That(!unreadable.Succeeded && unreadable.ErrorCode == ModErrorCode.InvalidArgument, "a target the network layer cannot read is refused");
        var hosted = rig.Host.Control.PlanLaunchSession(Host(developerMode: false, InternalSessionVisibility.Lan));
        That(hosted.TryGetValue(out var hostPlan) && hostPlan!.Host != null && hostPlan.PasswordRequired && hostPlan.Target == null,
            "a host is planned as its own settings");
    }

    /// <summary>
    /// Every valid launch request V5 of the shared fixtures, read as the game reads it, offered as the runtime offers it and
    /// planned by a real provider: with its own privacy, port and options, pinned to the server and descriptor it names,
    /// and through the service when it needs one; without a service only those plan.
    /// </summary>
    internal static void EveryLaunchFixtureIsPlanned()
    {
        var cases = 0;
        foreach (var item in NetworkFixtures.Load(Path.Combine("launch-v5", "cases.json")).GetProperty("valid").EnumerateArray())
        {
            cases++;
            var id = item.Text("id");
            var session = TopiaForge.ModManager.Core.LaunchTransportJson.ReadProfileV5(item.Text("bytes")).Session;
            var offered = TopiaForge.ModManager.LaunchSessionOffer.ToSession(session);
            foreach (var service in new[] { true, false })
            {
                using var planner = new Planner(service);
                var planned = planner.Control.PlanLaunchSession(offered);
                var needsService = session.Target?.NeedsService == true || session.Host?.Visibility is "public" or "invite";
                That(planned.Succeeded == (service || !needsService), id + (service ? " plans" : " plans without a service only when it needs none") + " (" + planned.ErrorMessage + ")");
                if (!planned.TryGetValue(out var plan) || plan == null) continue;
                var consented = session.Transport.Privacy == "direct" && session.Transport.DirectConsent?.IntentId == session.IntentId;
                That(plan.AllowDirect == consented && plan.IntentId == session.IntentId && plan.DisplayName == session.DisplayName,
                    id + " keeps its own privacy: direct only with the player's consent for it");
                var provided = (LaunchSessionPlan)plan;
                if (session.Host != null)
                {
                    That(plan.Host!.Name == session.Host.Name && plan.Host.MaxPlayers == session.Host.MaxPlayers && plan.Host.AllowDirect == consented &&
                         plan.PasswordRequired == session.Host.PasswordRequired && provided.Port == session.Transport.Port &&
                         provided.ServePackages == (session.Host.ServePackages && session.DeveloperMode), id + " hosts with its own options and port");
                    continue;
                }

                var target = (ProviderJoinTarget)plan.Target!;
                var kind = session.Target!.Kind == "invite" ? InternalJoinTargetKind.Invite
                    : session.Target.Kind == "direct" ? InternalJoinTargetKind.Direct
                    : session.Target.Source == "lan" ? InternalJoinTargetKind.Lan : InternalJoinTargetKind.ListedServer;
                That(target.Kind == kind && target.ServerId == session.Target.ServerId && target.ExpectedDescriptorSha256 == session.Target.DescriptorSha256 &&
                     target.PlannedAllowDirect == consented, id + " joins the server it names, pinned to the descriptor the launcher checked");
                That(session.Target.Host == null || (!target.Description.Contains(session.Target.Host, StringComparison.OrdinalIgnoreCase) &&
                     !(target.ToString() ?? string.Empty).Contains(session.Target.Host, StringComparison.OrdinalIgnoreCase)), id + "'s plan names no address");
            }
        }

        That(cases == 11, "every valid launch fixture is planned (" + cases + ")");
    }

    /// <summary>A provider whose runtime reaches the TopiaForge service, or reaches none.</summary>
    private sealed class Planner : IDisposable
    {
        private readonly provider::TopiaForge.Multiplayer.MultiplayerProvider provider;

        internal Planner(bool service)
        {
            var content = new provider::TopiaForge.Multiplayer.RuntimeContent("2545", "0.2.0");
            var diagnostics = new provider::TopiaForge.Multiplayer.RoutedDiagnostics(null);
            var network = new VirtualDatagramNetwork(7_712);
            provider = new provider::TopiaForge.Multiplayer.MultiplayerProvider(LiveRig.ProviderId, diagnostics, () => network.Now,
                new LiveSessionsSetup(new MemoryDataFiles(), content) { Factory = new SteppedNetworkFactory(network, "planner") });
            Control = new provider::TopiaForge.Multiplayer.InternalControl(provider, content, diagnostics);
            var runtime = new TopiaForge.ModManager.MultiplayerRuntimeHost("2545", "0.2.0", new RecordingMultiplayerLog());
            runtime.ConfigureServiceOrigin(service ? "https://mp.topiaforge.dev" : null, null);
            That(Control.AttachRuntime(runtime), "the runtime attaches");
        }

        internal provider::TopiaForge.Multiplayer.InternalControl Control { get; }

        public void Dispose()
        {
            Control.Dispose();
            provider.Dispose();
        }
    }
}
