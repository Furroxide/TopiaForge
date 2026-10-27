extern alias provider;

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class DeveloperPeerTests
{
    internal static void Run()
    {
        var factory = new SocketNetworkFactory(() => 0, loopbackOnly: true);
        // Check the configured address before opening anything: even a regression
        // in this test must never bind a wildcard socket.
        That(Equals(Field(factory, "bindAddress"), IPAddress.Loopback), "development sockets require loopback");
        That((bool)Field(factory, "withoutIPv6"), "development sockets use IPv4 loopback");
        using (var udp = (IDisposable)typeof(SocketNetworkFactory).GetMethod("OpenGameSocket", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(factory, new object[] { 0 })!)
            That(((IPEndPoint)((Socket)Field(udp, "socket")).LocalEndPoint!).Address.Equals(IPAddress.Loopback), "the game socket binds loopback");
        using (var endpoint = factory.CreateDescriptorEndpoint(0, false)!)
            That(((IPEndPoint)((Socket)Field(endpoint, "listener")).LocalEndPoint!).Address.Equals(IPAddress.Loopback), "the descriptor socket binds loopback");
        That(factory.CreateAnnouncer(default) == null && factory.CreateBrowser(0) == null, "development peers never open discovery");
        var refused = false;
        try { factory.StartTraversal(0, null!, null!); }
        catch (InvalidOperationException) { refused = true; }
        That(refused, "development peers refuse traversal before allocating a socket");

        var inner = new Resolver();
        var resolver = new LoopbackPeerResolver(inner);
        foreach (var host in new[] { "example.invalid", "localhost", "127.0.0.2", "::1", "192.0.2.1" })
        {
            var result = resolver.ResolveAsync(JoinTarget.Direct(host, 27250), true, null, default).GetAwaiter().GetResult();
            That(result.Failure == LiveFailure.DirectNotAllowed && inner.Calls == 0, "non-loopback targets never reach resolution");
        }
        resolver.ResolveAsync(JoinTarget.Direct("127.0.0.1", 27250), true, "trusted", default).GetAwaiter().GetResult();
        That(inner.Calls == 1 && !inner.AllowDirect && inner.Trusted == "trusted", "the local host keeps its pinned identity and cannot permit external resolution");

        var network = new VirtualDatagramNetwork(19001);
        var setup = new LiveSessionsSetup(new MemoryDataFiles(), new TestContent())
        { Factory = new SteppedNetworkFactory(network, "local"), LoopbackOnly = true };
        using var provider = new provider::TopiaForge.Multiplayer.MultiplayerProvider(LiveRig.ProviderId, null, () => network.Now, setup);
        var settings = new HostSettings("Local development", 8)
        { AllowDirect = true, MapPorts = true, Voice = true, ServePackages = true };
        // A provider with no attached world refuses hosting, but privacy is fixed
        // before admission and cannot depend on whether a world is attached.
        try { provider.Live!.StartHosting(settings); } catch (InvalidOperationException) { }
        That(settings.LocalDevelopmentPeers && !settings.AllowDirect && !settings.MapPorts && !settings.Voice && !settings.ServePackages,
            "local development cannot enable publication, forwarding, voice or package serving");
        Console.WriteLine("Development peers: socket binding, discovery refusal, resolver isolation and host privacy passed.");
        DeveloperPeerSocketTests.Run();
    }

    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;

    private sealed class Resolver : IJoinTargetResolver
    {
        internal int Calls;
        internal bool AllowDirect;
        internal string? Trusted;
        public Task<ResolvedTarget> ResolveAsync(JoinTarget target, bool allowDirect, string? trustedServerId, CancellationToken cancellationToken)
        {
            Calls++;
            AllowDirect = allowDirect;
            Trusted = trustedServerId;
            return Task.FromResult(ResolvedTarget.Failed(LiveFailure.DirectNotAllowed, "test"));
        }
    }
}
