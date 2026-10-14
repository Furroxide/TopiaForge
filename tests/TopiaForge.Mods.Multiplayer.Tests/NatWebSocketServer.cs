using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A minimal RFC 6455 server for the signaling client's tests, bound to the loopback address only: one connection at a
/// time, the upgrade handshake, masked client frames, and whatever text, binary or close frames a test sends back.
/// </summary>
internal sealed class LoopbackWebSocketServer : IDisposable
{
    private const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Thread thread;
    private readonly object gate = new();
    private NetworkStream? stream;
    private volatile bool stopping;

    internal LoopbackWebSocketServer(int refuseWithStatus = 0)
    {
        RefuseWithStatus = refuseWithStatus;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        thread = new Thread(Run) { IsBackground = true, Name = "TopiaForge test signaling server" };
        thread.Start();
    }

    internal int Port { get; }

    internal int RefuseWithStatus { get; }

    internal string? Path { get; private set; }

    internal string? Authorization { get; private set; }

    internal ConcurrentQueue<string> Received { get; } = new();

    internal int? ClosedByClient { get; private set; }

    internal bool Upgraded { get; private set; }

    internal void SendText(string text) => SendFrame(0x1, Encoding.UTF8.GetBytes(text));

    internal void SendBinary(byte[] bytes) => SendFrame(0x2, bytes);

    internal void SendClose(int code) => SendFrame(0x8, new[] { (byte)(code >> 8), (byte)code });

    public void Dispose()
    {
        stopping = true;
        listener.Stop();
        lock (gate) stream?.Dispose();
        thread.Join(2000);
    }

    private void SendFrame(int opcode, byte[] payload)
    {
        lock (gate)
        {
            if (stream == null) return;
            var header = new List<byte> { (byte)(0x80 | opcode) };
            if (payload.Length < 126)
            {
                header.Add((byte)payload.Length);
            }
            else if (payload.Length <= 0xFFFF)
            {
                header.Add(126);
                header.Add((byte)(payload.Length >> 8));
                header.Add((byte)payload.Length);
            }
            else
            {
                header.Add(127);
                for (var shift = 56; shift >= 0; shift -= 8) header.Add((byte)((long)payload.Length >> shift));
            }

            try
            {
                stream.Write(header.ToArray(), 0, header.Count);
                stream.Write(payload, 0, payload.Length);
            }
            catch (IOException)
            {
                // The client went away.
            }
        }
    }

    private void Run()
    {
        while (!stopping)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            using (client)
            {
                try
                {
                    Serve(client.GetStream());
                }
                catch (IOException)
                {
                    // The connection ended.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    private void Serve(NetworkStream network)
    {
        var head = ReadHead(network);
        var lines = head.Split("\r\n");
        Path = lines[0].Split(' ')[1];
        string? key = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) Authorization = value;
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
        }

        if (RefuseWithStatus != 0 || key == null)
        {
            var refusal = Encoding.ASCII.GetBytes("HTTP/1.1 " + (RefuseWithStatus == 0 ? 400 : RefuseWithStatus) + " Refused\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            network.Write(refusal, 0, refusal.Length);
            return;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Magic)));
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
        network.Write(response, 0, response.Length);
        lock (gate) stream = network;
        Upgraded = true;
        while (!stopping)
        {
            var first = network.ReadByte();
            var second = network.ReadByte();
            if (first < 0 || second < 0) return;
            var opcode = first & 0x0F;
            long length = second & 0x7F;
            if (length == 126) length = (network.ReadByte() << 8) | network.ReadByte();
            else if (length == 127) for (var index = 0; index < 8; index++) length = (length << 8) | (uint)network.ReadByte();
            var mask = new byte[4];
            if ((second & 0x80) != 0) ReadExactly(network, mask);
            var payload = new byte[length];
            ReadExactly(network, payload);
            for (var index = 0; index < payload.Length; index++) payload[index] ^= mask[index % 4];
            if (opcode == 0x1) Received.Enqueue(Encoding.UTF8.GetString(payload));
            else if (opcode == 0x8)
            {
                ClosedByClient = payload.Length >= 2 ? (payload[0] << 8) | payload[1] : 1005;
                SendClose(1000);
                return;
            }
            else if (opcode == 0x9) SendFrame(0xA, payload);
        }
    }

    private static string ReadHead(NetworkStream network)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 8192)
        {
            var next = network.ReadByte();
            if (next < 0) break;
            bytes.Add((byte)next);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10) break;
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static void ReadExactly(NetworkStream network, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = network.Read(buffer, read, buffer.Length - read);
            if (count <= 0) throw new IOException("closed");
            read += count;
        }
    }
}
