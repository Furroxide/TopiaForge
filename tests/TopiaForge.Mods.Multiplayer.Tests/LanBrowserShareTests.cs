extern alias provider;

using System.Net;
using System.Net.Sockets;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The LAN browser's bounds, the launcher's rule: at most 64 sessions, at most 8 of them from one sender address on
/// any port, and only silence makes room. Beacons come from the test's own sockets on loopback addresses, since the
/// browser takes the sender from the datagram and the port from the beacon. The budget its info queries keep is in
/// LanBrowserShareTests.Queries.cs.
/// </summary>
internal static partial class LanBrowserShareTests
{
    internal static void ALanSenderHoldsAtMostItsShareOfTheList()
    {
        // The share holds whichever socket the browser's queries and the hosts' answers cross: the game's socket, and
        // the IPv4 one a system without IPv6 gets.
        HoldsAtMostItsShare(withoutIPv6: false);
        HoldsAtMostItsShare(withoutIPv6: true);
    }

    internal static void TheLanListIsBoundedAndOnlySilenceMakesRoom()
    {
        // The browser runs on this thread at virtual times, so which session falls silent first is exact. Its queries
        // are never sent: no stack polls them.
        using var browser = new LanBrowser(0, expiryMicros: 6_000_000, bindAddress: IPAddress.Loopback);
        using var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(152, 1), new TransportOptions(), 0));
        var senders = new BeaconSender[9];
        try
        {
            for (var index = 0; index < senders.Length; index++) senders[index] = new BeaconSender(index + 1, browser.LocalPort);

            // Eight senders fill the list with eight sessions each, the first at 0 s and the rest a second later.
            for (var sender = 0; sender < 8; sender++)
            {
                for (var session = 0; session < 8; session++)
                {
                    Hear(browser, stack, sender == 0 ? 0 : 1_000_000, senders[sender], Id((sender * 8) + session), senders[sender].Port);
                }
            }

            var full = Listed(browser);
            That(full.SequenceEqual(Ids(0, 64)) && browser.Dropped == 0, "eight senders list eight sessions each, 64 in all");

            // A full list takes no newcomer, even from a sender that holds nothing, and pushes nobody out for one.
            Drop(browser, stack, 1_000_000, senders[8], Id(64), senders[8].Port, 1);
            That(Listed(browser).SequenceEqual(full), "a 65th session is dropped and counted, from a sender holding nothing, and nobody listed leaves");

            // A full list still follows a session to another port on its own sender, and to another address with room,
            // since neither adds a session. Onto a sender at its share, a move is dropped.
            Hear(browser, stack, 1_000_000, senders[1], Id(8), LanBeacon.DefaultGamePort);
            Hear(browser, stack, 1_000_000, senders[8], Id(9), senders[8].Port);
            That(Listed(browser).SequenceEqual(full) && browser.Dropped == 1,
                "in a full list, a session moves to another port on its own sender and to a sender with room, and keeps its place");
            Drop(browser, stack, 1_000_000, senders[2], Id(0), senders[2].Port, 2);
            That(IsAt(browser, Id(0), senders[0], senders[0].Port), "a move onto a sender at its share is dropped, and the session stays where it was heard");

            // Silence alone makes room, the longest silent first. Half the first sender's sessions are heard again at 5 s;
            // at 6 s the other half, silent since 0 s, leave, and the sessions heard at 1 s stay.
            for (var session = 0; session < 4; session++) Hear(browser, stack, 5_000_000, senders[0], Id(session), senders[0].Port);
            Turn(browser, stack, 6_000_000);
            That(Listed(browser).SequenceEqual(Ids(0, 4).Concat(Ids(8, 56))), "at 6 s only the sessions silent since 0 s have left");

            // The places they leave go to whoever comes next, a sender that held none of them included.
            Hear(browser, stack, 6_000_000, senders[8], Id(64), senders[8].Port);
            That(Listed(browser).Count == 61 && browser.Dropped == 2, "a newcomer takes a place silence made");

            // At 7 s the sessions last heard at 1 s leave too, the moved ones included.
            Turn(browser, stack, 7_000_000);
            That(Listed(browser).SequenceEqual(Ids(0, 4).Append(Id(64))), "at 7 s every session last heard at 1 s has left");
        }
        finally
        {
            foreach (var sender in senders) sender?.Dispose();
        }
    }

    private static void HoldsAtMostItsShare(bool withoutIPv6)
    {
        var socket = withoutIPv6 ? " over the IPv4 socket" : " over the game's socket";
        var info = new SessionInfo(Id(0), new string('b', 64), "LAN Party", "2545", "0.2.0", "1.0.0", "Open Sandbox", null, 2, 8, false, true);
        using var host = StartThread(150, withoutIPv6);
        host.Post((stack, _) => stack.Endpoint.SetInfo(info.Encode()));
        using var browserThread = StartThread(151, withoutIPv6);
        var browser = new LanBrowser(0, expiryMicros: 60_000_000, bindAddress: IPAddress.Loopback);
        browserThread.Attach(browser);
        using var crowded = new BeaconSender(1, browser.LocalPort);
        using var crowdedElsewhere = new BeaconSender(1, browser.LocalPort);
        using var neighbour = new BeaconSender(2, browser.LocalPort);

        // One sender announces eight sessions; the first is a real host, which answers for its server over this socket.
        Hear(browser, crowded, Id(0), host.LocalPort);
        for (var session = 1; session < 8; session++) Hear(browser, crowded, Id(session), crowded.Port);
        That(WaitFor(() => browser.Servers.Single(server => server.ServerId == Id(0)).Info?.Name == "LAN Party", 3_000),
            "the browser lists eight sessions from one sender and learns the real host's info" + socket);

        // A ninth from that sender is dropped and counted, and the first eight keep their places.
        Drop(browser, crowded, Id(8), crowded.Port, 1);
        That(Listed(browser).SequenceEqual(Ids(0, 8)), "a ninth session from one sender is dropped, and the first eight stay" + socket);

        // Another sender keeps its own share.
        Hear(browser, neighbour, Id(9), neighbour.Port);

        // A session that moves to another port on its own sender keeps its place.
        Hear(browser, crowdedElsewhere, Id(1), crowdedElsewhere.Port);
        That(browser.Servers.Count == 9 && browser.Dropped == 1, "a session moving to another port on its own sender keeps its place" + socket);

        // A session moving onto a sender at its share is dropped, and stays where it was heard.
        Drop(browser, crowded, Id(9), crowded.Port, 2);
        That(IsAt(browser, Id(9), neighbour, neighbour.Port), "a move onto a sender at its share is dropped" + socket);

        // A session that moves to a sender with room is heard there, and frees a place at its old sender.
        Hear(browser, neighbour, Id(7), neighbour.Port);
        Hear(browser, crowded, Id(8), crowded.Port);
        That(Listed(browser).SequenceEqual(Ids(0, 10)) && browser.Dropped == 2,
            "a session moving to a sender with room frees a place at its old one" + socket);
        That(browser.Servers.Single(server => server.ServerId == Id(0)).Info?.Name == "LAN Party",
            "and the real host's session keeps its info throughout" + socket);
    }

    /// <summary>Sends a beacon and waits for the browser on its thread to list the session at that sender and port.</summary>
    private static void Hear(LanBrowser browser, BeaconSender sender, string serverId, int gamePort)
    {
        sender.Announce(serverId, gamePort);
        That(WaitFor(() => IsAt(browser, serverId, sender, gamePort), 3_000), "the browser lists a session it was told of");
    }

    /// <summary>Sends a beacon and waits for the browser on its thread to drop it.</summary>
    private static void Drop(LanBrowser browser, BeaconSender sender, string serverId, int gamePort, long dropped)
    {
        sender.Announce(serverId, gamePort);
        That(WaitFor(() => browser.Dropped == dropped, 3_000), "the browser drops a beacon past its bounds and counts it (" + browser.Dropped + ")");
    }

    /// <summary>Sends a beacon and turns the browser at <paramref name="now"/> until it lists the session as heard then.</summary>
    private static void Hear(LanBrowser browser, NetworkStack stack, long now, BeaconSender sender, string serverId, int gamePort)
    {
        sender.Announce(serverId, gamePort);
        That(TurnUntil(browser, stack, now, () =>
                browser.Servers.Any(server => server.ServerId == serverId && server.LastSeenMicros == now) && IsAt(browser, serverId, sender, gamePort)),
            "the browser lists a session it was told of");
    }

    /// <summary>Sends a beacon and turns the browser at <paramref name="now"/> until it drops it.</summary>
    private static void Drop(LanBrowser browser, NetworkStack stack, long now, BeaconSender sender, string serverId, int gamePort, long dropped)
    {
        sender.Announce(serverId, gamePort);
        That(TurnUntil(browser, stack, now, () => browser.Dropped == dropped),
            "the browser drops a beacon past its bounds and counts it (" + browser.Dropped + ")");
    }

    private static void Turn(LanBrowser browser, NetworkStack stack, long now) => browser.Turn(stack, now);

    /// <summary>Turns the browser at one virtual time until <paramref name="condition"/> holds, for at most 3 s of real time.</summary>
    private static bool TurnUntil(LanBrowser browser, NetworkStack stack, long now, Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 3_000;
        while (true)
        {
            browser.Turn(stack, now);
            if (condition()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(1);
        }
    }

    private static bool IsAt(LanBrowser browser, string serverId, BeaconSender sender, int gamePort) =>
        browser.Servers.SingleOrDefault(server => server.ServerId == serverId) is { } listed &&
        UdpPaths.TryGetEndPoint(listed.Path, out var at) && at.Port == gamePort && at.Address.Equals(sender.Address.MapToIPv6());

    private static List<string> Listed(LanBrowser browser) => browser.Servers.Select(server => server.ServerId).ToList();

    /// <summary>A server id that sorts as its number does, as the browser lists them.</summary>
    private static string Id(int number) => number.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<string> Ids(int first, int count) => Enumerable.Range(first, count).Select(Id);

    private static NetworkThread StartThread(int seed, bool withoutIPv6)
    {
        var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(seed, 1), new TransportOptions(), Clock()));
        var thread = new NetworkThread("TopiaForge test " + seed, new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6), stack, Clock);
        thread.Start();
        return thread;
    }

    private static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();

    private static bool WaitFor(Func<bool> condition, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }

        return condition();
    }

    /// <summary>
    /// A socket on the loopback address 127.0.0.<c>n</c> that sends beacons to the browser, so each test sender has an
    /// address of its own. Its own port is the game port its beacons usually name, so the browser's queries come back
    /// to a socket the test holds rather than to a port someone else may hold.
    /// </summary>
    private sealed class BeaconSender : IDisposable
    {
        private readonly Socket socket;
        private readonly IPEndPoint browser;

        internal BeaconSender(int lastOctet, int browserPort)
        {
            Address = new IPAddress(new byte[] { 127, 0, 0, (byte)lastOctet });
            browser = new IPEndPoint(IPAddress.Loopback, browserPort);
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(Address, 0));
                Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        internal IPAddress Address { get; }

        internal int Port { get; }

        internal void Announce(string serverId, int gamePort)
        {
            var beacon = new LanBeacon(serverId, gamePort).Encode();
            socket.SendTo(beacon, browser);
        }

        /// <summary>Waits for a datagram to this sender's own port, as the browser's info query to a session there arrives.</summary>
        internal bool Receives(int milliseconds) => socket.Poll(milliseconds * 1_000, SelectMode.SelectRead);

        public void Dispose() => socket.Dispose();
    }
}
