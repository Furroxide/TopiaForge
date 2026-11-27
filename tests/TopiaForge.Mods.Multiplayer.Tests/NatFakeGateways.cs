using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A NAT-PMP gateway on the loopback address: it answers external-address and UDP mapping requests (RFC 6886), records
/// each one, and can first send a forged answer from another port, which a client must ignore.
/// </summary>
internal sealed class LoopbackNatPmpGateway : IDisposable
{
    private readonly Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Socket decoy = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly Thread thread;
    private volatile bool stopping;
    private uint epoch = 1000;

    internal LoopbackNatPmpGateway(IPAddress external)
    {
        External = external;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        decoy.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.ReceiveTimeout = 100;
        EndPoint = (IPEndPoint)socket.LocalEndPoint!;
        thread = new Thread(Run) { IsBackground = true, Name = "TopiaForge test NAT-PMP gateway" };
        thread.Start();
    }

    internal IPAddress External { get; }

    internal IPEndPoint EndPoint { get; }

    internal int ResultCode { get; set; }

    internal bool SendDecoyFirst { get; set; }

    /// <summary>Gets each request: opcode, internal port, suggested external port and requested lifetime.</summary>
    internal ConcurrentQueue<(int Opcode, int Internal, int Suggested, uint Lifetime)> Requests { get; } = new();

    public void Dispose()
    {
        stopping = true;
        thread.Join(2000);
        socket.Dispose();
        decoy.Dispose();
    }

    private void Run()
    {
        var buffer = new byte[64];
        while (!stopping)
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

            if (count < 2 || buffer[0] != 0) continue;
            var opcode = buffer[1];
            byte[] answer;
            if (opcode == 0 && count == 2)
            {
                Requests.Enqueue((0, 0, 0, 0));
                answer = new byte[12];
                answer[1] = 128;
                answer[3] = (byte)ResultCode;
                Write(answer, 4, epoch++);
                External.GetAddressBytes().CopyTo(answer, 8);
            }
            else if (opcode == 1 && count == 12)
            {
                var inside = (buffer[4] << 8) | buffer[5];
                var suggested = (buffer[6] << 8) | buffer[7];
                var lifetime = ((uint)buffer[8] << 24) | ((uint)buffer[9] << 16) | ((uint)buffer[10] << 8) | buffer[11];
                Requests.Enqueue((1, inside, suggested, lifetime));
                var granted = Math.Min(lifetime, 7200u);
                var mapped = lifetime == 0 ? 0 : suggested == 0 ? inside : suggested;
                answer = new byte[16];
                answer[1] = 129;
                answer[3] = (byte)ResultCode;
                Write(answer, 4, epoch++);
                answer[8] = (byte)(inside >> 8);
                answer[9] = (byte)inside;
                answer[10] = (byte)(mapped >> 8);
                answer[11] = (byte)mapped;
                Write(answer, 12, granted);
            }
            else
            {
                continue;
            }

            if (SendDecoyFirst)
            {
                var forged = (byte[])answer.Clone();
                if (opcode == 0) IPAddress.Parse("203.0.113.66").GetAddressBytes().CopyTo(forged, 8);
                else forged[11] ^= 0x55;
                decoy.SendTo(forged, from);
            }

            socket.SendTo(answer, from);
        }
    }

    private static void Write(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}

/// <summary>
/// A UPnP gateway on the loopback address: an SSDP responder on UDP and an HTTP server for the device description and
/// the WAN connection service's SOAP actions. It records each action with its arguments.
/// </summary>
internal sealed class LoopbackUpnpGateway : IDisposable
{
    private readonly Socket ssdp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly TcpListener http = new(IPAddress.Loopback, 0);
    private readonly Thread ssdpThread;
    private readonly Thread httpThread;
    private volatile bool stopping;

    internal LoopbackUpnpGateway(IPAddress external)
    {
        External = external;
        ssdp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        ssdp.ReceiveTimeout = 100;
        SearchTarget = (IPEndPoint)ssdp.LocalEndPoint!;
        http.Start();
        HttpPort = ((IPEndPoint)http.LocalEndpoint).Port;
        ssdpThread = new Thread(RunSsdp) { IsBackground = true, Name = "TopiaForge test SSDP" };
        httpThread = new Thread(RunHttp) { IsBackground = true, Name = "TopiaForge test IGD" };
        ssdpThread.Start();
        httpThread.Start();
    }

    internal IPAddress External { get; }

    internal IPEndPoint SearchTarget { get; }

    internal int HttpPort { get; }

    /// <summary>Gets or sets whether the gateway grants only permanent mappings, refusing a lease with error 725.</summary>
    internal bool PermanentOnly { get; set; }

    /// <summary>Gets the external ports that already hold someone else's mapping (error 718).</summary>
    internal HashSet<int> Taken { get; } = new();

    internal string Description { get; set; } = string.Empty;

    internal ConcurrentQueue<(string Action, Dictionary<string, string> Arguments)> Actions { get; } = new();

    public void Dispose()
    {
        stopping = true;
        http.Stop();
        ssdpThread.Join(2000);
        httpThread.Join(2000);
        ssdp.Dispose();
    }

    private void RunSsdp()
    {
        var buffer = new byte[2048];
        while (!stopping)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int count;
            try
            {
                count = ssdp.ReceiveFrom(buffer, ref from);
            }
            catch (SocketException)
            {
                continue;
            }

            if (!Encoding.ASCII.GetString(buffer, 0, count).StartsWith("M-SEARCH", StringComparison.Ordinal)) continue;
            var answer = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
                         "USN: uuid:test::urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nLOCATION: http://127.0.0.1:" + HttpPort + "/rootDesc.xml\r\n\r\n";
            ssdp.SendTo(Encoding.ASCII.GetBytes(answer), from);
        }
    }

    private void RunHttp()
    {
        while (!stopping)
        {
            TcpClient client;
            try
            {
                client = http.AcceptTcpClient();
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
                    // The client went away.
                }
            }
        }
    }

    private void Serve(NetworkStream stream)
    {
        var head = new List<byte>();
        while (head.Count < 8192 && !(head.Count >= 4 && head[^4] == 13 && head[^3] == 10 && head[^2] == 13 && head[^1] == 10))
        {
            var next = stream.ReadByte();
            if (next < 0) return;
            head.Add((byte)next);
        }

        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var length = lines.Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Select(line => int.Parse(line[15..].Trim())).FirstOrDefault();
        var body = new byte[length];
        var read = 0;
        while (read < length) read += stream.Read(body, read, length - read);
        string status;
        string content;
        if (lines[0].StartsWith("GET /rootDesc.xml", StringComparison.Ordinal))
        {
            status = "200 OK";
            content = Description;
        }
        else
        {
            (status, content) = Soap(Encoding.UTF8.GetString(body));
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/xml\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
        stream.Write(response, 0, response.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private (string Status, string Body) Soap(string envelope)
    {
        var action = new[] { "GetExternalIPAddress", "AddPortMapping", "DeletePortMapping" }.FirstOrDefault(name => envelope.Contains(":" + name + " ", StringComparison.Ordinal)) ?? "?";
        var arguments = new Dictionary<string, string>();
        foreach (var name in new[] { "NewRemoteHost", "NewExternalPort", "NewProtocol", "NewInternalPort", "NewInternalClient", "NewEnabled", "NewPortMappingDescription", "NewLeaseDuration" })
        {
            var open = envelope.IndexOf("<" + name + ">", StringComparison.Ordinal);
            if (open < 0) continue;
            var start = open + name.Length + 2;
            arguments[name] = envelope[start..envelope.IndexOf("</" + name + ">", start, StringComparison.Ordinal)];
        }

        Actions.Enqueue((action, arguments));
        const string Ok = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>";
        const string End = "</s:Body></s:Envelope>";
        if (action == "GetExternalIPAddress")
        {
            return ("200 OK", Ok + "<u:GetExternalIPAddressResponse xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"><NewExternalIPAddress>" + External +
                              "</NewExternalIPAddress></u:GetExternalIPAddressResponse>" + End);
        }

        if (action == "AddPortMapping")
        {
            var port = int.Parse(arguments["NewExternalPort"]);
            if (PermanentOnly && arguments["NewLeaseDuration"] != "0") return Fault(725);
            if (Taken.Contains(port)) return Fault(718);
        }

        return ("200 OK", Ok + "<u:" + action + "Response xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\"/>" + End);

        static (string, string) Fault(int code) =>
            ("500 Internal Server Error", "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail>" +
                                          "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code + "</errorCode><errorDescription>refused</errorDescription>" +
                                          "</UPnPError></detail></s:Fault></s:Body></s:Envelope>");
    }
}
