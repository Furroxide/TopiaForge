extern alias provider;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using CallbackFailureMode = provider::TopiaForge.Multiplayer.Engine.EngineCallbackFailureMode;
using EngineOptions = provider::TopiaForge.Multiplayer.Engine.EngineOptions;
using ProviderLinkState = provider::TopiaForge.Multiplayer.Engine.EngineLinkState;
using SessionEngine = provider::TopiaForge.Multiplayer.Engine.SessionEngine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Real sockets on the loopback interface: the UDP driver, and two engines joining across network threads.</summary>
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
        using var first = new UdpSocketDriver(0);
        using var second = new UdpSocketDriver(0);
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];
        var loopbacks = Socket.OSSupportsIPv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : new[] { IPAddress.Loopback };
        foreach (var loopback in loopbacks)
        {
            first.Send(UdpPaths.For(new IPEndPoint(loopback, second.LocalPort)), new byte[] { 1, 2, 3 }, 0, 3);
            That(Receive(second, buffer, out var count, out var path) && count == 3 && buffer[0] == 1, "a datagram crosses " + loopback.AddressFamily);
            That(UdpPaths.TryGetEndPoint(path!, out var from) && from.Port == first.LocalPort && path!.Kind == TransportPathKind.Lan,
                "and arrives on a LAN path back to the sender");
            second.Send(path!, new byte[] { 9 }, 0, 1);
            That(Receive(first, buffer, out count, out _) && count == 1 && buffer[0] == 9, "a reply on that path reaches the sender");
        }

        // A datagram larger than any transport datagram is read and dropped: the read that consumed it yields no path,
        // so the network thread counts it against its turn. The next one still arrives.
        using (var raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            raw.SendTo(new byte[1_500], new IPEndPoint(IPAddress.Loopback, second.LocalPort));
            raw.SendTo(new byte[] { 5 }, new IPEndPoint(IPAddress.Loopback, second.LocalPort));
        }

        That(Read(second, buffer, out _, out var droppedPath) && droppedPath == null && second.Dropped == 1,
            "an oversized datagram is consumed by one read that yields no path");
        That(Receive(second, buffer, out var afterCount, out _) && afterCount == 1 && buffer[0] == 5 && second.Dropped == 1,
            "and the next one arrives");

        // A datagram to a closed port draws an ICMP unreachable; it must not break the next receive.
        int closedPort;
        using (var probe = new UdpSocketDriver(0)) closedPort = probe.LocalPort;
        first.Send(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, closedPort)), new byte[] { 1 }, 0, 1);
        Thread.Sleep(50);
        second.Send(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, first.LocalPort)), new byte[] { 6 }, 0, 1);
        That(Receive(first, buffer, out var count2, out _) && count2 == 1 && buffer[0] == 6, "an unreachable port never breaks receiving");
        first.Send(new TransportPath(new byte[] { 2, 9 }, TransportPathKind.Relayed), new byte[] { 1 }, 0, 1);
        That(first.Dropped >= 1, "a path the socket cannot reach is dropped, not thrown");
    }

    internal static void EnginesJoinOverRealSocketsQuickly()
    {
        var watch = Stopwatch.StartNew();
        var secret = new byte[32];
        new Random(120).NextBytes(secret);
        using var identity = new LocalIdentity(TransportPair.Key(120, 1), TransportPair.Key(120, 3), secret, 0);
        var hostStack = new NetworkStack(
            new TransportEndpoint(identity.CopyTransportPrivateKey(), new TransportOptions(), Clock()),
            key => new MultiplayerPlayerKey(identity.PlayerKeyFor(key)));
        var clientStack = new NetworkStack(new TransportEndpoint(TransportPair.Key(120, 2), new TransportOptions(), Clock()));
        using var hostThread = new NetworkThread("TopiaForge test host", new UdpSocketDriver(0), hostStack, Clock);
        using var clientThread = new NetworkThread("TopiaForge test client", new UdpSocketDriver(0), clientStack, Clock);
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
        }, 3_000), "the client joins over real sockets");
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
        That(watch.ElapsedMilliseconds < 5_000, "the whole exchange takes under five seconds (" + watch.ElapsedMilliseconds + " ms)");
    }

    private static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();

    internal static void StoppingHonoursItsGracePeriod()
    {
        var hostStack = new NetworkStack(new TransportEndpoint(TransportPair.Key(121, 1), new TransportOptions(), Clock()));
        var clientStack = new NetworkStack(new TransportEndpoint(TransportPair.Key(121, 2), new TransportOptions(), Clock()));
        var hostThread = new NetworkThread("TopiaForge test host", new UdpSocketDriver(0), hostStack, Clock);
        var clientThread = new NetworkThread("TopiaForge test client", new UdpSocketDriver(0), clientStack, Clock);
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

        var unused = new NetworkThread("TopiaForge test unused", new UdpSocketDriver(0), new NetworkStack(new TransportEndpoint(TransportPair.Key(121, 3), new TransportOptions(), Clock())), Clock);
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
