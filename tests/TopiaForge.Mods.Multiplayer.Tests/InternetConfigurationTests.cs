extern alias provider;

using System.Net;
using System.Net.Sockets;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.ControlPlane;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Where the TopiaForge service comes from and when it is reached: a LAN host never contacts it, a session launch's own
/// origin comes before the manager's, a launch to an invite or an Internet listing is planned through it, and the
/// signaling socket, like the HTTPS client, never goes through a proxy the system or the environment names.
/// </summary>
internal static class InternetConfigurationTests
{
    private const string ServerId = "c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3";
    private const string DescriptorSha256 = "0d4c3b2a1908f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c4b3a2918f7e6d5c";
    private const string PackageDigest = "9a8b7c6d5e4f30211203f4e5d6c7b8a99a8b7c6d5e4f30211203f4e5d6c7b8a9";

    internal static void ALanHostNeverContactsTheService()
    {
        using var rig = new InternetRig(7_601);
        var host = rig.AddPeer("host", NatBehaviour.FullCone, "198.51.100.250", "192.168.1.250");
        That(host.Internet.ServiceConfigured, "the service is configured");
        That(host.Control.StartHosting(new InternalHostSettings("Sam's Sandbox", 4, InternalSessionVisibility.Lan)).Succeeded, "LAN hosting starts");
        That(rig.Run(() => host.Status.HostPhase == InternalHostPhase.Hosting, 5_000_000), "and serves");
        rig.Run(() => false, 60_000_000);
        That(rig.Service.Requests.Count == 0 && host.Factory.Networks.Count == 0 && host.Factory.Plain.Count == 1,
            "a LAN host binds its plain socket and asks the service nothing, ever");
        That(host.Internet.ListingPhase == InternalListingPhase.Off && host.Internet.Invite == null, "and has no listing or invite to show");
    }

    /// <summary>The runtime host names the service the provider reaches: a session launch's origin before the manager's.</summary>
    internal static void ASessionLaunchsOriginComesFirst()
    {
        var content = new provider::TopiaForge.Multiplayer.RuntimeContent("2545", "0.2.0");
        var diagnostics = new provider::TopiaForge.Multiplayer.RoutedDiagnostics(null);
        var network = new VirtualDatagramNetwork(7_602);
        var cases = new (string? Launch, string? Configured, string? Reached)[]
        {
            ("https://launch.example.org", "https://manager.example.org", "https://launch.example.org"),
            (null, "https://manager.example.org", "https://manager.example.org"),
            ("https://launch.example.org", null, "https://launch.example.org"),
            (null, null, null)
        };
        foreach (var (launch, configured, reached) in cases)
        {
            using var provider = new provider::TopiaForge.Multiplayer.MultiplayerProvider(LiveRig.ProviderId, diagnostics, () => network.Now,
                new LiveSessionsSetup(new MemoryDataFiles(), content) { Factory = new SteppedNetworkFactory(network, "launch") });
            using var control = new provider::TopiaForge.Multiplayer.InternalControl(provider, content, diagnostics);
            var runtime = new TopiaForge.ModManager.MultiplayerRuntimeHost("2545", "0.2.0", new RecordingMultiplayerLog());
            runtime.ConfigureServiceOrigin(launch, configured);
            That(control.AttachRuntime(runtime), "the runtime attaches");
            That(provider.Live!.Services?.Origin?.Text == reached, "a launch naming " + (launch ?? "no service") + " and a configuration naming " +
                                                                    (configured ?? "none") + " reach " + (reached ?? "nothing"));
            var invite = control.PlanLaunchSession(new InternalLaunchSession("intent-7", "Sam", false,
                target: InternalLaunchTarget.Invite("ABCDE12345", ServerId, DescriptorSha256)));
            That(invite.Succeeded == (reached != null), "and a launched invite is planned exactly when a service is reached");
        }
    }

    internal static void ALaunchToAnInviteOrAListingGoesThroughTheService()
    {
        InternalLaunchSession Join(InternalLaunchTarget target) => new("intent-8", "Sam", false, target: target);

        var invite = LaunchSessionPlan.From(Join(InternalLaunchTarget.Invite("ABCDE12345", ServerId, DescriptorSha256)), true, out var unsupported);
        var target = invite!.Target!.NetworkTarget;
        That(unsupported == LaunchSessionUnsupported.None && target.Kind == JoinTargetKind.Invite && target.InviteCode == "ABCDE12345" &&
             target.ServerId == ServerId && target.ToString() == "invite" && invite.ExpectedDescriptorSha256 == DescriptorSha256,
            "an invite is planned through the service, pinned to the server and descriptor the launcher verified, and never printed");
        var listing = LaunchSessionPlan.From(Join(InternalLaunchTarget.Listing(ServerId, DescriptorSha256, lan: false)), true, out unsupported);
        That(unsupported == LaunchSessionUnsupported.None && listing!.Target!.NetworkTarget.Kind == JoinTargetKind.Listed && listing.Target.ServerId == ServerId,
            "an Internet listing is found again by its server id");
        That(LaunchSessionPlan.From(Join(InternalLaunchTarget.Invite("ABCDE12345", ServerId, DescriptorSha256)), false, out unsupported) == null &&
             unsupported == LaunchSessionUnsupported.NeedsService, "without a service an invite still cannot be planned");
    }

    /// <summary>
    /// A proxy that would take every request, named by the system and by the environment: the signaling socket must reach
    /// the room directly all the same, since it opens over the HTTPS client's own connection.
    /// </summary>
    internal static void SignalingNeverAsksAProxy()
    {
        using var proxy = LoopbackHttpServer.Answering(LoopbackHttpServer.Answer(502, null));
        using var room = new LoopbackWebSocketServer();
        var names = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" };
        var exceptions = new[] { "NO_PROXY", "no_proxy" };
        var saved = names.Concat(exceptions).ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var savedDefault = System.Net.Http.HttpClient.DefaultProxy;
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, "http://127.0.0.1:" + proxy.Port);

            // No exception the machine names may let loopback past the proxy, or a client that asked one would pass.
            foreach (var name in exceptions) Environment.SetEnvironmentVariable(name, null);
            System.Net.Http.HttpClient.DefaultProxy = new GreedyProxy(new Uri("http://127.0.0.1:" + proxy.Port));
            using var client = SignalingUpgrade.Open(new HttpsControlPlaneTransport(ControlPlaneOrigin.Loopback(room.Port)), ServerId, "tfj1_ticket");
            var opened = SpinWait.SpinUntil(() => client.State != SignalingState.Connecting, TimeSpan.FromSeconds(10));
            That(opened && client.State == SignalingState.Open && room.Upgraded, "the room is reached directly (" + client.State + ", " + client.CloseReason + ")");
            That(proxy.Connections == 0, "and the proxy the system or the environment names is never asked");
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            System.Net.Http.HttpClient.DefaultProxy = savedDefault;
        }

        // By construction: neither the HTTPS client nor the signaling client names the runtime's WebSocket client, whose
        // handshake follows each runtime's own proxy and redirect rules.
        foreach (var folder in new[] { "ControlPlane", Path.Combine("Nat", "Signaling") })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(NetworkFixtures.RepositoryRoot(), "mods", "TopiaForge.Multiplayer", folder), "*.cs"))
            {
                That(!System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file), "\\bClientWebSocket\\b"), Path.GetFileName(file) + " never names ClientWebSocket");
            }
        }
    }

    private sealed class GreedyProxy : IWebProxy
    {
        private readonly Uri address;

        internal GreedyProxy(Uri address)
        {
            this.address = address;
        }

        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => address;

        public bool IsBypassed(Uri host) => false;
    }
}
