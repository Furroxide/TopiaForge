extern alias provider;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>The services the network thread runs beside its stack, over real loopback sockets: descriptors and LAN discovery.</summary>
internal static class NetworkServiceTests
{
    internal static void TheDescriptorEndpointServesOnlyWhatItShould()
    {
        var fixture = NetworkFixtures.Load("descriptor/cases.json");
        var signed = SignedServerDescriptor.Sign(
            NetworkDescriptorTests.Build(fixture.GetProperty("valid")[0].GetProperty("descriptor")),
            NetworkFixtures.Hex(fixture.GetProperty("keys")[0].Text("signingSeed")));
        var package = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("package bytes ", 30_000)));
        var packageSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package)).ToLowerInvariant();
        var brokenSha = new string('b', 64);
        var lockedSha = new string('c', 64);
        using var host = StartThread(122);
        var endpoint = new DescriptorEndpoint(0, sha => sha switch
        {
            _ when sha == packageSha => new MemoryStream(package, false),
            _ when sha == brokenSha => new BrokenArchive(),
            _ when sha == lockedSha => throw new IOException("The archive is locked."),
            _ => null,
        }, requestTimeoutMicros: 300_000);
        host.Attach(endpoint);
        var port = endpoint.LocalPort;

        var none = Fetch(IPAddress.Loopback, port, null);
        That(none.Outcome == DescriptorFetchOutcome.Refused && none.Descriptor == null, "a host with no descriptor refuses");
        endpoint.SetDescriptor(signed);
        foreach (var loopback in Socket.OSSupportsIPv6 ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback } : new[] { IPAddress.Loopback })
        {
            var fetched = Fetch(loopback, port, signed.Descriptor.ServerId);
            That(fetched.Outcome == DescriptorFetchOutcome.Fetched && fetched.Descriptor!.Sha256 == signed.Sha256,
                "the signed descriptor arrives and verifies over " + loopback.AddressFamily + ": " + fetched.Detail);
        }

        var other = Fetch(IPAddress.Loopback, port, new string('0', 64));
        That(other.Outcome == DescriptorFetchOutcome.Invalid && other.Descriptor == null, "a descriptor for another server is refused");
        Thread.Sleep(1_100);

        That(Raw(port, "TFD1 package " + packageSha + "\n", out var body) == "TFD1 200 " + package.Length && body.SequenceEqual(package),
            "a known package streams whole (" + body.Length + " bytes)");
        That(Raw(port, "TFD1 package " + new string('a', 64) + "\n", out _) == "TFD1 404", "an unknown package is not found");
        That(Raw(port, "HELLO\n", out _) == "TFD1 400", "a malformed request is refused");
        That(Raw(port, "TFD1 descriptor\u0001\n", out _) == "TFD1 400", "a request with a control character is refused");
        That(Raw(port, new string('x', 200), out _) == "TFD1 400", "a request past 128 bytes is refused");

        // An archive that fails belongs to its own request: the transfer is cut short, or the archive is not found,
        // and the network thread never faults.
        That(Raw(port, "TFD1 package " + brokenSha + "\n", out var cut) == "TFD1 200 " + BrokenArchive.Size && cut.Length < BrokenArchive.Size,
            "an archive that fails mid-read cuts its transfer short (" + cut.Length + " bytes)");
        That(Raw(port, "TFD1 package " + lockedSha + "\n", out _) == "TFD1 404", "an archive that cannot be opened is not found");
        var faults = new List<string>();
        while (host.TryTakeEvent(out var happened))
        {
            if (happened.Kind == NetworkEventKind.Fault) faults.Add(happened.Fault!);
        }

        That(faults.Count == 0, "and neither faults the network thread (" + string.Join(", ", faults) + ")");
        Thread.Sleep(1_100);

        // Ten requests a second per address; the rest are told to wait.
        var statuses = Enumerable.Range(0, 12).Select(attempt => Raw(port, "TFD1 descriptor\n", out _)).ToList();
        That(statuses.Count(status => status.StartsWith("TFD1 200", StringComparison.Ordinal)) == DescriptorEndpoint.RequestsPerSecond &&
             statuses.Count(status => status == "TFD1 429") == 2, "an address gets ten requests a second (" + string.Join(", ", statuses) + ")");

        // A client that never asks is dropped after the request timeout; a ninth connection finds the endpoint busy.
        var idle = Enumerable.Range(0, DescriptorEndpoint.MaximumConnections).Select(_ => Connect(port)).ToList();
        Thread.Sleep(50);
        That(Raw(port, "TFD1 descriptor\n", out _) == "TFD1 503", "a ninth connection is turned away");
        var watch = Stopwatch.StartNew();
        That(idle.All(socket => ReadsEndOfStream(socket, 3_000)), "idle connections are closed after the request timeout");
        That(watch.ElapsedMilliseconds < 2_000, "promptly (" + watch.ElapsedMilliseconds + " ms)");
        idle.ForEach(socket => socket.Dispose());

        using var plain = StartThread(123);
        var noPackages = new DescriptorEndpoint(0);
        plain.Attach(noPackages);
        noPackages.SetDescriptor(signed);
        That(Raw(noPackages.LocalPort, "TFD1 package " + packageSha + "\n", out _) == "TFD1 404", "outside developer mode no package is served");
    }

    internal static void LanBrowsersFindAnnouncedHosts()
    {
        var info = new SessionInfo(new string('a', 64), new string('b', 64), "LAN Party", "2545", "0.2.0", "1.0.0", "Open Sandbox", null, 2, 8, false, true);
        using var host = StartThread(124);
        host.Post((stack, _) => stack.Endpoint.SetInfo(info.Encode()));
        using var browserThread = StartThread(125);
        var browser = new LanBrowser(0, expiryMicros: 1_500_000);
        browserThread.Attach(browser);
        var targets = new[] { new IPEndPoint(IPAddress.Loopback, browser.LocalPort) };
        var announcer = new LanAnnouncer(new LanBeacon(info.ServerId, host.LocalPort), targets);
        host.Attach(announcer);

        That(WaitFor(() => browser.Servers.Any(server => server.Info != null), 3_000), "the browser hears the host and learns its info");
        var found = browser.Servers.Single();
        That(found.ServerId == info.ServerId && found.Info!.Name == "LAN Party" && found.Info.Players == 2 && found.RoundTripMicros > 0 &&
             found.Path.Kind == TransportPathKind.Lan && UdpPaths.TryGetEndPoint(found.Path, out var to) && to.Port == host.LocalPort,
            "with the host's info, round trip, and a LAN path to its game port");

        // A beacon naming another server at the same game socket never borrows that server's info.
        var impostor = new LanAnnouncer(new LanBeacon(new string('c', 64), host.LocalPort), targets);
        host.Attach(impostor);
        That(WaitFor(() => browser.Servers.Any(server => server.ServerId == new string('c', 64)), 3_000), "the second beacon is heard");
        Thread.Sleep(300);
        That(browser.Servers.Single(server => server.ServerId == new string('c', 64)).Info == null &&
             browser.Servers.Single(server => server.ServerId == info.ServerId).Info != null,
            "but its entry stays without info, since the info names another server");

        host.Detach(announcer);
        host.Detach(impostor);
        That(WaitFor(() => browser.Servers.Count == 0, 4_000), "silent hosts leave the list");
    }

    internal static void AMovedServerIsHeardAfresh()
    {
        var info = new SessionInfo(new string('a', 64), new string('b', 64), "LAN Party", "2545", "0.2.0", "1.0.0", "Open Sandbox", null, 2, 8, false, true);
        var stranger = new SessionInfo(new string('d', 64), new string('b', 64), "Elsewhere", "2545", "0.2.0", "1.0.0", "Open Sandbox", null, 1, 8, false, true);
        using var first = StartThread(128);
        first.Post((stack, _) => stack.Endpoint.SetInfo(info.Encode()));
        using var second = StartThread(129);
        second.Post((stack, _) => stack.Endpoint.SetInfo(stranger.Encode()));
        using var browserThread = StartThread(130);
        var browser = new LanBrowser(0, expiryMicros: 30_000_000);
        browserThread.Attach(browser);
        var targets = new[] { new IPEndPoint(IPAddress.Loopback, browser.LocalPort) };
        var before = new LanAnnouncer(new LanBeacon(info.ServerId, first.LocalPort), targets);
        first.Attach(before);
        That(WaitFor(() => browser.Servers.Any(server => server.Info != null), 3_000), "the browser learns the server's info at its first socket");

        // The server's beacon now names another game socket, whose host answers for a different server: what the old
        // socket said must not follow the beacon there.
        first.Detach(before);
        var after = new LanAnnouncer(new LanBeacon(info.ServerId, second.LocalPort), targets);
        second.Attach(after);
        That(WaitFor(() => browser.Servers.SingleOrDefault() is { } server && UdpPaths.TryGetEndPoint(server.Path, out var at) && at.Port == second.LocalPort, 3_000),
            "the browser follows the beacon to the new socket");
        Thread.Sleep(300);
        var moved = browser.Servers.Single();
        That(moved.Info == null && moved.RoundTripMicros == 0 && !moved.Incompatible,
            "and waits for that socket to answer for the same server, which it never does");
        second.Detach(after);
    }

    internal static void ServicesStopWithTheirThread()
    {
        var thread = StartThread(126);
        var endpoint = new DescriptorEndpoint(0);
        thread.Attach(endpoint);
        Thread.Sleep(20);
        var port = endpoint.LocalPort;
        thread.Stop(0);
        That(!CanConnect(port), "stopping the thread closes its services' sockets");

        var unstarted = new NetworkThread("TopiaForge test unstarted", new UdpSocketDriver(0),
            new NetworkStack(new TransportEndpoint(TransportPair.Key(127, 1), new TransportOptions(), Clock())), Clock);
        var orphan = new DescriptorEndpoint(0);
        unstarted.Attach(orphan);
        var orphanPort = orphan.LocalPort;
        var queued = new RecordingService();
        unstarted.Attach(queued);
        var unsent = unstarted.Connect(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, 9)), null);
        unstarted.Stop(0);
        That(!CanConnect(orphanPort), "a service attached to a thread that never ran is disposed with it");
        That(queued.Disposed && queued.Turns == 0 && unsent.State == ConnectAttemptState.Failed,
            "without running, and a connection it never started fails");

        // A service still queued behind more commands than one turn runs when the deadline passes is the thread's to
        // dispose, though it never ran; once the thread is stopping, nothing new is taken on.
        var busy = StartThread(131);
        using var release = new ManualResetEventSlim();
        busy.Post((_, _) => release.Wait());
        for (var index = 0; index < 300; index++) busy.Post((_, _) => { });
        var waiting = new RecordingService();
        busy.Attach(waiting);
        var releaser = Task.Run(() =>
        {
            Thread.Sleep(50);
            release.Set();
        });
        busy.Stop(0);
        releaser.Wait();
        That(waiting.Disposed && waiting.Turns == 0, "a service still queued when the thread stops is disposed without running");
        var late = new RecordingService();
        busy.Attach(late);
        That(late.Disposed && busy.Connect(UdpPaths.For(new IPEndPoint(IPAddress.Loopback, 9)), null).State == ConnectAttemptState.Failed,
            "a service attached to a stopped thread is disposed at once, and a connection fails at once");
    }

    private static NetworkThread StartThread(int seed)
    {
        var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(seed, 1), new TransportOptions(), Clock()));
        var thread = new NetworkThread("TopiaForge test " + seed, new UdpSocketDriver(0), stack, Clock);
        thread.Start();
        return thread;
    }

    private static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();

    private static DescriptorFetch Fetch(IPAddress address, int port, string? expected) =>
        DescriptorClient.FetchAsync(new IPEndPoint(address, port), expected, TimeSpan.FromSeconds(3), CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Sends one raw request and returns the status line, with any body that follows it.</summary>
    private static string Raw(int port, string request, out byte[] body)
    {
        using var socket = Connect(port);
        socket.Send(Encoding.ASCII.GetBytes(request));
        socket.ReceiveTimeout = 3_000;
        var received = new MemoryStream();
        var buffer = new byte[65536];
        while (true)
        {
            int read;
            try
            {
                read = socket.Receive(buffer);
            }
            catch (SocketException)
            {
                break;
            }

            if (read == 0) break;
            received.Write(buffer, 0, read);
        }

        var all = received.ToArray();
        var newline = Array.IndexOf(all, (byte)'\n');
        if (newline < 0)
        {
            body = Array.Empty<byte>();
            return Encoding.ASCII.GetString(all);
        }

        body = all.Skip(newline + 1).ToArray();
        return Encoding.ASCII.GetString(all, 0, newline);
    }

    private static Socket Connect(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        return socket;
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var socket = Connect(port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool ReadsEndOfStream(Socket socket, int milliseconds)
    {
        socket.ReceiveTimeout = milliseconds;
        try
        {
            return socket.Receive(new byte[16]) == 0;
        }
        catch (SocketException exception)
        {
            return exception.SocketErrorCode == SocketError.ConnectionReset;
        }
    }

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

    /// <summary>A service that only records whether it ran and whether it was disposed.</summary>
    private sealed class RecordingService : INetworkService
    {
        internal int Turns { get; private set; }

        internal bool Disposed { get; private set; }

        public void Turn(NetworkStack stack, long now) => Turns++;

        public bool TryClaim(TransportInfoReply reply, long now) => false;

        public long NextTimeoutMicros() => long.MaxValue;

        public void Dispose() => Disposed = true;
    }

    /// <summary>A package archive that reads once and then fails, as a file on a failing disk would.</summary>
    private sealed class BrokenArchive : Stream
    {
        internal const int Size = 300_000;
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => Size;

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position > 0) throw new IOException("The disk failed.");
            var read = Math.Min(count, 70_000);
            Array.Clear(buffer, offset, read);
            position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
