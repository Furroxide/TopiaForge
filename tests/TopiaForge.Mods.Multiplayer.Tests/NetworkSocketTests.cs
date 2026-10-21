extern alias provider;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using CallbackFailureMode = provider::TopiaForge.Multiplayer.Engine.EngineCallbackFailureMode;
using EngineOptions = provider::TopiaForge.Multiplayer.Engine.EngineOptions;
using NatAddresses = provider::TopiaForge.Multiplayer.NatTraversal.NatAddresses;
using ProviderLinkState = provider::TopiaForge.Multiplayer.Engine.EngineLinkState;
using RelayServers = provider::TopiaForge.Multiplayer.NatTraversal.Turn.RelayServers;
using SessionEngine = provider::TopiaForge.Multiplayer.Engine.SessionEngine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Real sockets on the loopback interface: the UDP driver over both its sockets, the dual-mode one and the IPv4 one a
/// system without IPv6 gets, and two engines joining across network threads over each.
/// </summary>
internal static class NetworkSocketTests
{
    private const string Owner = "socket-test";
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddCommandType = new("add");

    internal static void UdpPathsKeepAddressesInsideTheNetworkLayer()
    {
        var v4 = UdpPaths.For(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 27250));
        That(v4.Kind == TransportPathKind.Lan && UdpPaths.TryGetEndPoint(v4, out var back) && back.Port == 27250 &&
             back.Address.IsIPv4MappedToIPv6 && back.Address.MapToIPv4().Equals(IPAddress.Parse("192.168.1.20")),
            "an IPv4 endpoint maps to a LAN path and back to its mapped IPv6 form");
        That(UdpPaths.For(new IPEndPoint(IPAddress.Parse("192.168.1.20").MapToIPv6(), 27250)).Equals(v4), "an address and its mapped form are one path");
        foreach (var (address, kind) in new[]
                 {
                     ("10.1.2.3", TransportPathKind.Lan), ("172.16.0.1", TransportPathKind.Lan), ("172.31.255.255", TransportPathKind.Lan),
                     ("172.32.0.1", TransportPathKind.Direct), ("169.254.9.9", TransportPathKind.Lan), ("127.0.0.1", TransportPathKind.Lan),
                     ("100.64.0.1", TransportPathKind.Direct), ("8.8.8.8", TransportPathKind.Direct), ("::1", TransportPathKind.Lan),
                     ("fe80::1", TransportPathKind.Lan), ("fd00::1", TransportPathKind.Lan), ("2001:db8::1", TransportPathKind.Direct),
                 })
        {
            That(UdpPaths.Classify(IPAddress.Parse(address)) == kind, address + " is a " + kind + " address");
        }

        That(v4.ToString() == "Lan path" && !UdpPaths.TryGetEndPoint(new TransportPath(new byte[] { 2, 1, 2, 3 }, TransportPathKind.Relayed), out _),
            "a path prints no address, and a relay path is not a UDP endpoint");

        // A link-local address means nothing without its interface: two scopes are two paths, each restored as heard.
        var seven = UdpPaths.For(new IPEndPoint(IPAddress.Parse("fe80::1%7"), 27250));
        var eight = UdpPaths.For(new IPEndPoint(IPAddress.Parse("fe80::1%8"), 27250));
        That(!seven.Equals(eight) && seven.Kind == TransportPathKind.Lan && UdpPaths.TryGetEndPoint(seven, out var scoped) &&
             scoped.Address.Equals(IPAddress.Parse("fe80::1%7")) && scoped.Address.ScopeId == 7 && scoped.Port == 27250,
            "a scoped address keeps its scope through its path");
        That(UdpPaths.TryGetEndPoint(UdpPaths.For(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1)), out var global) && global.Address.ScopeId == 0,
            "and an unscoped one stays unscoped");
    }

    internal static void TheUdpDriverCarriesDatagramsAndShrugsOffNoise()
    {
        // The game's own socket, dual mode wherever this system has IPv6, then the IPv4 socket a system without IPv6 gets.
        CarryDatagramsAndShrugOffNoise(withoutIPv6: false);
        CarryDatagramsAndShrugOffNoise(withoutIPv6: true);
    }

    /// <summary>
    /// Without IPv6 the driver falls back to one IPv4 socket, and the stack cannot tell: an IPv4 peer keeps the path a
    /// dual-mode socket gives it, IPv4 mapped into IPv6, with the same source, network and family, both ways between the
    /// two kinds of socket. A path the IPv4 socket cannot reach is dropped, and an IPv6-only bind address is refused for
    /// its family, with an error that names no address, rather than replaced by another.
    /// </summary>
    internal static void WithoutIPv6TheDriverFallsBackToIPv4()
    {
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];
        using var system = new UdpSocketDriver(0, IPAddress.Loopback);
        That(system.IPv4Only == !Socket.OSSupportsIPv6,
            "the game's socket is IPv4 only exactly where this system has no IPv6 (" + (system.IPv4Only ? "IPv4 only" : "dual mode") + ")");

        // The IPv4 socket binds the IPv4 form of its address, so the mapped loopback binds the IPv4 loopback.
        using var ipv4 = new UdpSocketDriver(0, IPAddress.Loopback.MapToIPv6(), withoutIPv6: true);
        That(ipv4.IPv4Only, "a system without IPv6 gets the IPv4 socket");
        system.Send(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, ipv4.LocalPort)), new byte[] { 7 }, 0, 1);
        That(Receive(ipv4, buffer, out var count, out var heard) && count == 1 && buffer[0] == 7, "the IPv4 socket hears the game's socket");
        var mapped = UdpPaths.For(new IPEndPoint(IPAddress.Loopback.MapToIPv6(), system.LocalPort));
        That(heard!.Equals(mapped) && heard.Equals(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, system.LocalPort))) &&
             UdpPaths.TryGetEndPoint(heard, out var form) && form.Address.IsIPv4MappedToIPv6 && form.Port == system.LocalPort,
            "and names the sender by the path a dual-mode socket gives it, IPv4 mapped into IPv6");
        That(heard.Kind == TransportPathKind.Lan && UdpPaths.SourceOf(heard) == UdpPaths.SourceOf(mapped) &&
             UdpPaths.NetworkOf(heard)!.SequenceEqual(new byte[] { 4, 127, 0, 0, 1 }) && NatAddresses.IsPlausibleSource(heard) &&
             RelayServers.FamilyOf(heard) == AddressFamily.InterNetwork,
            "so its kind, source, network and family read as they always have, for the stack and the NAT layer alike");
        ipv4.Send(heard, new byte[] { 8 }, 0, 1);
        That(Receive(system, buffer, out count, out var back) && count == 1 && buffer[0] == 8 &&
             back!.Equals(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, ipv4.LocalPort))),
            "a reply on that path reaches the game's socket, which names the IPv4 socket by the same form");

        // A path to an IPv6 address is one the IPv4 socket cannot reach: it is dropped, never sent or thrown.
        var dropped = ipv4.Dropped;
        ipv4.Send(UdpPaths.For(new IPEndPoint(IPAddress.IPv6Loopback, system.LocalPort)), new byte[] { 1 }, 0, 1);
        That(ipv4.Dropped == dropped + 1, "a path to an IPv6 address is dropped, not sent or thrown");

        // An IPv6-only address has no IPv4 form: binding it is refused for its family, never replaced by another address.
        try
        {
            using var refused = new UdpSocketDriver(0, IPAddress.IPv6Loopback, withoutIPv6: true);
            That(false, "binding the IPv6 loopback without IPv6 is refused (it bound " + (refused.IPv4Only ? "IPv4" : "dual mode") + ")");
        }
        catch (SocketException exception)
        {
            That(exception.SocketErrorCode == SocketError.AddressFamilyNotSupported && !exception.Message.Contains("::1", StringComparison.Ordinal),
                "binding the IPv6 loopback without IPv6 is refused for its family, naming no address (" + exception.SocketErrorCode + ")");
        }
    }

    private static void CarryDatagramsAndShrugOffNoise(bool withoutIPv6)
    {
        // Every socket here binds a loopback address: binding every address would make Windows ask to let the test
        // program through its firewall. Each family gets its own pair, as a dual-mode socket bound to one loopback
        // address hears only that family.
        var socket = withoutIPv6 ? " on the IPv4 socket" : " on the game's socket";
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];
        var loopbacks = Socket.OSSupportsIPv6 && !withoutIPv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : new[] { IPAddress.Loopback };
        foreach (var loopback in loopbacks)
        {
            using var sender = new UdpSocketDriver(0, loopback, withoutIPv6);
            using var receiver = new UdpSocketDriver(0, loopback, withoutIPv6);
            That(sender.IPv4Only == (withoutIPv6 || !Socket.OSSupportsIPv6), "the socket is IPv4 only exactly where IPv6 is missing" + socket);
            sender.Send(UdpPaths.For(new IPEndPoint(loopback, receiver.LocalPort)), new byte[] { 1, 2, 3 }, 0, 3);
            That(Receive(receiver, buffer, out var count, out var path) && count == 3 && buffer[0] == 1, "a datagram crosses " + loopback.AddressFamily + socket);
            That(UdpPaths.TryGetEndPoint(path!, out var from) && from.Port == sender.LocalPort && path!.Kind == TransportPathKind.Lan,
                "and arrives on a LAN path back to the sender" + socket);
            receiver.Send(path!, new byte[] { 9 }, 0, 1);
            That(Receive(sender, buffer, out count, out _) && count == 1 && buffer[0] == 9, "a reply on that path reaches the sender" + socket);
        }

        using var first = new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6);
        using var second = new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6);

        // A datagram larger than any transport datagram is read and dropped: the read that consumed it yields no path,
        // so the network thread counts it against its turn. The next one still arrives.
        using (var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            // Bound to loopback first: a socket that sends unbound binds every interface, which raises a firewall prompt.
            raw.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            That(raw.LocalEndPoint is IPEndPoint bound && bound.Address.Equals(IPAddress.Loopback), "the raw sender listens on loopback only");
            raw.SendTo(new byte[1_500], new IPEndPoint(IPAddress.Loopback, second.LocalPort));
            raw.SendTo(new byte[] { 5 }, new IPEndPoint(IPAddress.Loopback, second.LocalPort));
        }

        That(Read(second, buffer, out _, out var droppedPath) && droppedPath == null && second.Dropped == 1,
            "an oversized datagram is consumed by one read that yields no path" + socket);
        That(Receive(second, buffer, out var afterCount, out _) && afterCount == 1 && buffer[0] == 5 && second.Dropped == 1,
            "and the next one arrives" + socket);

        // A datagram to a closed port draws an ICMP unreachable; it must not break the next receive.
        int closedPort;
        using (var probe = new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6)) closedPort = probe.LocalPort;
        first.Send(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, closedPort)), new byte[] { 1 }, 0, 1);
        Thread.Sleep(50);
        second.Send(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, first.LocalPort)), new byte[] { 6 }, 0, 1);
        That(Receive(first, buffer, out var count2, out _) && count2 == 1 && buffer[0] == 6, "an unreachable port never breaks receiving" + socket);
        first.Send(new TransportPath(new byte[] { 2, 9 }, TransportPathKind.Relayed), new byte[] { 1 }, 0, 1);
        That(first.Dropped >= 1, "a path the socket cannot reach is dropped, not thrown" + socket);
    }

    internal static void EnginesJoinOverRealSocketsQuickly()
    {
        // The transport finds every connection by its path, so the join runs over the IPv4 socket as well.
        JoinOverRealSockets(withoutIPv6: false);
        JoinOverRealSockets(withoutIPv6: true);
    }

    private static void JoinOverRealSockets(bool withoutIPv6)
    {
        var socket = withoutIPv6 ? " on the IPv4 socket" : " on the game's socket";
        var watch = Stopwatch.StartNew();
        var secret = new byte[32];
        new Random(120).NextBytes(secret);
        using var identity = new LocalIdentity(TransportPair.Key(120, 1), TransportPair.Key(120, 3), secret, 0);
        var hostStack = new NetworkStack(
            new TransportEndpoint(identity.CopyTransportPrivateKey(), new TransportOptions(), Clock()),
            key => new MultiplayerPlayerKey(identity.PlayerKeyFor(key)));
        var clientStack = new NetworkStack(new TransportEndpoint(TransportPair.Key(120, 2), new TransportOptions(), Clock()));
        using var hostThread = new NetworkThread("TopiaForge test host", new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6), hostStack, Clock);
        using var clientThread = new NetworkThread("TopiaForge test client", new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6), clientStack, Clock);
        hostThread.Start();
        clientThread.Start();

        var options = new EngineOptions
        {
            StepRateHz = 20,
            CallbackFailures = CallbackFailureMode.Propagate,
            ParticipantIdForIdentity = peer => new ParticipantId("player-" + peer.Substring(0, 8))
        };
        var sessionId = new MultiplayerSessionId("socket-session");
        var host = SessionEngine.CreateHost(options, MultiplayerProcessKind.Interactive, sessionId, new SessionSeed(3), new ParticipantId("host"), "Host", Clock());
        var hostScore = Register(host.CreateFacade(Owner));
        var client = SessionEngine.CreateClient(options, sessionId, Clock());
        var clientSession = client.CreateFacade(Owner);
        var clientScore = Register(clientSession);

        var attempt = clientThread.Connect(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, hostThread.LocalPort)), identity.TransportPublicKey);
        NetworkLink? accepted = null;
        var joining = false;
        That(Pump(host, client, () =>
        {
            while (hostThread.TryTakeEvent(out var networkEvent))
            {
                if (networkEvent.Kind == NetworkEventKind.LinkAccepted) host.Accept(accepted = networkEvent.Link!);
            }

            if (!joining && attempt.State == ConnectAttemptState.Established && accepted != null)
            {
                joining = true;
                client.Connect(attempt.Link!, "Client");
            }

            return client.Snapshot.State == MultiplayerSessionState.Ready;
        }, 3_000), "the client joins over real sockets" + socket);
        That(accepted!.PlayerKey.Value == identity.PlayerKeyFor(clientStack.Endpoint.StaticPublicKey), "the host's link carries the player key it derived");

        var submission = clientSession.SubmitAsync(AddCommandType, new AddRequest(4));
        That(Pump(host, client, () => submission.IsCompleted && clientScore.Value.Value == 4, 2_000), "a command round-trips");
        That(hostScore.Value.Value == 4, "and ran once on the host");

        client.Shutdown(MultiplayerSessionEndReason.Left);
        That(Pump(host, client, () => accepted.State == ProviderLinkState.Closed, 2_000), "leaving closes the host's link");
        host.Shutdown(MultiplayerSessionEndReason.HostEnded);
        clientThread.Stop(100_000);
        hostThread.Stop(100_000);
        That(hostThread.HasStopped && clientThread.HasStopped, "both threads stop");
        That(watch.ElapsedMilliseconds < 5_000, "the whole exchange takes under five seconds" + socket + " (" + watch.ElapsedMilliseconds + " ms)");
    }

    private static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();

    internal static void StoppingHonoursItsGracePeriod()
    {
        var hostStack = new NetworkStack(new TransportEndpoint(TransportPair.Key(121, 1), new TransportOptions(), Clock()));
        var clientStack = new NetworkStack(new TransportEndpoint(TransportPair.Key(121, 2), new TransportOptions(), Clock()));
        var hostThread = new NetworkThread("TopiaForge test host", new UdpSocketDriver(0, IPAddress.Loopback), hostStack, Clock);
        var clientThread = new NetworkThread("TopiaForge test client", new UdpSocketDriver(0, IPAddress.Loopback), clientStack, Clock);
        hostThread.Start();
        clientThread.Start();
        var attempt = clientThread.Connect(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, hostThread.LocalPort)), null);
        NetworkLink? accepted = null;
        var deadline = Environment.TickCount64 + 3_000;
        while ((accepted == null || attempt.State != ConnectAttemptState.Established) && Environment.TickCount64 < deadline)
        {
            while (hostThread.TryTakeEvent(out var networkEvent))
            {
                if (networkEvent.Kind == NetworkEventKind.LinkAccepted) accepted = networkEvent.Link;
            }

            Thread.Sleep(1);
        }

        That(accepted != null && attempt.State == ConnectAttemptState.Established, "the two threads connect");

        // At 256 kbit/s a 512 KiB frame takes many seconds to deliver, so a graceful close cannot finish within the grace.
        accepted!.Send(provider::TopiaForge.Multiplayer.Engine.EngineChannel.Session, new ArraySegment<byte>(new byte[512 * 1024]));
        Thread.Sleep(20);
        var watch = Stopwatch.StartNew();
        hostThread.Stop(300_000);
        That(hostThread.HasStopped && watch.ElapsedMilliseconds >= 250 && watch.ElapsedMilliseconds < 1_500,
            "a stop waits for its grace period and no longer (" + watch.ElapsedMilliseconds + " ms)");
        That(accepted.State == ProviderLinkState.Closed, "and leaves every link closed");
        var events = new List<NetworkEventKind>();
        while (hostThread.TryTakeEvent(out var networkEvent)) events.Add(networkEvent.Kind);
        That(events.Contains(NetworkEventKind.Stopped) && !events.Contains(NetworkEventKind.Fault), "the thread reports that it stopped, without faults");
        clientThread.Stop(0);
        That(clientThread.HasStopped, "an immediate stop returns once the loop has ended");

        var unused = new NetworkThread("TopiaForge test unused", new UdpSocketDriver(0, IPAddress.Loopback), new NetworkStack(new TransportEndpoint(TransportPair.Key(121, 3), new TransportOptions(), Clock())), Clock);
        unused.Stop(0);
        Throws<InvalidOperationException>(() => unused.Start(), "a thread stopped before it started cannot start");
        unused.Stop(0);
        unused.Dispose();
        That(unused.HasStopped, "and stopping or disposing it again has nothing to wait for");
    }

    /// <summary>Waits for a datagram that arrived intact, passing over any the driver dropped.</summary>
    private static bool Receive(UdpSocketDriver driver, byte[] buffer, out int count, out TransportPath? path)
    {
        var deadline = Environment.TickCount64 + 2_000;
        while (Read(driver, buffer, out count, out path, deadline))
        {
            if (path != null) return true;
        }

        return false;
    }

    /// <summary>Waits for the driver to consume something, kept or dropped.</summary>
    private static bool Read(UdpSocketDriver driver, byte[] buffer, out int count, out TransportPath? path, long deadline = 0)
    {
        if (deadline == 0) deadline = Environment.TickCount64 + 2_000;
        while (Environment.TickCount64 < deadline)
        {
            if (driver.TryReceive(buffer, out count, out path)) return true;
            driver.WaitReadable(10_000);
        }

        count = 0;
        path = null;
        return false;
    }

    /// <summary>Pumps both engines on this thread, as the game's main thread would, until the condition holds.</summary>
    private static bool Pump(SessionEngine host, SessionEngine client, Func<bool> condition, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!host.IsEnded) host.Pump(Clock());
            if (!client.IsEnded) client.Pump(Clock());
            if (condition()) return true;
            Thread.Sleep(1);
        }

        return false;
    }

    private static IReplicatedState<CounterValue> Register(IMultiplayerSession session)
    {
        if (!session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)).TryGetValue(out var score))
        {
            throw new InvalidOperationException("Assertion failed: the score registers");
        }

        if (!session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                AddCommandType,
                RequestCodec,
                CounterCodec,
                (_, request) => score.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount))))).Succeeded)
        {
            throw new InvalidOperationException("Assertion failed: the add command registers");
        }

        return score;
    }
}
