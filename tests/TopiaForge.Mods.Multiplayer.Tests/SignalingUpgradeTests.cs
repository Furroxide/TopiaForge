extern alias provider;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using provider::TopiaForge.Multiplayer.ControlPlane;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The signaling socket's upgrade, made over the HTTPS client's own connection so that every runtime opens it by the
/// same rules: a redirect is never followed, an answer of the wrong shape is refused, and the HTTPS client's deadlines
/// and certificate check hold. That no proxy is asked is <see cref="InternetConfigurationTests.SignalingNeverAsksAProxy"/>.
/// Every server binds the loopback address.
/// </summary>
internal static class SignalingUpgradeTests
{
    private const string Welcome = """{"peers":[],"role":"host","type":"welcome"}""";
    private static readonly string ServerId = new('a', 64);

    internal static void RedirectsToTheUpgradeAreNeverFollowed()
    {
        using var room = new LoopbackWebSocketServer();
        foreach (var status in new[] { 301, 302, 307, 308 })
        {
            foreach (var location in new[] { "http://127.0.0.1:" + room.Port + SignalingUpgrade.Path(ServerId), "ws://127.0.0.1:" + room.Port + SignalingUpgrade.Path(ServerId) })
            {
                using var moved = LoopbackHttpServer.Answering("HTTP/1.1 " + status + " Moved\r\nLocation: " + location + "\r\nContent-Length: 0\r\n\r\n");
                using var client = Open(moved.Port);
                That(Wait(() => client.State == SignalingState.Closed) && client.CloseReason == SignalingCloseReason.Refused,
                    "a " + status + " answer to the upgrade fails as refused (" + client.State + ", " + client.CloseReason + ")");
                That(moved.Connections == 1 && moved.Requests.Single().RequestLine == "GET " + SignalingUpgrade.Path(ServerId) + " HTTP/1.1",
                    "after one upgrade request to the origin");
            }
        }

        That(room.Path == null && !room.Upgraded, "and the place a redirect names is never contacted");
    }

    internal static void UpgradesOfTheWrongShapeAreRefused()
    {
        var shapes = new (string Name, Func<string, string> Answer)[]
        {
            ("an HTTP/1.0 answer", key => Upgrade(key).Replace("HTTP/1.1 101", "HTTP/1.0 101", StringComparison.Ordinal)),
            ("an ordinary answer", _ => LoopbackHttpServer.Answer(200, "{}")),
            ("a refusal", _ => LoopbackHttpServer.Answer(401, """{"error":{"code":"unauthorized","message":"Sign in."}}""")),
            ("an interim answer first", key => "HTTP/1.1 100 Continue\r\n\r\n" + Upgrade(key)),
            ("no Upgrade header", key => Upgrade(key).Replace("Upgrade: websocket\r\n", string.Empty, StringComparison.Ordinal)),
            ("an upgrade to another protocol", key => Upgrade(key).Replace("Upgrade: websocket", "Upgrade: h2c", StringComparison.Ordinal)),
            ("two Upgrade headers", key => Upgrade(key).Replace("Upgrade: websocket\r\n", "Upgrade: websocket\r\nUpgrade: websocket\r\n", StringComparison.Ordinal)),
            ("a Connection that names no upgrade", key => Upgrade(key).Replace("Connection: Upgrade", "Connection: keep-alive", StringComparison.Ordinal)),
            ("no accept", key => Upgrade(key).Replace("Sec-WebSocket-Accept: " + SignalingUpgrade.Accept(key) + "\r\n", string.Empty, StringComparison.Ordinal)),
            ("another key's accept", key => Upgrade(key).Replace(SignalingUpgrade.Accept(key), SignalingUpgrade.Accept("x" + key), StringComparison.Ordinal)),
            ("two accepts", key => Upgrade(key).Replace("\r\n\r\n", "\r\nSec-WebSocket-Accept: " + SignalingUpgrade.Accept(key) + "\r\n\r\n", StringComparison.Ordinal)),
            ("an extension never offered", key => Upgrade(key).Replace("\r\n\r\n", "\r\nSec-WebSocket-Extensions: permessage-deflate\r\n\r\n", StringComparison.Ordinal)),
            ("a subprotocol never offered", key => Upgrade(key).Replace("\r\n\r\n", "\r\nSec-WebSocket-Protocol: chat\r\n\r\n", StringComparison.Ordinal)),
            ("a body's length", key => Upgrade(key).Replace("\r\n\r\n", "\r\nContent-Length: 0\r\n\r\n", StringComparison.Ordinal)),
            ("a chunked body", key => Upgrade(key).Replace("\r\n\r\n", "\r\nTransfer-Encoding: chunked\r\n\r\n", StringComparison.Ordinal)),
            ("bare line feeds", key => Upgrade(key).Replace("\r\n", "\n", StringComparison.Ordinal)),
            ("a folded header", key => Upgrade(key).Replace("Upgrade: websocket\r\n", "Upgrade:\r\n websocket\r\n", StringComparison.Ordinal)),
            ("a head past 16 KiB", key => Upgrade(key).Replace("\r\n\r\n", "\r\n" + string.Concat(Enumerable.Repeat("X-Pad: " + new string('p', 1_000) + "\r\n", 17)) + "\r\n", StringComparison.Ordinal)),
            ("65 header lines", key => Upgrade(key).Replace("\r\n\r\n", "\r\n" + string.Concat(Enumerable.Repeat("X-Line: 1\r\n", 62)) + "\r\n", StringComparison.Ordinal)),
            ("a status line cut short", _ => "HTTP/1.1 10\r\n\r\n"),
            ("nothing but a closed connection", _ => string.Empty)
        };
        foreach (var (name, answer) in shapes)
        {
            using var server = Answering(answer);
            using var client = Open(server.Port);
            var expected = name == "nothing but a closed connection" ? SignalingCloseReason.Unreachable : SignalingCloseReason.Refused;
            That(Wait(() => client.State == SignalingState.Closed) && client.CloseReason == expected,
                name + " closes the socket as " + expected + " (" + client.State + ", " + client.CloseReason + ")");
        }

        // The right shape opens, with 64 header lines in all, and a frame sent in the same write as the answer is read, not lost.
        using var early = Answering(key => Upgrade(key).Replace("\r\n\r\n", "\r\n" + string.Concat(Enumerable.Repeat("X-Line: 1\r\n", 61)) + "\r\n", StringComparison.Ordinal) +
                                           Encoding.Latin1.GetString(TextFrame(Welcome)));
        using var opened = Open(early.Port);
        That(Wait(() => opened.TryReceive(out var message) && message.Type == SignalType.Welcome) && opened.State == SignalingState.Open,
            "an upgrade of the right shape opens, and a frame that came with it arrives (" + opened.State + ", " + opened.CloseReason + ")");
    }

    internal static void UpgradesSpelledAnyWayTheRfcAllowsOpen()
    {
        // Header names and the upgrade's tokens are read without regard to case, and Connection is a list (RFC 7230,
        // RFC 6455): an edge that writes them in lower case, or adds headers of its own, is never refused.
        var spellings = new (string Name, Func<string, string> Answer)[]
        {
            ("lower-case names and tokens", key => "HTTP/1.1 101 Switching Protocols\r\nupgrade: websocket\r\nconnection: upgrade\r\nsec-websocket-accept: " + SignalingUpgrade.Accept(key) + "\r\n\r\n"),
            ("an edge's own headers and a mixed-case protocol", key => "HTTP/1.1 101 Switching Protocols\r\nDate: Thu, 22 Oct 2026 10:00:00 GMT\r\nConnection: upgrade\r\nUpgrade: WebSocket\r\n" +
                                                                       "Sec-WebSocket-Accept: " + SignalingUpgrade.Accept(key) + "\r\nServer: edge\r\n\r\n"),
            ("a Connection that lists other tokens as well", key => Upgrade(key).Replace("Connection: Upgrade", "Connection: keep-alive, Upgrade", StringComparison.Ordinal))
        };
        foreach (var (name, answer) in spellings)
        {
            using var server = Answering(answer);
            using var client = Open(server.Port);
            That(Wait(() => client.State == SignalingState.Open), "an upgrade with " + name + " opens the socket (" + client.State + ", " + client.CloseReason + ")");
        }
    }

    internal static void ARefusedUpgradeClosesItsConnection()
    {
        // The connection, TLS and socket both, goes as soon as the answer is refused, rather than whenever it is collected.
        var refusals = new[]
        {
            LoopbackHttpServer.Answer(401, """{"error":{"code":"unauthorized","message":"Sign in."}}"""),
            "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:9/\r\nContent-Length: 0\r\n\r\n"
        };
        foreach (var refusal in refusals)
        {
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new LoopbackHttpServer(async (_, stream, token) =>
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(refusal), token);
                await stream.FlushAsync(token);
                using var holding = CancellationTokenSource.CreateLinkedTokenSource(token);
                holding.CancelAfter(3_000);
                try
                {
                    closed.TrySetResult(await stream.ReadAsync(new byte[16], holding.Token) == 0);
                }
                catch (OperationCanceledException)
                {
                    closed.TrySetResult(false);
                }
                catch (IOException)
                {
                    // Reset rather than closed: gone all the same.
                    closed.TrySetResult(true);
                }
            });
            using var client = Open(server.Port);
            That(closed.Task.Wait(6_000) && closed.Task.Result && Wait(() => client.CloseReason == SignalingCloseReason.Refused),
                "a refused upgrade closes its connection at once (" + refusal.Substring(9, 3) + ": " + client.State + ", " + client.CloseReason + ")");
        }
    }

    internal static void TheUpgradeKeepsTheClientsDeadlinesAndShape()
    {
        using (var silent = new LoopbackHttpServer((_, _, token) => Task.Delay(Timeout.Infinite, token)))
        {
            var clock = Stopwatch.StartNew();
            using var client = Open(silent.Port, TimeSpan.FromSeconds(1));
            That(Wait(() => client.State == SignalingState.Closed, 6_000) && client.CloseReason == SignalingCloseReason.Unreachable && clock.Elapsed < TimeSpan.FromSeconds(4),
                "a service that never answers the upgrade is given up on at the deadline (" + client.CloseReason + ", " + clock.ElapsedMilliseconds + " ms)");

            var transport = Transport(silent.Port);
            transport.TotalTimeout = TimeSpan.FromSeconds(1);
            clock.Restart();
            using var patient = SignalingUpgrade.Open(transport, ServerId, "tfj1_ticket", TimeSpan.FromSeconds(30));
            That(Wait(() => patient.State == SignalingState.Closed, 6_000) && patient.CloseReason == SignalingCloseReason.Unreachable && clock.Elapsed < TimeSpan.FromSeconds(4),
                "and the HTTPS client's whole deadline holds, whatever the opener allows (" + patient.CloseReason + ", " + clock.ElapsedMilliseconds + " ms)");
        }

        using (var trickle = new LoopbackHttpServer(async (request, stream, token) =>
               {
                   foreach (var character in Upgrade(request.Header("Sec-WebSocket-Key") ?? string.Empty))
                   {
                       await stream.WriteAsync(new[] { (byte)character }, token);
                       await stream.FlushAsync(token);
                       await Task.Delay(150, token);
                   }
               }))
        {
            var clock = Stopwatch.StartNew();
            using var client = Open(trickle.Port, TimeSpan.FromSeconds(1));
            That(Wait(() => client.State == SignalingState.Closed, 6_000) && client.CloseReason == SignalingCloseReason.Unreachable && clock.Elapsed < TimeSpan.FromSeconds(4),
                "an answer sent a byte at a time ends at the deadline (" + client.CloseReason + ", " + clock.ElapsedMilliseconds + " ms)");
        }

        // A service that takes the connection but never finishes TLS: the deadline closes the connection under the handshake.
        var mute = new TcpListener(IPAddress.Loopback, 0);
        mute.Start();
        try
        {
            That(ControlPlaneOrigin.TryParse("https://localhost:" + ((IPEndPoint)mute.LocalEndpoint).Port, out var origin), "a service origin with a port parses");
            var transport = new HttpsControlPlaneTransport(origin, _ => Task.FromResult(new[] { IPAddress.Loopback })) { TotalTimeout = TimeSpan.FromSeconds(1) };
            var clock = Stopwatch.StartNew();
            using var client = SignalingUpgrade.Open(transport, ServerId, "tfj1_ticket", TimeSpan.FromSeconds(30));
            That(Wait(() => client.State == SignalingState.Closed, 6_000) && client.CloseReason == SignalingCloseReason.Unreachable && clock.Elapsed < TimeSpan.FromSeconds(4),
                "a service that never finishes TLS is given up on at the deadline (" + client.CloseReason + ", " + clock.ElapsedMilliseconds + " ms)");
        }
        finally
        {
            mute.Stop();
        }

        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using (var nowhere = Open(port, TimeSpan.FromSeconds(2)))
        {
            That(Wait(() => nowhere.State == SignalingState.Closed) && nowhere.CloseReason == SignalingCloseReason.Unreachable,
                "a port nobody listens on is unreachable (" + nowhere.CloseReason + ")");
        }

        // The upgrade carries these headers and no other, the credential only in its own header, and no body.
        using var room = Answering(Upgrade);
        using var shaped = Open(room.Port);
        RecordedHttpRequest? request = null;
        That(Wait(() => shaped.State == SignalingState.Open) && room.Requests.TryDequeue(out request), "the upgrade arrives");
        var names = request!.Headers.Select(header => header.Key).ToArray();
        That(request.RequestLine == "GET " + SignalingUpgrade.Path(ServerId) + " HTTP/1.1" &&
             names.SequenceEqual(new[] { "Host", "User-Agent", "Upgrade", "Connection", "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Authorization" }) &&
             request.Header("Host") == "127.0.0.1:" + room.Port && request.Header("Upgrade") == "websocket" && request.Header("Connection") == "Upgrade" &&
             request.Header("Sec-WebSocket-Version") == "13" && request.Header("Authorization") == "Bearer tfj1_ticket" && request.Body.Length == 0,
            "the upgrade is the room's path with exactly the headers it needs (" + string.Join(", ", names) + ")");
        That(Convert.FromBase64String(request.Header("Sec-WebSocket-Key")!).Length == 16, "and a key of 16 random bytes");

        Throws<ArgumentException>(() => SignalingUpgrade.Open(Transport(room.Port), "ABC", "tfj1_ticket"), "a malformed server id is refused");
        Throws<ArgumentException>(() => SignalingUpgrade.Open(Transport(room.Port), ServerId, "two words"), "and so is a credential that could break a header");
        Throws<ArgumentException>(() => SignalingUpgrade.Open(Transport(room.Port), ServerId, "tfj1/ticket"), "or a path");
        Thread.Sleep(200);
        That(room.Connections == 1, "before anything is contacted (" + room.Connections + " connections)");
    }

    internal static void TheUpgradeKeepsTheClientsCertificateCheck()
    {
        using var certificate = LoopbackHttpServer.SelfSigned("localhost");
        using var server = new LoopbackHttpServer(Upgraded, certificate);
        That(ControlPlaneOrigin.TryParse("https://localhost:" + server.Port, out var origin), "a service origin with a port parses");
        Task<IPAddress[]> Loopback(string _) => Task.FromResult(new[] { IPAddress.Loopback });

        using (var strict = SignalingUpgrade.Open(new HttpsControlPlaneTransport(origin, Loopback), ServerId, "tfj1_ticket"))
        {
            That(Wait(() => strict.State == SignalingState.Closed) && strict.CloseReason == SignalingCloseReason.Unreachable && server.Requests.IsEmpty,
                "a certificate no system trusts is refused before the upgrade is sent (" + strict.CloseReason + ")");
        }

        var pinned = new HttpsControlPlaneTransport(origin, Loopback, (_, presented, _, _) => presented != null && presented.GetCertHashString() == certificate.GetCertHashString());
        using var client = SignalingUpgrade.Open(pinned, ServerId, "tfj1_ticket");
        That(Wait(() => client.TryReceive(out var message) && message.Type == SignalType.Welcome), "over TLS the socket opens and the service's frames arrive");
        That(server.Requests.TryDequeue(out var request) && request.Header("Host") == "localhost:" + server.Port, "and the upgrade names the origin's own name");
    }

    private static WebSocketSignalingClient Open(int port, TimeSpan? timeout = null) => SignalingUpgrade.Open(Transport(port), ServerId, "tfj1_ticket", timeout);

    private static HttpsControlPlaneTransport Transport(int port) => new(ControlPlaneOrigin.Loopback(port));

    /// <summary>The answer a service upgrades with, for the key the request carried.</summary>
    private static string Upgrade(string key) =>
        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + SignalingUpgrade.Accept(key) + "\r\n\r\n";

    /// <summary>A server that writes what <paramref name="answer"/> makes of the request's key, then holds the connection open.</summary>
    private static LoopbackHttpServer Answering(Func<string, string> answer) => new(async (request, stream, token) =>
    {
        var bytes = Encoding.Latin1.GetBytes(answer(request.Header("Sec-WebSocket-Key") ?? string.Empty));
        if (bytes.Length == 0) return;
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
        await Task.Delay(5_000, token);
    });

    /// <summary>Upgrades, then sends the service's welcome and holds the connection open.</summary>
    private static async Task Upgraded(RecordedHttpRequest request, Stream stream, CancellationToken token)
    {
        var bytes = Encoding.ASCII.GetBytes(Upgrade(request.Header("Sec-WebSocket-Key") ?? string.Empty));
        await stream.WriteAsync(bytes, token);
        await stream.WriteAsync(TextFrame(Welcome), token);
        await stream.FlushAsync(token);
        await Task.Delay(5_000, token);
    }

    /// <summary>A server's text frame, unmasked, as RFC 6455 sends one to a client.</summary>
    private static byte[] TextFrame(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        return new byte[] { 0x81, (byte)payload.Length }.Concat(payload).ToArray();
    }

    private static bool Wait(Func<bool> condition, int milliseconds = 5_000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }

        return condition();
    }
}
