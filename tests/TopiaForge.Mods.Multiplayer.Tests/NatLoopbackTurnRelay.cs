using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The fake TURN server on real loopback sockets, run by its own thread: one listening socket, and one more for each
/// relayed address it allocates, all on 127.0.0.1.
/// </summary>
internal sealed class LoopbackTurnRelay : IDisposable
{
    private readonly Socket listening = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Dictionary<int, Socket> relays = new();
    private readonly object gate = new();
    private readonly Thread thread;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private volatile bool stopping;

    internal LoopbackTurnRelay(int seed)
    {
        if (OperatingSystem.IsWindows()) listening.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
        listening.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)listening.LocalEndPoint!).Port;
        Server = new FakeTurnServer(IPAddress.Loopback, seed, Port) { RelayPorts = OpenRelay };
        thread = new Thread(Run) { IsBackground = true, Name = "TopiaForge test TURN relay" };
        thread.Start();
    }

    internal int Port { get; }

    internal FakeTurnServer Server { get; }

    internal int Allocations
    {
        get
        {
            lock (gate) return Server.Allocations.Count;
        }
    }

    public void Dispose()
    {
        stopping = true;
        thread.Join(2000);
        listening.Dispose();
        foreach (var relay in relays.Values) relay.Dispose();
    }

    private int OpenRelay()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows()) socket.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        relays.Add(port, socket);
        return port;
    }

    private void Run()
    {
        var buffer = new byte[2048];
        while (!stopping)
        {
            var readable = new List<Socket> { listening };
            lock (gate) readable.AddRange(relays.Values);
            try
            {
                Socket.Select(readable, null, null, 2_000);
            }
            catch (SocketException)
            {
                continue;
            }

            lock (gate)
            {
                var now = clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;
                foreach (var socket in readable)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int count;
                    try
                    {
                        count = socket.ReceiveFrom(buffer, ref from);
                    }
                    catch (SocketException)
                    {
                        continue;
                    }

                    Server.Receive((IPEndPoint)from, (IPEndPoint)socket.LocalEndPoint!, buffer.AsSpan(0, count).ToArray(), now);
                }

                Server.Poll(now);
                while (Server.Outbox.Count > 0)
                {
                    var (source, to, bytes) = Server.Outbox.Dequeue();
                    var sender = source.Port == Port ? listening : relays.TryGetValue(source.Port, out var relay) ? relay : null;
                    try
                    {
                        sender?.SendTo(bytes, to);
                    }
                    catch (SocketException)
                    {
                        // Lost, as on any network.
                    }
                }
            }
        }
    }
}
