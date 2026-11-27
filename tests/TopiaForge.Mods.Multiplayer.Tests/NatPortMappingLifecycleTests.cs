extern alias provider;

using System.Diagnostics;
using System.Net;
using System.Text;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.PortMapping;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The port mapping belongs to hosting, not to the wrapper: it starts only once hosting has started on the network
/// thread, never for a relay-only host (whether the agent or the transport says so), never when hosting could not start,
/// and the agent deletes it when the network stops. A session on a relay-only stack is relay-only, whatever the agent's
/// own option. And the UPnP description parser reads every shape a gateway writes.
/// </summary>
internal static class NatPortMappingLifecycleTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("198.51.100.40");

    internal static void PortMappingFollowsHosting()
    {
        // A network thread that never starts runs no command: nothing is hosted, so nothing is mapped.
        using (var gateway = new LoopbackNatPmpGateway(PublicAddress))
        {
            var (thread, nat, mapper, _) = Build(gateway, false, false, 701);
            using var host = NatHost.Start(thread, nat, Plan(new List<string>()), true, mapper);
            That(nat.Agent.Role == NatRole.None && host.PortMapping == PortMappingState.Off && gateway.Requests.IsEmpty, "a host on a network that never started maps nothing");
            thread.Dispose();
        }

        // A relay-only host never contacts the gateway or looks up the STUN server, whether its agent or its transport is the one that says so.
        foreach (var (agentRelayOnly, stackRelayOnly, seed) in new[] { (true, false, 702), (false, true, 703), (true, true, 704) })
        {
            var name = (agentRelayOnly ? "a relay-only agent" : "an agent that allows direct play") + " on " + (stackRelayOnly ? "a relay-only stack" : "a direct stack");
            using var gateway = new LoopbackNatPmpGateway(PublicAddress);
            var lookups = new List<string>();
            var (thread, nat, mapper, _) = Build(gateway, agentRelayOnly, stackRelayOnly, seed);
            thread.Start();
            using var host = NatHost.Start(thread, nat, Plan(lookups), true, mapper);
            That(Settled(thread) && nat.Agent.Role == NatRole.Host, name + ": hosting starts");
            That(nat.Agent.RelayOnly, name + ": hosts relay-only");
            That(host.PortMapping == PortMappingState.Off && gateway.Requests.IsEmpty, name + ": and never contacts the gateway");
            lock (lookups) That(lookups.All(lookup => lookup == "turn.example.test"), name + ": nor looks up any name but its TURN server's (" + string.Join(", ", lookups) + ")");
            thread.Stop(0);
        }

        // A host that shows its address maps once hosting has started, and stopping the network deletes the mapping,
        // though nobody disposed the host.
        using (var gateway = new LoopbackNatPmpGateway(PublicAddress))
        {
            var (thread, nat, mapper, _) = Build(gateway, false, false, 705);
            thread.Start();
            var host = NatHost.Start(thread, nat, Plan(new List<string>()), true, mapper);
            That(Wait(() => host.PortMapping == PortMappingState.Mapped), "a host that shows its address maps its port once hosting runs");
            thread.Stop(300_000);
            That(Wait(() => mapper.State == PortMappingState.Stopped && gateway.Requests.Any(request => request.Opcode == 1 && request.Lifetime == 0)),
                "stopping the network deletes the mapping without the host being disposed");
        }

        // A host the agent cannot start, because it already runs a join, maps nothing.
        using (var gateway = new LoopbackNatPmpGateway(PublicAddress))
        {
            var (thread, nat, mapper, _) = Build(gateway, false, false, 706);
            thread.Start();
            using var join = NatJoin.Start(thread, nat, Plan(new List<string>()), null);
            That(Wait(() => nat.Agent.Role == NatRole.Joiner), "the agent runs a join");
            using var host = NatHost.Start(thread, nat, Plan(new List<string>()), true, mapper);
            That(Settled(thread) && nat.Agent.Role == NatRole.Joiner, "the host's start ran, and threw");
            That(host.PortMapping == PortMappingState.Off && gateway.Requests.IsEmpty, "a host whose start throws maps nothing");
            thread.Stop(0);
        }

        // Disposing the host deletes the mapping while the network carries on.
        using (var gateway = new LoopbackNatPmpGateway(PublicAddress))
        {
            var (thread, nat, mapper, _) = Build(gateway, false, false, 707);
            thread.Start();
            var host = NatHost.Start(thread, nat, Plan(new List<string>()), true, mapper);
            That(Wait(() => host.PortMapping == PortMappingState.Mapped), "the port is mapped");
            host.Dispose();
            That(Wait(() => mapper.State == PortMappingState.Stopped && gateway.Requests.Any(request => request.Opcode == 1 && request.Lifetime == 0)),
                "disposing the host deletes it");
            thread.Stop(0);
        }
    }

    internal static void AJoinOnARelayOnlyStackIsRelayOnly()
    {
        using var gateway = new LoopbackNatPmpGateway(PublicAddress);
        var lookups = new List<string>();
        var (thread, nat, _, _) = Build(gateway, false, true, 708);
        thread.Start();
        using var join = NatJoin.Start(thread, nat, Plan(lookups), null);
        That(Settled(thread) && nat.Agent.Role == NatRole.Joiner, "the join starts");
        That(nat.Agent.RelayOnly && nat.Agent.SocketBase == null, "an agent that allows direct play joins relay-only on a relay-only stack, with no socket base");
        lock (lookups) That(lookups.All(lookup => lookup == "turn.example.test"), "and never looks up the STUN server (" + string.Join(", ", lookups) + ")");
        thread.Stop(0);
    }

    internal static void UpnpDescriptionsParseInEveryShape()
    {
        var root = new Uri("http://192.168.1.1:5000/rootDesc.xml");
        var children = new[]
        {
            "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>",
            "<serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>",
            "<controlURL>/ctl/IPConn</controlURL>",
            "<eventSubURL>/evt/IPConn</eventSubURL>",
            "<SCPDURL>/WANIPCn.xml</SCPDURL>"
        };
        var orders = 0;
        foreach (var order in Permutations(Enumerable.Range(0, children.Length).ToArray()))
        {
            orders++;
            foreach (var minified in new[] { true, false })
            {
                foreach (var urlBase in new[] { false, true })
                {
                    var xml = Description(order.Select(index => children[index]).ToArray(), minified, urlBase);
                    var shape = (minified ? "minified" : "pretty-printed") + (urlBase ? " with URLBase" : "") + " in order " + string.Join("", order);
                    That(UpnpIgdClient.TryParseDescription(Encoding.UTF8.GetBytes(xml), root, out var control, out var type), "a description " + shape + " parses");
                    That(type == "urn:schemas-upnp-org:service:WANIPConnection:1" && control.ToString() == "http://192.168.1.1:5000/ctl/IPConn",
                        "and names the WAN IP service's control URL (" + shape + ": " + type + ")");
                }
            }
        }

        That(orders == 120, "every order of a service's five children was read (" + orders + ")");
        var afterEmpty = "<root><device><serviceList><service/><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType>" +
                         "<controlURL>/ctl/IPConn</controlURL></service></serviceList></device></root>";
        That(UpnpIgdClient.TryParseDescription(Encoding.UTF8.GetBytes(afterEmpty), root, out var found, out _) && found.AbsolutePath == "/ctl/IPConn",
            "an empty service element before the WAN one changes nothing");
    }

    /// <summary>A gateway's description: a Layer 3 forwarding service, then the WAN device holding the WAN IP connection service.</summary>
    private static string Description(string[] wanChildren, bool minified, bool urlBase)
    {
        var newline = minified ? string.Empty : "\r\n";
        string Indent(int depth) => minified ? string.Empty : new string(' ', depth * 2);
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\"?>").Append(newline);
        builder.Append("<root xmlns=\"urn:schemas-upnp-org:device-1-0\">").Append(newline);
        builder.Append(Indent(1)).Append("<specVersion><major>1</major><minor>0</minor></specVersion>").Append(newline);
        if (urlBase) builder.Append(Indent(1)).Append("<URLBase>http://192.168.1.1:5000</URLBase>").Append(newline);
        builder.Append(Indent(1)).Append("<device>").Append(newline);
        builder.Append(Indent(2)).Append("<deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>").Append(newline);
        builder.Append(Indent(2)).Append("<serviceList>").Append(newline);
        builder.Append(Indent(3)).Append("<service>").Append(newline);
        foreach (var child in new[] { "<serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType>", "<serviceId>urn:upnp-org:serviceId:L3Forwarding1</serviceId>", "<controlURL>/ctl/L3F</controlURL>", "<eventSubURL>/evt/L3F</eventSubURL>", "<SCPDURL>/L3F.xml</SCPDURL>" })
        {
            builder.Append(Indent(4)).Append(child).Append(newline);
        }

        builder.Append(Indent(3)).Append("</service>").Append(newline);
        builder.Append(Indent(2)).Append("</serviceList>").Append(newline);
        builder.Append(Indent(2)).Append("<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType><deviceList><device>").Append(newline);
        builder.Append(Indent(3)).Append("<deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:1</deviceType>").Append(newline);
        builder.Append(Indent(3)).Append("<serviceList>").Append(newline);
        builder.Append(Indent(4)).Append("<service>").Append(newline);
        foreach (var child in wanChildren) builder.Append(Indent(5)).Append(child).Append(newline);
        builder.Append(Indent(4)).Append("</service>").Append(newline);
        builder.Append(Indent(3)).Append("</serviceList>").Append(newline);
        builder.Append(Indent(2)).Append("</device></deviceList></device></deviceList>").Append(newline);
        builder.Append(Indent(1)).Append("</device>").Append(newline);
        builder.Append("</root>").Append(newline);
        return builder.ToString();
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (var index = 0; index < items.Length; index++)
        {
            var rest = items.Where((_, position) => position != index).ToArray();
            foreach (var tail in Permutations(rest)) yield return new[] { items[index] }.Concat(tail).ToArray();
        }
    }

    /// <summary>A NAT layer on a socket that sends nowhere, on a network thread not yet started, with a NAT-PMP mapper aimed at the gateway.</summary>
    private static (NetworkThread Thread, NatPlumbing Nat, PortMapper Mapper, NetworkStack Stack) Build(LoopbackNatPmpGateway gateway, bool agentRelayOnly, bool stackRelayOnly, int seed)
    {
        var mapper = new PortMapper(new NatPmpClient(gateway.EndPoint, IPAddress.Loopback), null);
        var nat = NatPlumbing.Wrap(new NatPrivacyTests.RecordingSocket(), new NatAgentOptions { RelayOnly = agentRelayOnly, Random = new SeededTransportRandom(seed) }, () => 0);
        var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(seed, 1), new TransportOptions { RelayOnly = stackRelayOnly, Random = new SeededTransportRandom(seed + 1) }, 0));
        var thread = new NetworkThread("TopiaForge test host " + seed, nat.Socket, stack, () => 0);
        thread.Attach(nat.Service);
        return (thread, nat, mapper, stack);
    }

    /// <summary>A plan with a STUN and a TURN server and no signaling; every name asked for is recorded.</summary>
    private static NatSessionPlan Plan(List<string> lookups) =>
        new(IceServerSet.Create(new[] { new IceServerAddress("stun.example.test", 3478) }, new[] { new IceServerAddress("turn.example.test", 3478) }, "user", "secret", 600),
            () => throw new InvalidOperationException("no signaling in this test"), host =>
            {
                lock (lookups) lookups.Add(host);
                return Task.FromResult(Array.Empty<IPAddress>());
            })
        {
            LocalAddresses = new[] { IPAddress.Parse("192.168.1.10") },
            LocalPort = 27250
        };

    /// <summary>Waits until every command posted so far has run: commands run in order, so a marker posted now runs after them.</summary>
    private static bool Settled(NetworkThread thread)
    {
        using var marker = new ManualResetEventSlim();
        thread.Post((_, _) => marker.Set());
        return marker.Wait(5_000);
    }

    private static bool Wait(Func<bool> condition, int milliseconds = 5_000)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }

        return condition();
    }
}
