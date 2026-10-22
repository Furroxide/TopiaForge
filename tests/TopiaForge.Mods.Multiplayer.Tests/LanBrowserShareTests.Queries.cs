extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

// The LAN browser's info queries keep the launcher's budget: at most 32 a second overall, and 8 to one sender host in
// any two seconds. A query the budget holds back goes out once it fits, the longest held first and with no further
// beacon, so one host changing port as fast as it can never pushes another host's query out of the endpoint's 64
// outstanding ones before its answer comes.
internal static partial class LanBrowserShareTests
{
    internal static void APortChangeFloodLeavesAnHonestHostItsInfo()
    {
        // The honest host answers over whichever socket the browser's queries use: the game's socket, and the IPv4 one a
        // system without IPv6 gets.
        FloodLeavesAnHonestHostItsInfo(withoutIPv6: false);
        FloodLeavesAnHonestHostItsInfo(withoutIPv6: true);
    }

    internal static void TheLanBrowserAsksWithinTheLaunchersBudget()
    {
        // The browser runs on this thread at virtual times, as in the bounds test, so each window is exact. Its queries
        // are taken off its stack as the network thread would send them.
        using var browser = new LanBrowser(0, expiryMicros: 60_000_000, bindAddress: IPAddress.Loopback);
        using var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(155, 1), new TransportOptions(), 0));
        var senders = new BeaconSender[6];
        try
        {
            for (var index = 0; index < senders.Length; index++) senders[index] = new BeaconSender(index + 1, browser.LocalPort);

            // Four hosts announce eight sessions each at once: 32 queries, each host's eight and the second's 32.
            for (var sender = 0; sender < 4; sender++)
            {
                for (var session = 0; session < 8; session++)
                {
                    Hear(browser, stack, 0, senders[sender], Id((sender * 8) + session), senders[sender].Port);
                }
            }

            That(Asked(stack).Count == 32 && browser.Throttled == 0, "32 sessions heard within one second are all asked about");

            // Two more hosts' sessions within that second wait, though neither host has been asked anything.
            Hear(browser, stack, 0, senders[4], Id(33), senders[4].Port);
            Hear(browser, stack, 500_000, senders[5], Id(32), senders[5].Port);
            That(Asked(stack).Count == 0 && browser.Throttled == 2, "a 33rd and a 34th query within one second wait");
            Turn(browser, stack, 999_999);
            That(Asked(stack).Count == 0, "and still wait just short of the second");

            // Once the second has passed both go out, the longest held first, with no further beacon.
            Turn(browser, stack, 1_000_000);
            That(Asked(stack).SequenceEqual(new[] { At(senders[4], senders[4].Port), At(senders[5], senders[5].Port) }),
                "the waiting queries go out once the budget has room, the longest held first, with no further beacon");

            // A host asked eight times in the last two seconds waits for a ninth, though the second has room: one of its
            // sessions moves to another port.
            Hear(browser, stack, 1_000_000, senders[0], Id(0), LanBeacon.DefaultGamePort);
            That(Asked(stack).Count == 0 && browser.Throttled == 3, "a ninth query to one host within two seconds waits, though the second has room");
            Turn(browser, stack, 1_999_999);
            That(Asked(stack).Count == 0, "and still waits just short of two seconds after that host's first");
            Turn(browser, stack, 2_000_000);
            That(Asked(stack).SequenceEqual(new[] { At(senders[0], LanBeacon.DefaultGamePort) }),
                "it goes out once that host's two seconds have passed, to the port the session moved to");
        }
        finally
        {
            foreach (var sender in senders) sender?.Dispose();
        }
    }

    private static void FloodLeavesAnHonestHostItsInfo(bool withoutIPv6)
    {
        var socket = withoutIPv6 ? " over the IPv4 socket" : " over the game's socket";
        var info = new SessionInfo(Id(0), new string('b', 64), "LAN Party", "2545", "0.2.0", "1.0.0", "Open Sandbox", null, 2, 8, false, true);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var host = StartThread(153, withoutIPv6);
        host.Post((stack, _) => stack.Endpoint.SetInfo(info.Encode()));
        using var browserThread = StartThread(154, withoutIPv6);
        var browser = new LanBrowser(0, expiryMicros: 60_000_000, bindAddress: IPAddress.Loopback);
        browserThread.Attach(browser);
        using var honest = new BeaconSender(1, browser.LocalPort);
        var flood = new BeaconSender[65];
        try
        {
            for (var index = 0; index < flood.Length; index++) flood[index] = new BeaconSender(2, browser.LocalPort);

            // As in #272's review probe, the flooding sender's session is heard and asked about first. The honest host's
            // thread is then held, so that its answer waits until the flood is over.
            Hear(browser, flood[0], Id(1), flood[0].Port);
            host.Post((_, _) =>
            {
                held.Set();
                release.Wait();
            });
            That(held.Wait(3_000), "the honest host is held before it answers" + socket);
            Hear(browser, honest, Id(0), host.LocalPort);

            // 64 port changes from one sender between the honest host's query and its answer. Unbudgeted, each was a query
            // of its own, and the endpoint, which keeps the newest 64, forgot the honest host's before the answer came.
            for (var change = 1; change < flood.Length; change++) Hear(browser, flood[change], Id(1), flood[change].Port);
            release.Set();
            That(WaitFor(() => browser.Servers.Single(server => server.ServerId == Id(0)).Info?.Name == "LAN Party", 3_000),
                "the honest host's info still arrives after another sender changed port 64 times" + socket);
            That(browser.Throttled > 0, "the flooding sender's port changes are held to its share of the queries" + socket);

            // What the budget held back still goes out once it fits, to the session's latest port, with no further beacon.
            That(flood[^1].Receives(4_000), "the flooding session's latest port is asked once its host has room again" + socket);
        }
        finally
        {
            release.Set();
            foreach (var sender in flood) sender?.Dispose();
        }
    }

    /// <summary>Takes the queries the browser queued off its stack, as the network thread sends them, and says where each went.</summary>
    private static List<IPEndPoint> Asked(NetworkStack stack)
    {
        var asked = new List<IPEndPoint>();
        while (stack.TryDequeueDatagram(out var datagram))
        {
            if (UdpPaths.TryGetEndPoint(datagram.Path, out var at)) asked.Add(at);
            stack.Release(datagram);
        }

        return asked;
    }

    private static IPEndPoint At(BeaconSender sender, int port) => new IPEndPoint(sender.Address.MapToIPv6(), port);
}
