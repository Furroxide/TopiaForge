extern alias provider;

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// What a turn of the network thread costs with nothing to do, over real sockets on the loopback interface. Every socket
/// loop asks its socket before it reads, accepts or writes, so an idle turn throws nothing and allocates nothing, and a
/// datagram that has arrived is still read in the turn that follows, as much of it as a turn reads.
/// </summary>
internal static class NetworkIdleTurnTests
{
    private const int Turns = 10_000;

    /// <summary>The first-chance exceptions raised on this thread while a test counts them.</summary>
    [ThreadStatic]
    private static int raised;

    internal static void AnIdleNetworkTurnThrowsAndAllocatesNothing()
    {
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            using (var stack = Stack(170))
            using (var browser = new LanBrowser(0, bindAddress: IPAddress.Loopback))
            {
                Idle(() => browser.Turn(stack, 1_000), "the LAN browser's turn with nothing to read");
            }

            // The game's socket, dual mode wherever this system has IPv6, then the IPv4 socket a system without IPv6 gets.
            var buffer = new byte[TransportOptions.MaximumDatagramBytes];
            foreach (var withoutIPv6 in new[] { false, true })
            {
                using var driver = new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6);
                Idle(() => driver.TryReceive(buffer, out _, out _), "the game socket's read with nothing waiting" + Over(withoutIPv6));
            }

            DescriptorEndpointIdles();

            // The whole turn of a real network thread, over each socket, with the browser and a descriptor endpoint
            // attached: whatever runs on the thread with nothing to do, the stack's own poll and wait included.
            foreach (var withoutIPv6 in new[] { false, true }) IdleThread(withoutIPv6);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }
    }

    internal static void ADatagramThatArrivesIsReadInTheSameTurn()
    {
        using (var stack = Stack(172))
        using (var browser = new LanBrowser(0, expiryMicros: 60_000_000, bindAddress: IPAddress.Loopback))
        {
            var senders = Enumerable.Range(1, 9).Select(octet => new Sender(octet)).ToArray();
            try
            {
                var to = new IPEndPoint(IPAddress.Loopback, browser.LocalPort);
                senders[0].Send(new LanBeacon(Id(0), 27_250).Encode(), to);
                Arrive(browser);
                browser.Turn(stack, 1_000);
                That(Listed(browser).SequenceEqual(new[] { Id(0) }), "a beacon that has arrived is heard in the next turn");

                // An empty datagram leaves nothing available, so a browser that asked for that would never read past it.
                senders[0].Send(Array.Empty<byte>(), to);
                senders[0].Send(new LanBeacon(Id(1), 27_250).Encode(), to);
                Arrive(browser);
                browser.Turn(stack, 1_000);
                That(Listed(browser).SequenceEqual(new[] { Id(0), Id(1) }), "and so is a beacon behind an empty datagram, in the same turn");

                // A turn still reads at most 64 beacons. Eight senders fill the list with 64 sessions, two of them heard
                // already, and a ninth sends six more, which the next turn reads and drops, since the list is full.
                for (var sender = 0; sender < 8; sender++)
                {
                    for (var session = 0; session < 8; session++) senders[sender].Send(new LanBeacon(Id((sender * 8) + session), 27_250).Encode(), to);
                }

                for (var session = 64; session < 70; session++) senders[8].Send(new LanBeacon(Id(session), 27_250).Encode(), to);
                Arrive(browser);
                browser.Turn(stack, 1_000);
                That(Listed(browser).Count == 64 && browser.Dropped == 0, "one turn reads 64 beacons and no more (" + Listed(browser).Count + " listed, " + browser.Dropped + " dropped)");
                browser.Turn(stack, 1_000);
                That(Listed(browser).Count == 64 && browser.Dropped == 6, "and the next turn reads the six behind them (" + browser.Dropped + " dropped)");
            }
            finally
            {
                foreach (var sender in senders) sender.Dispose();
            }
        }

        // The game's socket over both its forms: a datagram that has arrived is read by the next call, and one behind an
        // empty datagram by the call after the one that reads the empty datagram.
        var buffer = new byte[TransportOptions.MaximumDatagramBytes];
        foreach (var withoutIPv6 in new[] { false, true })
        {
            using var sender = new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6);
            using var receiver = new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6);
            var path = UdpPaths.For(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort));
            sender.Send(path, new byte[] { 4, 5, 6 }, 0, 3);
            That(receiver.WaitReadable(2_000_000) && receiver.TryReceive(buffer, out var count, out var from) && count == 3 && buffer[2] == 6 &&
                 UdpPaths.TryGetEndPoint(from!, out var at) && at.Port == sender.LocalPort,
                "a datagram that has arrived is read by the next call" + Over(withoutIPv6));
            sender.Send(path, buffer, 0, 0);
            sender.Send(path, new byte[] { 7 }, 0, 1);
            Thread.Sleep(20);
            That(receiver.WaitReadable(2_000_000) && receiver.TryReceive(buffer, out var empty, out var emptyFrom) && empty == 0 && emptyFrom != null &&
                 receiver.TryReceive(buffer, out var next, out _) && next == 1 && buffer[0] == 7,
                "and one behind an empty datagram by the call after it" + Over(withoutIPv6));
            That(!receiver.TryReceive(buffer, out _, out _), "then nothing is left to read" + Over(withoutIPv6));
        }
    }

    /// <summary>
    /// The descriptor endpoint's loops: its accepts with no connection waiting, the read of a connection that has sent
    /// nothing, and the write to a reader that has stopped reading, whose transfer resumes whole once it reads again.
    /// </summary>
    private static void DescriptorEndpointIdles()
    {
        using var stack = Stack(171);
        var archive = new byte[16 * 1024 * 1024];
        new Random(171).NextBytes(archive);
        using var endpoint = new DescriptorEndpoint(0, _ => new MemoryStream(archive, false), bindAddress: IPAddress.Loopback);
        Idle(() => endpoint.Turn(stack, 1_000), "the descriptor endpoint's turn with no connection waiting");

        using (var quiet = Connect(endpoint.LocalPort, receiveBuffer: 0))
        {
            That(TurnUntil(endpoint, stack, 1_000, () => endpoint.ConnectionCount == 1), "the endpoint takes a connection");
            Idle(() => endpoint.Turn(stack, 1_000), "its turn with a connection that has sent nothing");
        }

        That(TurnUntil(endpoint, stack, 1_000, () => endpoint.ConnectionCount == 0), "a connection its peer closed reads as closed, and goes");

        // A reader asks for an archive larger than the sockets between them hold, then stops reading.
        using var stalled = Connect(endpoint.LocalPort, receiveBuffer: 4_096);
        stalled.Send(Encoding.ASCII.GetBytes("TFD1 package " + new string('a', 64) + "\n"));
        Thread.Sleep(20);
        for (var turn = 0; turn < 200; turn++) endpoint.Turn(stack, 2_000_000);
        That(endpoint.ConnectionCount == 1, "a reader that stopped reading holds its connection, its transfer not done");
        Idle(() => endpoint.Turn(stack, 2_000_000), "its turn with a reader that stopped reading");

        var header = "TFD1 200 " + archive.Length + "\n";
        var received = new MemoryStream();
        var scratch = new byte[64 * 1024];
        var deadline = Environment.TickCount64 + 20_000;
        while (received.Length < header.Length + archive.Length && Environment.TickCount64 < deadline)
        {
            endpoint.Turn(stack, 2_000_000);
            while (stalled.Poll(1_000, SelectMode.SelectRead))
            {
                var read = stalled.Receive(scratch);
                if (read == 0) break;
                received.Write(scratch, 0, read);
            }
        }

        var bytes = received.ToArray();
        That(bytes.Length == header.Length + archive.Length && Encoding.ASCII.GetString(bytes, 0, header.Length) == header &&
             bytes.AsSpan(header.Length).SequenceEqual(archive),
            "once it reads again, the whole archive arrives, byte for byte (" + bytes.Length + " bytes)");
    }

    /// <summary>
    /// Lets a network thread idle and samples, at each of its turns, what the thread has allocated and raised so far. A cost
    /// paid every turn shows in every stretch of turns, while the runtime's one-off work on the thread, such as moving the
    /// loop to optimized code, shows in one; so the stretch of 50 turns that cost least must cost nothing.
    /// </summary>
    private static void IdleThread(bool withoutIPv6)
    {
        var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(173, 1), new TransportOptions(), Clock()));
        var thread = new NetworkThread("TopiaForge idle test", new UdpSocketDriver(0, IPAddress.Loopback, withoutIPv6), stack, Clock);
        var sampler = new TurnSampler();
        try
        {
            thread.Start();
            thread.Attach(new LanBrowser(0, bindAddress: IPAddress.Loopback));
            thread.Attach(new DescriptorEndpoint(0, bindAddress: IPAddress.Loopback));
            thread.Attach(sampler);
            That(SpinWait.SpinUntil(() => sampler.Full, 10_000), "the network thread turns while idle" + Over(withoutIPv6));
        }
        finally
        {
            thread.Stop(0);
        }

        var (bytes, thrown) = sampler.FewestInAStretch(50);
        That(bytes == 0 && thrown == 0,
            "an idle turn of the network thread allocates nothing and throws nothing" + Over(withoutIPv6) + " (the cheapest 50 turns: " + bytes +
            " bytes, " + thrown + " exceptions)");
    }

    /// <summary>
    /// Runs <paramref name="turn"/> with nothing to do, as the repository's zero-allocation tests measure, and checks that
    /// it allocates nothing and raises no exception, not even one it catches itself.
    /// </summary>
    private static void Idle(Action turn, string what)
    {
        var before = raised;
        var allocated = AllocationPasses.Fewest(() => Turned(turn));
        var thrown = raised - before;
        That(allocated == 0 && thrown == 0,
            what + " allocates nothing and throws nothing (" + allocated + " bytes in " + Turns + " turns, " + thrown + " exceptions in all)");
    }

    /// <summary>Runs <paramref name="turn"/> <see cref="Turns"/> times and returns what this thread allocated meanwhile.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Turned(Action turn)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Turns; index++) turn();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void Count(object? sender, FirstChanceExceptionEventArgs arguments) => raised++;

    /// <summary>Waits until the browser's socket holds what was sent to it, without reading it, so the next turn finds it there.</summary>
    private static void Arrive(LanBrowser browser)
    {
        var field = typeof(LanBrowser).GetField("socket", BindingFlags.NonPublic | BindingFlags.Instance) ??
                    throw new InvalidOperationException("Assertion failed: the browser listens on a socket of its own");
        Thread.Sleep(20);
        That(((Socket)field.GetValue(browser)!).Poll(2_000_000, SelectMode.SelectRead), "what was sent to the browser arrives");
    }

    private static bool TurnUntil(DescriptorEndpoint endpoint, NetworkStack stack, long now, Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 3_000;
        while (Environment.TickCount64 < deadline)
        {
            endpoint.Turn(stack, now);
            if (condition()) return true;
            Thread.Sleep(1);
        }

        return condition();
    }

    /// <summary>A TCP connection to the endpoint; a non-zero <paramref name="receiveBuffer"/> keeps what it holds unread small.</summary>
    private static Socket Connect(int port, int receiveBuffer)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (receiveBuffer > 0) socket.ReceiveBufferSize = receiveBuffer;
            socket.Connect(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static NetworkStack Stack(int seed) => new(new TransportEndpoint(TransportPair.Key(seed, 1), new TransportOptions(), 0));

    private static long Clock() => provider::TopiaForge.Multiplayer.MultiplayerProvider.MonotonicMicros();

    private static string Over(bool withoutIPv6) => withoutIPv6 ? ", over the IPv4 socket" : ", over the game's socket";

    private static List<string> Listed(LanBrowser browser) => browser.Servers.Select(server => server.ServerId).ToList();

    /// <summary>A server id that sorts as its number does, as the browser lists them.</summary>
    private static string Id(int number) => number.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A service that writes down, on the network thread at each turn, what that thread has allocated and raised so far,
    /// into arrays it allocates up front, so that taking a sample costs the turn nothing.
    /// </summary>
    private sealed class TurnSampler : INetworkService
    {
        private const int Samples = 600;
        private const int Skipped = 100;
        private readonly long[] allocated = new long[Samples];
        private readonly int[] thrown = new int[Samples];
        private int count;

        /// <summary>Gets whether every sample is taken; the thread writes no more after that.</summary>
        internal bool Full => Volatile.Read(ref count) == Samples;

        public void Turn(NetworkStack stack, long now)
        {
            var index = count;
            if (index == Samples) return;
            allocated[index] = GC.GetAllocatedBytesForCurrentThread();
            thrown[index] = raised;
            Volatile.Write(ref count, index + 1);
        }

        public bool TryClaim(TransportInfoReply reply, long now) => false;

        public long NextTimeoutMicros() => long.MaxValue;

        public void Dispose()
        {
        }

        /// <summary>
        /// Gets the bytes and the exceptions of the cheapest stretch of <paramref name="turns"/> turns, past the first
        /// turns, which still start the services and compile the loop.
        /// </summary>
        internal (long Bytes, int Thrown) FewestInAStretch(int turns)
        {
            var bytes = long.MaxValue;
            var exceptions = int.MaxValue;
            for (var start = Skipped; start + turns < Samples; start += turns)
            {
                bytes = Math.Min(bytes, allocated[start + turns] - allocated[start]);
                exceptions = Math.Min(exceptions, thrown[start + turns] - thrown[start]);
            }

            return (bytes, exceptions);
        }
    }

    /// <summary>A socket on the loopback address 127.0.0.<c>n</c>, so each test sender has an address of its own.</summary>
    private sealed class Sender : IDisposable
    {
        private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        internal Sender(int lastOctet)
        {
            try
            {
                socket.Bind(new IPEndPoint(new IPAddress(new byte[] { 127, 0, 0, (byte)lastOctet }), 0));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        internal void Send(byte[] datagram, IPEndPoint to) => socket.SendTo(datagram, to);

        public void Dispose() => socket.Dispose();
    }
}
