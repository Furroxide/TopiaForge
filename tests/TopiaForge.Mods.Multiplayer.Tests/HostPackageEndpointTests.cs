extern alias provider;

using System.Net;
using System.Net.Sockets;
using System.Text;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class HostPackageEndpointTests
{
    internal static void Run()
    {
        VerificationDoesNotBlockTheNetwork();
        AbandonedWorkersStayBounded();
        TransfersKeepTheTwoMinuteBudget();
        DeveloperModeIsCheckedAfterHashing();
    }

    private static void VerificationDoesNotBlockTheNetwork()
    {
        using var stack = Stack();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var archive = new TrackedStream();
        using var endpoint = new DescriptorEndpoint(0, (_, _) => { started.Set(); release.Wait(); return archive; }, bindAddress: IPAddress.Loopback);
        try
        {
            using var waiting = Connect(endpoint.LocalPort);
            waiting.Send(Request);
            TurnUntil(endpoint, stack, 0, () => started.IsSet);
            using var descriptor = Connect(endpoint.LocalPort);
            descriptor.Send(Encoding.ASCII.GetBytes("TFD1 descriptor\n"));
            TurnUntil(endpoint, stack, 0, () => descriptor.Available != 0);
            That(Read(descriptor).SequenceEqual(Encoding.ASCII.GetBytes("TFD1 404\n")), "descriptors still answer while an archive waits for disk");
            endpoint.Dispose();
        }
        finally { release.Set(); }
        That(SpinWait.SpinUntil(() => archive.Disposed, 5_000), "an archive that opens after the endpoint closes is disposed");
    }

    private static void AbandonedWorkersStayBounded()
    {
        using var stack = Stack();
        using var release = new ManualResetEventSlim();
        var started = 0;
        var finished = 0;
        var tokens = new System.Collections.Concurrent.ConcurrentBag<CancellationToken>();
        using var endpoint = new DescriptorEndpoint(0, (_, token) =>
        {
            Interlocked.Increment(ref started);
            tokens.Add(token);
            release.Wait();
            Interlocked.Increment(ref finished);
            return null;
        }, bindAddress: IPAddress.Loopback);
        var sockets = new List<Socket>();
        try
        {
            for (var index = 0; index < DescriptorEndpoint.MaximumConnections; index++)
            {
                var socket = Connect(endpoint.LocalPort);
                sockets.Add(socket);
                socket.Send(Request);
                var expected = index + 1;
                TurnUntil(endpoint, stack, 0, () => Volatile.Read(ref started) == expected);
            }
            endpoint.Turn(stack, 120_000_000);
            That(endpoint.ConnectionCount == DescriptorEndpoint.MaximumConnections, "verification retains the existing two-minute budget");
            endpoint.Turn(stack, 120_000_001);
            That(endpoint.ConnectionCount == 0 && tokens.All(token => token.IsCancellationRequested), "the deadline cancels every pending verification");
            using var ninth = Connect(endpoint.LocalPort);
            ninth.Send(Request);
            TurnUntil(endpoint, stack, 121_000_000, () => ninth.Available != 0);
            That(Read(ninth).SequenceEqual(Encoding.ASCII.GetBytes("TFD1 503\n")) && Volatile.Read(ref started) == DescriptorEndpoint.MaximumConnections,
                "timed-out disk workers keep their slots; reconnecting cannot queue more");
        }
        finally
        {
            release.Set();
            foreach (var socket in sockets) socket.Dispose();
            That(SpinWait.SpinUntil(() => Volatile.Read(ref finished) == Volatile.Read(ref started), 5_000), "all test disk workers finish");
        }
    }

    private static void TransfersKeepTheTwoMinuteBudget()
    {
        using var stack = Stack();
        var archive = new TrackedStream(new byte[16 * 1024 * 1024]);
        using var endpoint = new DescriptorEndpoint(0, (_, _) => archive, bindAddress: IPAddress.Loopback);
        using var stalled = Connect(endpoint.LocalPort);
        stalled.ReceiveBufferSize = 1024;
        stalled.Send(Request);
        TurnUntil(endpoint, stack, 0, () => stalled.Available != 0);
        endpoint.Turn(stack, 120_000_000);
        That(endpoint.ConnectionCount == 1 && !archive.Disposed, "an unfinished transfer survives up to two minutes");
        endpoint.Turn(stack, 120_000_001);
        That(endpoint.ConnectionCount == 0 && archive.Disposed, "the transfer deadline closes its archive and connection");
    }

    private static void DeveloperModeIsCheckedAfterHashing()
    {
        using var files = new HostPackageTests.PackageFiles();
        var reads = 0;
        var archives = new HostPackageArchives(files.Packages, files.Root.FullName, () => ++reads == 1);
        using var offered = archives.Open(files.Sha, CancellationToken.None);
        That(offered == null && reads == 2, "disabling developer mode during verification prevents the answer");
        // The failed open released its file handle too.
        using var exclusive = new FileStream(files.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static readonly byte[] Request = Encoding.ASCII.GetBytes("TFD1 package " + new string('a', 64) + "\n");
    private static NetworkStack Stack() => new(new TransportEndpoint(TransportPair.Key(9122, 1), new TransportOptions(), 0));

    private static void TurnUntil(DescriptorEndpoint endpoint, NetworkStack stack, long now, Func<bool> condition)
    {
        That(SpinWait.SpinUntil(() => { endpoint.Turn(stack, now); return condition(); }, 10_000), "the loopback operation settles");
    }

    internal static byte[] Raw(int port, string sha256)
    {
        using var socket = Connect(port);
        socket.Send(Encoding.ASCII.GetBytes("TFD1 package " + sha256 + "\n"));
        return Read(socket);
    }

    private static Socket Connect(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveTimeout = 5_000 };
        socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
        return socket;
    }

    private static byte[] Read(Socket socket)
    {
        using var result = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = socket.Receive(buffer)) != 0) result.Write(buffer, 0, read);
        return result.ToArray();
    }

    private sealed class TrackedStream : MemoryStream
    {
        private int disposed;
        internal TrackedStream(byte[]? bytes = null) : base(bytes ?? new byte[] { 1, 2, 3 }, false) { }
        internal bool Disposed => Volatile.Read(ref disposed) != 0;
        protected override void Dispose(bool disposing) { base.Dispose(disposing); Interlocked.Exchange(ref disposed, 1); }
    }
}

/// <summary>Real sockets bound only to loopback, with no LAN broadcaster.</summary>
internal sealed class PackageNetworkFactory : INetworkFactory
{
    private readonly SocketNetworkFactory sockets = new(Clock, IPAddress.Loopback);
    internal static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();
    internal Func<string, CancellationToken, Stream?>? Packages { get; private set; }
    public bool OpenedIPv4Only => sockets.OpenedIPv4Only;
    public INetworkControl Start(int port, NetworkStack stack) => sockets.Start(port, stack);
    public TraversalNetwork StartTraversal(int port, NetworkStack stack, NatAgentOptions options) => throw new NotSupportedException();
    public DescriptorEndpoint? CreateDescriptorEndpoint(int port, bool relayOnly, Func<string, CancellationToken, Stream?>? packages = null)
    {
        Packages = packages;
        return sockets.CreateDescriptorEndpoint(port, relayOnly, packages);
    }
    public LanAnnouncer? CreateAnnouncer(LanBeacon beacon) => null;
    public LanBrowser? CreateBrowser(int discoveryPort) => null;
    public string? SourceOf(TransportPath path) => sockets.SourceOf(path);
    public byte[]? NetworkOf(TransportPath path) => sockets.NetworkOf(path);
}
