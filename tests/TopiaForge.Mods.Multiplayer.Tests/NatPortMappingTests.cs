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

/// <summary>Port mapping, best effort: NAT-PMP and UPnP against loopback gateways, hour-long leases, renewal, deletion, and when not to map.</summary>
internal static class NatPortMappingTests
{
    private static readonly IPAddress PublicAddress = IPAddress.Parse("198.51.100.40");

    internal static void NatPmpFramesFollowRfc6886()
    {
        That(NatPmpClient.EncodeAddressRequest().SequenceEqual(new byte[] { 0, 0 }), "the external address request is version 0, opcode 0");
        That(NatPmpClient.EncodeMapRequest(27250, 27250, 3600).SequenceEqual(new byte[] { 0, 1, 0, 0, 0x6A, 0x72, 0x6A, 0x72, 0, 0, 0x0E, 0x10 }),
            "a UDP mapping request is version, opcode 1, two zero bytes, the ports and the lifetime, big-endian");
        That(NatPmpClient.EncodeMapRequest(27250, 0, 0).Skip(6).All(value => value == 0), "a deletion asks for port 0 and lifetime 0");
        var answer = new byte[] { 0, 129, 0, 0, 0, 0, 3, 232, 0x6A, 0x72, 0x6A, 0x73, 0, 0, 0x0E, 0x10 };
        That(NatPmpClient.TryDecode(answer, 16, NatPmpClient.UdpMapOpcode, out var mapping) && mapping.Succeeded && mapping.Epoch == 1000 &&
             mapping.InternalPort == 27250 && mapping.ExternalPort == 27251 && mapping.Lifetime == 3600, "a mapping answer reads field by field");
        That(!NatPmpClient.TryDecode(answer, 15, NatPmpClient.UdpMapOpcode, out _), "a short answer is refused");
        answer[0] = 1;
        That(!NatPmpClient.TryDecode(answer, 16, NatPmpClient.UdpMapOpcode, out _), "so is another version");
        answer[0] = 0;
        That(!NatPmpClient.TryDecode(answer, 16, NatPmpClient.AddressOpcode, out _), "and an answer to another request");
        That(!NatPmpClient.TryDecode(null!, 0, 0, out _), "and nothing at all");
    }

    internal static void NatPmpMapsRenewsAndDeletes()
    {
        using var gateway = new LoopbackNatPmpGateway(PublicAddress) { SendDecoyFirst = true };
        var delays = new List<TimeSpan>();
        var renew = new TaskCompletionSource();
        var mapper = new PortMapper(new NatPmpClient(gateway.EndPoint, IPAddress.Loopback), null, (wait, token) =>
        {
            delays.Add(wait);
            return delays.Count == 1 ? renew.Task : Task.Delay(Timeout.Infinite, token);
        });
        IPEndPoint? mapped = null;
        mapper.Start(27250, endpoint => mapped = endpoint);
        That(Wait(() => mapped != null), "the gateway maps the port");
        That(mapped!.Equals(new IPEndPoint(PublicAddress, 27250)) && mapper.Protocol == PortMappingProtocol.NatPmp && mapper.State == PortMappingState.Mapped,
            "to its external address, and not the forged one a decoy sent first");
        That(gateway.Requests.Any(request => request.Opcode == 1 && request.Internal == 27250 && request.Suggested == 27250 && request.Lifetime == 3600), "asking for an hour's lease");
        That(Wait(() => delays.Count == 1) && delays[0] == TimeSpan.FromSeconds(1800), "renewal waits half the lease (" + string.Join(",", delays) + ")");
        renew.SetResult();
        That(Wait(() => gateway.Requests.Count(request => request.Opcode == 1 && request.Lifetime == 3600) == 2), "and asks again for the same port");
        mapper.Stop().Wait(5000);
        That(gateway.Requests.Any(request => request.Opcode == 1 && request.Lifetime == 0 && request.Suggested == 0), "stopping deletes the mapping");
        That(mapper.State == PortMappingState.Stopped, "and leaves the mapper stopped");
    }

    internal static void UnusableGatewaysLeaveNothingMapped()
    {
        using (var refusing = new LoopbackNatPmpGateway(PublicAddress) { ResultCode = 2 })
        {
            var mapper = new PortMapper(new NatPmpClient(refusing.EndPoint, IPAddress.Loopback), null);
            var called = false;
            mapper.Start(27250, _ => called = true);
            That(Wait(() => mapper.State == PortMappingState.Unavailable) && !called, "a gateway that refuses leaves the mapping unavailable");
        }

        using (var doubled = new LoopbackNatPmpGateway(IPAddress.Parse("10.0.0.5")))
        {
            var mapper = new PortMapper(new NatPmpClient(doubled.EndPoint, IPAddress.Loopback), null);
            mapper.Start(27250, _ => { });
            That(Wait(() => mapper.State == PortMappingState.Unavailable), "a gateway behind another NAT, whose external address is private, maps nothing");
            That(doubled.Requests.All(request => request.Opcode == 0), "and is never even asked to");
        }

        var silent = new PortMapper(new NatPmpClient(new IPEndPoint(IPAddress.Loopback, 9), IPAddress.Loopback), null);
        var watch = Stopwatch.StartNew();
        silent.Start(27250, _ => { });
        That(Wait(() => silent.State == PortMappingState.Unavailable, 10_000) && watch.ElapsedMilliseconds < 6_000, "a silent gateway gives up within four tries");
    }

    internal static void UpnpDiscoveryAndDescriptionsAreValidated()
    {
        var lan = IPAddress.Parse("192.168.1.1");
        var ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: http://192.168.1.1:5000/rootDesc.xml\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        That(UpnpIgdClient.TryParseSearchAnswer(ok, ok.Length, lan, out var location) && location.Port == 5000, "an answer from the gateway naming itself is taken");
        That(!UpnpIgdClient.TryParseSearchAnswer(ok, ok.Length, IPAddress.Parse("198.51.100.7"), out _), "an answer from beyond the LAN is not");
        That(!UpnpIgdClient.TryParseSearchAnswer(ok, ok.Length, IPAddress.Parse("192.168.1.2"), out _), "nor one naming a host other than its sender");
        var secure = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nLOCATION: https://192.168.1.1:5000/rootDesc.xml\r\n\r\n");
        That(!UpnpIgdClient.TryParseSearchAnswer(secure, secure.Length, lan, out _), "nor one that is not plain HTTP");
        That(!UpnpIgdClient.TryParseSearchAnswer(new byte[3000], 3000, lan, out _), "nor an oversized one");

        var root = new Uri("http://192.168.1.1:5000/rootDesc.xml");
        That(UpnpIgdClient.TryParseDescription(Utf8("<root>" + Description("WANIPConnection:1", "/ctl/IPConn") + Description("WANIPConnection:2", "ctl/IPConn2") + "</root>"), root, out var control, out var type) &&
             type.EndsWith("WANIPConnection:2", StringComparison.Ordinal) && control.ToString() == "http://192.168.1.1:5000/ctl/IPConn2", "the newest WAN IP service wins and its control URL resolves");
        That(!UpnpIgdClient.TryParseDescription(Utf8(Description("WANIPConnection:1", "http://203.0.113.5:5000/ctl")), root, out _, out _), "a control URL off the gateway is refused");
        That(!UpnpIgdClient.TryParseDescription(Utf8("<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/hosts\">]><root>&x;" +
                                                    Description("WANIPConnection:1", "/ctl") + "</root>"), root, out _, out _), "a description with a DTD is refused outright");
        var envelope = Encoding.UTF8.GetString(UpnpIgdClient.SoapEnvelope("urn:schemas-upnp-org:service:WANIPConnection:1", "DeletePortMapping",
            new[] { new KeyValuePair<string, string>("NewExternalPort", "27250"), new KeyValuePair<string, string>("NewProtocol", "<&>") }));
        That(envelope.Contains("<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"><NewExternalPort>27250</NewExternalPort><NewProtocol>&lt;&amp;&gt;</NewProtocol>", StringComparison.Ordinal),
            "a SOAP envelope carries the action and escaped arguments");
    }

    internal static void UpnpMapsWithLeasesAndDeletes()
    {
        using var gateway = new LoopbackUpnpGateway(PublicAddress) { PermanentOnly = true };
        gateway.Taken.Add(27250);
        gateway.Description = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><serviceList>" + Description("WANIPConnection:1", "/ctl/IPConn") + "</serviceList></device></root>";
        var mapper = new PortMapper(null, new UpnpIgdClient(IPAddress.Loopback, gateway.SearchTarget, 500));
        IPEndPoint? mapped = null;
        mapper.Start(27250, endpoint => mapped = endpoint);
        That(Wait(() => mapped != null), "the gateway is found and maps a port");
        That(mapped!.Equals(new IPEndPoint(PublicAddress, 27251)) && mapper.Protocol == PortMappingProtocol.Upnp, "the next port, since 27250 was taken");
        var adds = gateway.Actions.Where(action => action.Action == "AddPortMapping").Select(action => action.Arguments).ToList();
        That(adds.First()["NewLeaseDuration"] == "3600" && adds.Any(arguments => arguments["NewLeaseDuration"] == "0"), "an hour's lease first, a permanent one when that is all the gateway grants");
        That(adds.All(arguments => arguments["NewProtocol"] == "UDP" && arguments["NewInternalPort"] == "27250" && arguments["NewInternalClient"] == "127.0.0.1" &&
                                   arguments["NewPortMappingDescription"] == UpnpIgdClient.MappingDescription), "for this machine's UDP game port, described by a fixed name");
        mapper.Stop().Wait(5000);
        That(gateway.Actions.Any(action => action.Action == "DeletePortMapping" && action.Arguments["NewExternalPort"] == "27251"), "stopping deletes the mapping it made");
    }

    internal static void RelayOnlyHostsAndJoinersNeverMap()
    {
        using var gateway = new LoopbackNatPmpGateway(PublicAddress);
        var plan = new NatSessionPlan(IceServerSet.Create(Array.Empty<IceServerAddress>(), new[] { new IceServerAddress("turn.example.test", 3478) }, "user", "secret", 600),
            () => throw new InvalidOperationException("no signaling in this test"), _ => Task.FromResult(Array.Empty<IPAddress>()))
        {
            LocalPort = 27250
        };
        foreach (var relayOnly in new[] { true, false })
        {
            var mapper = new PortMapper(new NatPmpClient(gateway.EndPoint, IPAddress.Loopback), null);
            var nat = NatPlumbing.Wrap(new NatPrivacyTests.RecordingSocket(), new NatAgentOptions { RelayOnly = relayOnly }, () => 0);
            var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(700, 1), new TransportOptions(), 0));
            using var thread = new NetworkThread("TopiaForge test host", nat.Socket, stack, () => 0);
            using var host = NatHost.Start(thread, nat, plan, true, mapper);
            if (relayOnly)
            {
                Thread.Sleep(300);
                That(host.PortMapping == PortMappingState.Off && gateway.Requests.IsEmpty, "a relay-only host never contacts the gateway");
            }
            else
            {
                That(Wait(() => host.PortMapping == PortMappingState.Mapped), "a host that shows its address maps its port");
            }
        }
    }

    private static string Description(string service, string control) =>
        "<service><serviceType>urn:schemas-upnp-org:service:" + service + "</serviceType><serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>" +
        "<controlURL>" + control + "</controlURL><eventSubURL>/evt</eventSubURL><SCPDURL>/scpd.xml</SCPDURL></service>";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

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
