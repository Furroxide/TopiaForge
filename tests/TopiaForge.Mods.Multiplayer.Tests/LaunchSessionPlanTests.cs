extern alias provider;

using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The session a launch request V5 asks for, as the provider plans it from the runtime's ticket: host settings, or a
/// join target pinned by the server the launcher verified, so the join never falls back to trust on first use.
/// </summary>
internal static class LaunchSessionPlanTests
{
    private const string ServerId = "c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3";
    private const string DescriptorSha256 = "0d4c3b2a1908f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c";
    private const string PackageDigest = "9a8b7c6d5e4f30211203f4e5d6c7b8a99a8b7c6d5e4f30211203f4e5d6c7b8a9";

    private static LaunchSessionTicket Join(LaunchSessionTicketTarget target, bool allowDirect = false) =>
        new("join", "intent-1", "Charl", allowDirect, 0, false, true, null, target, null);

    private static LaunchSessionTicketTarget Target(string kind, string? host = null, int port = 0, string? source = null, string? invite = null) =>
        new(kind, ServerId, DescriptorSha256, PackageDigest, invite, source, host, port);

    internal static void HostsAndJoinsAreMappedOntoLiveSessions()
    {
        LaunchSessionTicket Host(bool developerMode) => new("host", "intent-2", "Charl", true, 0, true, true, null, null,
            new LaunchSessionTicketHost("Charl's Sandbox", 6, "public", true, true, true), developerMode: developerMode);
        var host = LaunchSessionPlan.FromTicket(Host(developerMode: true), out var unsupported);
        That(unsupported == LaunchSessionUnsupported.None && host!.Host!.Name == "Charl's Sandbox" && host.Host.MaxPlayers == 6 &&
             host.Host.Visibility == ServerVisibility.Public && host.Host.Port == 0 && host.Host.AllowDirect && host.Host.Voice &&
             host.Host.Password == null && host.PasswordRequired && host.ServePackages && host.DeveloperMode && host.Upnp &&
             host.Target == null,
            "a host opens with its options; the password is typed in game, never staged");
        That(!LaunchSessionPlan.FromTicket(Host(developerMode: false), out _)!.ServePackages,
            "only developer mode serves packages, even when the ticket asks");

        var direct = LaunchSessionPlan.FromTicket(Join(Target("direct", "192.168.77.31", 41297)), out unsupported);
        That(unsupported == LaunchSessionUnsupported.None && direct!.Target!.Kind == JoinTargetKind.Direct &&
             direct.Target.ServerId == ServerId && direct.Target.Port == 41297 && !direct.Join!.AllowDirect &&
             direct.Join.DisplayName == "Charl" && direct.ExpectedDescriptorSha256 == DescriptorSha256 &&
             direct.ExpectedPackageDigest == PackageDigest,
            "a direct target is pinned by the server the launcher verified, so trust on first use never runs again");

        var lan = LaunchSessionPlan.FromTicket(Join(Target("listing", source: "lan")), out unsupported);
        That(unsupported == LaunchSessionUnsupported.None && lan!.Target!.Kind == JoinTargetKind.Lan && lan.Target.ServerId == ServerId,
            "a LAN listing is found again on the local network by its server id");

        That(LaunchSessionPlan.FromTicket(Join(Target("invite", invite: "ABCDE12345")), out unsupported) == null &&
             unsupported == LaunchSessionUnsupported.NeedsService, "an invite needs the control plane");
        That(LaunchSessionPlan.FromTicket(Join(Target("listing", source: "internet")), out unsupported) == null &&
             unsupported == LaunchSessionUnsupported.NeedsService, "and so does an Internet listing");
        That(LaunchSessionPlan.FromTicket(null, out unsupported) == null && unsupported == LaunchSessionUnsupported.Invalid,
            "no ticket plans nothing");
        That(LaunchSessionPlan.FromTicket(Join(Target("direct", "not a host!", 41297)), out unsupported) == null &&
             unsupported == LaunchSessionUnsupported.Invalid, "a ticket the provider cannot read plans nothing");
    }

    internal static void APlanNeverShowsItsAddress()
    {
        var plan = LaunchSessionPlan.FromTicket(Join(Target("direct", "203.0.113.77", 47819), allowDirect: true), out _)!;
        foreach (var text in new[] { plan.ToString(), plan.Target!.ToString(), Join(Target("direct", "203.0.113.77", 47819)).ToString() })
        {
            That(!text.Contains("203.0.113.77", StringComparison.Ordinal) && !text.Contains("47819", StringComparison.Ordinal),
                "a launch plan's description names no address (" + text + ")");
        }
    }

    internal static void TheProviderTakesTheOfferedTicketOnce()
    {
        LaunchSessionTickets.Offer(Join(Target("listing", source: "lan")));
        using var provider = new provider::TopiaForge.Multiplayer.MultiplayerProvider("io.github.furroxide.topiaforge.multiplayer");
        var plan = provider.TakeLaunchSession(out var unsupported);
        That(plan != null && unsupported == LaunchSessionUnsupported.None && plan.IntentId == "intent-1", "the provider plans the offered session");
        That(provider.TakeLaunchSession(out unsupported) == null && unsupported == LaunchSessionUnsupported.Invalid, "only once");
    }
}
