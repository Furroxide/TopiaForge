extern alias provider;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using provider::TopiaForge.Multiplayer.ControlPlane;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The client's HTTPS transport against hostile services on the loopback address: answers past their bound, malformed,
/// slow, silent, redirecting or behind a certificate that does not verify. Each fails with its own reason, fast and
/// without throwing, and nothing but the configured origin is ever contacted, a proxy included.
/// </summary>
internal static class ControlPlaneHttpTests
{
    private static readonly ControlPlaneRequest Status = new(ControlPlaneMethod.Get, "/v1/status", null, null, 4096);

    internal static void OversizedAnswersStopAtTheirBound()
    {
        // A Content-Length past the bound fails before any body is read, even when the body never comes.
        using (var server = new LoopbackHttpServer(async (_, stream, token) =>
               {
                   var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n\r\n");
                   await stream.WriteAsync(head, token);
                   await Task.Delay(Timeout.Infinite, token);
               }))
        {
            var clock = Stopwatch.StartNew();
            That(Send(server.Port).Failure == ControlPlaneTransportFailure.TooLarge && clock.Elapsed < TimeSpan.FromSeconds(5), "a declared length past the bound is refused at once");
        }

        That(Answer("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n" + Chunks(5000)).Failure == ControlPlaneTransportFailure.TooLarge,
            "a chunked body past the bound is refused");
        That(Answer("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n" + new string('x', 5000)).Failure == ControlPlaneTransportFailure.TooLarge,
            "a body read to the connection's end is held to the bound too");
        That(Answer("HTTP/1.1 200 OK\r\n" + string.Concat(Enumerable.Repeat("X-Pad: " + new string('p', 100) + "\r\n", 200)) + "\r\n").Failure == ControlPlaneTransportFailure.Malformed,
            "a header block past 16 KiB is refused");
        That(Answer("HTTP/1.1 200 OK\r\n" + string.Concat(Enumerable.Range(0, 65).Select(index => "X-" + index + ": 1\r\n")) + "\r\n").Failure == ControlPlaneTransportFailure.Malformed,
            "more than 64 headers are refused");
        var exact = Answer("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 4096\r\n\r\n" + new string('y', 4096));
        That(exact.Succeeded && exact.Body.Length == 4096 && exact.IsJson, "a body exactly at the bound is read whole");
    }

    internal static void MalformedAnswersAreRefused()
    {
        var malformed = new (string About, string Raw)[]
        {
            ("bare line feeds", "HTTP/1.1 200 OK\nContent-Length: 2\n\n{}"),
            ("no status line", "Content-Length: 2\r\n\r\n{}"),
            ("another protocol", "HTTP/2.0 200 OK\r\nContent-Length: 2\r\n\r\n{}"),
            ("a four-digit status", "HTTP/1.1 2000 OK\r\nContent-Length: 2\r\n\r\n{}"),
            ("a status past 599", "HTTP/1.1 600 OK\r\nContent-Length: 2\r\n\r\n{}"),
            ("a header without a colon", "HTTP/1.1 200 OK\r\nContent-Length 2\r\n\r\n{}"),
            ("a space in a header name", "HTTP/1.1 200 OK\r\nContent Length: 2\r\n\r\n{}"),
            ("a folded header", "HTTP/1.1 200 OK\r\nX-A: 1\r\n continued\r\nContent-Length: 2\r\n\r\n{}"),
            ("a control character in a value", "HTTP/1.1 200 OK\r\nX-A: \u0001\r\nContent-Length: 2\r\n\r\n{}"),
            ("both framings", "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{}\r\n0\r\n\r\n"),
            ("a transfer coding it does not know", "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip\r\n\r\n{}"),
            ("two lengths that disagree", "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Length: 3\r\n\r\n{}"),
            ("a length that is not a number", "HTTP/1.1 200 OK\r\nContent-Length: +2\r\n\r\n{}"),
            ("a body cut short", "HTTP/1.1 200 OK\r\nContent-Length: 50\r\n\r\n{\"a\":1}"),
            ("a chunk size that is not hexadecimal", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n{}\r\n0\r\n\r\n"),
            ("a chunk without its line end", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\n{}xx0\r\n\r\n"),
            ("a protocol switch", "HTTP/1.1 101 Switching\r\nUpgrade: websocket\r\n\r\n"),
            ("a body on a 204", "HTTP/1.1 204 No Content\r\nContent-Length: 2\r\n\r\n{}"),
            ("nothing at all", string.Empty)
        };

        foreach (var (about, raw) in malformed)
        {
            var answer = Answer(raw);
            That(answer.Failure == ControlPlaneTransportFailure.Malformed, "an answer with " + about + " is malformed (" + answer + ")");
        }

        var interim = Answer("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}");
        That(interim.Succeeded && interim.Status == 200, "an interim answer is skipped");
        var chunked = Answer("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: Chunked\r\n\r\n1;ext=1\r\n{\r\n1\r\n}\r\n0\r\nX-Trailer: 1\r\n\r\n");
        That(chunked.Succeeded && Encoding.ASCII.GetString(chunked.Body) == "{}", "chunks join, with extensions and trailers dropped");
        That(!Answer("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\n{}").IsJson, "a body labelled anything but JSON says so");
        That(Answer("HTTP/1.1 503 Busy\r\nRetry-After: 30\r\nContent-Length: 0\r\n\r\n").RetryAfterSeconds == 30, "a plain Retry-After is read");
        foreach (var value in new[] { "Wed, 21 Oct 2026 07:28:00 GMT", "-1", "90000", "1.5", "" })
        {
            That(Answer("HTTP/1.1 503 Busy\r\nRetry-After: " + value + "\r\nContent-Length: 0\r\n\r\n").RetryAfterSeconds == null, "Retry-After '" + value + "' says nothing");
        }

        That(Answer("HTTP/1.1 503 Busy\r\nRetry-After: 5\r\nRetry-After: 5\r\nContent-Length: 0\r\n\r\n").RetryAfterSeconds == null, "a repeated Retry-After says nothing");
    }

    internal static void RedirectsAreNeverFollowed()
    {
        using var elsewhere = LoopbackHttpServer.Answering(LoopbackHttpServer.Answer(200, "{}"));
        foreach (var status in new[] { 301, 302, 303, 307, 308 })
        {
            using var server = LoopbackHttpServer.Answering("HTTP/1.1 " + status + " Moved\r\nLocation: http://127.0.0.1:" + elsewhere.Port + "/v1/status\r\nContent-Length: 0\r\n\r\n");
            That(Send(server.Port).Failure == ControlPlaneTransportFailure.Redirected, status + " is a failure, never followed");
        }

        That(elsewhere.Connections == 0, "and the place a redirect names is never contacted");
    }

    internal static void SlowAndSilentServicesMeetTheDeadline()
    {
        using (var trickle = new LoopbackHttpServer(async (_, stream, token) =>
               {
                   foreach (var character in "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}")
                   {
                       await stream.WriteAsync(new[] { (byte)character }, token);
                       await stream.FlushAsync(token);
                       await Task.Delay(150, token);
                   }
               }))
        {
            var clock = Stopwatch.StartNew();
            var answer = Send(trickle.Port, TimeSpan.FromSeconds(1));
            That(answer.Failure == ControlPlaneTransportFailure.TimedOut && clock.Elapsed < TimeSpan.FromSeconds(4),
                "an answer sent a byte at a time ends at the deadline (" + answer + ", " + clock.ElapsedMilliseconds + " ms)");
        }

        using (var silent = new LoopbackHttpServer((_, _, token) => Task.Delay(Timeout.Infinite, token)))
        {
            var clock = Stopwatch.StartNew();
            That(Send(silent.Port, TimeSpan.FromSeconds(1)).Failure == ControlPlaneTransportFailure.TimedOut && clock.Elapsed < TimeSpan.FromSeconds(4), "a silent service times out");
        }

        That(Send(ClosedPort()).Failure == ControlPlaneTransportFailure.Unreachable, "a port nobody listens on is unreachable");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        That(new HttpsControlPlaneTransport(ControlPlaneOrigin.Loopback(ClosedPort())).SendAsync(Status, cancelled.Token).GetAwaiter().GetResult().Failure ==
             ControlPlaneTransportFailure.Cancelled, "a request given up before it starts contacts nothing");

        // A name that does not resolve, or resolves to nothing usable, is unreachable; the lookup is the origin's own name.
        That(ControlPlaneOrigin.TryParse("https://control-plane.example.test", out var named), "a service origin parses");
        var asked = new List<string>();
        var none = new HttpsControlPlaneTransport(named, host =>
        {
            asked.Add(host);
            return Task.FromResult(new[] { IPAddress.Any, IPAddress.Broadcast, IPAddress.Parse("224.0.0.1") });
        });
        That(none.SendAsync(Status, CancellationToken.None).GetAwaiter().GetResult().Failure == ControlPlaneTransportFailure.Unreachable && asked.SequenceEqual(new[] { "control-plane.example.test" }),
            "addresses no service answers from are never contacted");
        var failing = new HttpsControlPlaneTransport(named, _ => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));
        That(failing.SendAsync(Status, CancellationToken.None).GetAwaiter().GetResult().Failure == ControlPlaneTransportFailure.Unreachable, "a name that does not resolve is unreachable");
        var hanging = new HttpsControlPlaneTransport(named, _ => new TaskCompletionSource<IPAddress[]>().Task) { TotalTimeout = TimeSpan.FromMilliseconds(300) };
        That(hanging.SendAsync(Status, CancellationToken.None).GetAwaiter().GetResult().Failure == ControlPlaneTransportFailure.TimedOut, "a lookup that never answers meets the deadline");
    }

    internal static void TlsVerifiesTheOriginsName()
    {
        using var certificate = LoopbackHttpServer.SelfSigned("localhost");
        using var server = new LoopbackHttpServer((_, stream, token) => stream.WriteAsync(Encoding.ASCII.GetBytes(LoopbackHttpServer.Answer(200, "{}")), token).AsTask(), certificate);
        That(ControlPlaneOrigin.TryParse("https://localhost:" + server.Port, out var origin), "a service origin with a port parses");
        Task<IPAddress[]> Loopback(string _) => Task.FromResult(new[] { IPAddress.Loopback });

        var strict = new HttpsControlPlaneTransport(origin, Loopback).SendAsync(Status, CancellationToken.None).GetAwaiter().GetResult();
        That(strict.Failure == ControlPlaneTransportFailure.Tls, "a certificate no system trusts is refused (" + strict + ")");
        That(server.Requests.IsEmpty, "and no request reaches the service behind it");

        // The same server, its own certificate pinned: TLS works, and the request arrives inside it.
        var pinned = new HttpsControlPlaneTransport(origin, Loopback, (_, presented, _, _) => presented != null && presented.GetCertHashString() == certificate.GetCertHashString());
        var answer = pinned.SendAsync(Status, CancellationToken.None).GetAwaiter().GetResult();
        That(answer.Succeeded && answer.Status == 200 && server.Requests.TryDequeue(out var request) && request.Header("Host") == "localhost:" + server.Port,
            "over TLS the request goes to the origin's own name (" + answer + ")");

        // A plain-http server where TLS is expected never hears a request.
        using var plain = LoopbackHttpServer.Answering(LoopbackHttpServer.Answer(200, "{}"));
        That(ControlPlaneOrigin.TryParse("https://localhost:" + plain.Port, out var plainOrigin), "the origin parses");
        var downgrade = new HttpsControlPlaneTransport(plainOrigin, Loopback).SendAsync(Status, CancellationToken.None).GetAwaiter().GetResult();
        That(!downgrade.Succeeded && plain.Requests.IsEmpty, "a service that does not speak TLS gets nothing (" + downgrade + ")");
    }

    internal static void NoProxyIsEverAsked()
    {
        using var proxy = LoopbackHttpServer.Answering(LoopbackHttpServer.Answer(200, "{}"));
        using var origin = LoopbackHttpServer.Answering(LoopbackHttpServer.Answer(200, "{}"));
        var names = new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var savedDefault = System.Net.Http.HttpClient.DefaultProxy;
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, "http://127.0.0.1:" + proxy.Port);
            System.Net.Http.HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:" + proxy.Port);
            That(Send(origin.Port).Succeeded && origin.Connections == 1 && proxy.Connections == 0, "requests go straight to the origin whatever proxy the system names");
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
            System.Net.Http.HttpClient.DefaultProxy = savedDefault;
        }

        // By construction: the client's code never names a type that could pick a proxy, follow a redirect or keep a cookie.
        var folder = Path.Combine(NetworkFixtures.RepositoryRoot(), "mods", "TopiaForge.Multiplayer", "ControlPlane");
        var forbidden = new[] { "HttpClient", "HttpWebRequest", "WebRequest", "WebProxy", "IWebProxy", "ServicePointManager", "CookieContainer", "WebClient" };
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var name in forbidden)
            {
                That(!System.Text.RegularExpressions.Regex.IsMatch(text, "\\b" + name + "\\b"), Path.GetFileName(file) + " never names " + name);
            }
        }
    }

    /// <summary>Seeded hostile answers through the reader: each is read or refused as malformed or too large, and nothing else escapes.</summary>
    internal static void HostileAnswersNeverThrow()
    {
        var random = new Random(7_311);
        var seeds = new[]
        {
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 13\r\n\r\n{\"players\":2}",
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n5\r\n{\"a\":\r\n2\r\n1}\r\n0\r\n\r\n",
            "HTTP/1.1 429 Too Many\r\nRetry-After: 5\r\nContent-Type: application/json\r\nContent-Length: 61\r\n\r\n{\"error\":{\"code\":\"rate-limited\",\"message\":\"Too many requests.\"}}"
        };
        var outcomes = new Dictionary<string, int>();
        var inputs = 4_000 * HarnessOptions.FuzzScale;
        using var run = FuzzLedger.Start("control-plane answers");
        run.Inputs = inputs;
        for (var index = 0; index < inputs; index++)
        {
            var bytes = Encoding.ASCII.GetBytes(seeds[index % seeds.Length]).ToList();
            var mutations = 1 + random.Next(6);
            for (var step = 0; step < mutations && bytes.Count > 0; step++)
            {
                var at = random.Next(bytes.Count);
                switch (random.Next(4))
                {
                    case 0:
                        bytes[at] = (byte)random.Next(256);
                        break;
                    case 1:
                        bytes.RemoveAt(at);
                        break;
                    case 2:
                        bytes.Insert(at, (byte)"\r\n:; 0aZ"[random.Next(8)]);
                        break;
                    default:
                        bytes.RemoveRange(at, bytes.Count - at);
                        break;
                }
            }

            string outcome;
            try
            {
                var answer = HttpAnswerReader.ReadAsync(new MemoryStream(bytes.ToArray()), 64, CancellationToken.None).GetAwaiter().GetResult();
                That(answer.Body.Length <= 64 && answer.Status >= 200 && answer.Status <= 599, "what is read stays within its bound and the statuses");
                outcome = "read";
            }
            catch (ControlPlaneProtocolException exception)
            {
                That(exception.Failure == ControlPlaneTransportFailure.Malformed || exception.Failure == ControlPlaneTransportFailure.TooLarge ||
                     exception.Failure == ControlPlaneTransportFailure.Redirected, "a refusal names its reason");
                outcome = exception.Failure.ToString();
            }

            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }

        That(outcomes.GetValueOrDefault("read") > inputs / 20 && outcomes.GetValueOrDefault("Malformed") > inputs / 4,
            "the inputs reach both outcomes (" + string.Join(", ", outcomes.Select(pair => pair.Key + " " + pair.Value)) + ")");
    }

    private static ControlPlaneResponse Answer(string raw)
    {
        using var server = LoopbackHttpServer.Answering(Encoding.Latin1.GetBytes(raw));
        return Send(server.Port);
    }

    private static ControlPlaneResponse Send(int port, TimeSpan? total = null)
    {
        var transport = new HttpsControlPlaneTransport(ControlPlaneOrigin.Loopback(port)) { TotalTimeout = total ?? TimeSpan.FromSeconds(10) };
        return transport.SendAsync(Status, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string Chunks(int total)
    {
        var builder = new StringBuilder();
        for (var sent = 0; sent < total; sent += 100) builder.Append("64\r\n").Append(new string('c', 100)).Append("\r\n");
        return builder.Append("0\r\n\r\n").ToString();
    }

    private static int ClosedPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
}
