extern alias provider;

using System.Diagnostics;
using System.Net;
using System.Reflection;
using provider::TopiaForge.Multiplayer.Network;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class DeveloperPeerSocketTests
{
    internal static void Run()
    {
        foreach (var count in new[] { 2, 8 }) Run(count);
    }

    private static void Run(int count)
    {
        var peers = new List<Provider>();
        try
        {
            for (var index = 0; index < count; index++)
            {
                var peer = new Provider(LiveRig.ProviderId, live: new LiveSessionsSetup(new MemoryDataFiles(), new TestContent())
                { LoopbackOnly = true, Voice = false });
                peers.Add(peer);
                // Guard before any socket opens, including when production is mutated.
                var factory = Field(peer.Live!, "factory");
                That(Equals(Field(factory, "bindAddress"), IPAddress.Loopback), "the composed provider must bind loopback before opening sockets");
                That((bool)Field(factory, "loopbackOnly"), "the composed provider must disable external services");
                peer.Live!.PrepareAsync();
            }
            Pump(peers, () => peers.All(peer => peer.Live!.IsReady), "all peer identities load");
            var host = peers[0];
            host.Live!.StartHosting(new HostSettings("Local fixture", count) { Port = 0 });
            Pump(peers, () => host.Live.HostPhase == HostPhase.Hosting, "local host binds and signs its descriptor");
            var port = host.Live.Host!.Port;
            var serverId = host.Live.Host.Descriptor!.Descriptor.ServerId;
            var joins = new List<LiveJoin>();
            foreach (var peer in peers.Skip(1))
            {
                joins.Add(peer.Live!.StartJoin(JoinTarget.Direct("127.0.0.1", port, serverId), new JoinSettings("Peer")));
            }
            Pump(peers, () => joins.All(join => join.Phase == JoinPhase.InSession), "every peer joins through the real loopback resolver and admission");
            That(host.Snapshot.Participants.Count(participant => participant.IsConnected) == count, "the host admits every local peer");
            var ports = peers.Select(peer => ((INetworkControl)Field(peer == host ? (object)host.Live.Host : peer.Live!.Join!, "network")).LocalPort).ToArray();
            That(ports.Distinct().Count() == count, "every peer owns a distinct UDP port");
            Console.WriteLine($"Development loopback integration: {count} peers, distinct ports, signed descriptor and admission passed.");
        }
        finally
        {
            foreach (var peer in peers.AsEnumerable().Reverse()) peer.Dispose();
        }
    }

    private static void Pump(List<Provider> peers, Func<bool> done, string assertion)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            foreach (var peer in peers) peer.Pump();
            if (done()) return;
            Thread.Sleep(2);
        }
        throw new InvalidOperationException("Assertion failed: " + assertion + "; " + string.Join(", ", peers.Select(peer => peer.Live!.Join?.Phase.ToString() ?? peer.Live!.HostPhase.ToString())));
    }

    private static object Field(object value, string name) => value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
}
