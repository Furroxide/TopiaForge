extern alias provider;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using provider::TopiaForge.Multiplayer.ControlPlane;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>A transport that records what the client sends and answers from a script, without any socket.</summary>
internal sealed class RecordingTransport : IControlPlaneTransport
{
    private readonly Func<ControlPlaneRequest, ControlPlaneResponse> answer;

    internal RecordingTransport(Func<ControlPlaneRequest, ControlPlaneResponse> answer)
    {
        this.answer = answer;
    }

    internal List<ControlPlaneRequest> Requests { get; } = new();

    public Task<ControlPlaneResponse> SendAsync(ControlPlaneRequest request, CancellationToken cancellation)
    {
        Requests.Add(request);
        return Task.FromResult(answer(request));
    }
}

/// <summary>The shared control-plane fixtures, read as the service recorded them.</summary>
internal static class ControlPlaneFixtures
{
    internal static JsonElement Exchanges => NetworkFixtures.Load(Path.Combine("control-plane", "exchanges.json"));

    internal static JsonElement Exchange(string id) => Exchanges.GetProperty("exchanges").EnumerateArray().Single(item => item.Text("id") == id);

    internal static JsonElement Request(string id) => Exchange(id).GetProperty("request");

    internal static JsonElement Response(string id) => Exchange(id).GetProperty("response");

    /// <summary>The recorded answer as the transport hands it over: status, body, whether it is JSON, and its Retry-After.</summary>
    internal static ControlPlaneResponse Answer(string id)
    {
        var response = Response(id);
        var headers = response.GetProperty("headers");
        var body = response.OptionalText("body");
        int? retryAfter = headers.TryGetProperty("retry-after", out var retry) ? int.Parse(retry.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null;
        var json = headers.TryGetProperty("content-type", out var type) && HttpAnswerReader.IsJson(type.GetString());
        return ControlPlaneResponse.Answered(response.GetProperty("status").GetInt32(), body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body), json, retryAfter);
    }

    /// <summary>The recorded request's bearer credential, or null.</summary>
    internal static string? Bearer(string id)
    {
        var headers = Request(id).GetProperty("headers");
        return headers.TryGetProperty("authorization", out var value) ? value.GetString()!.Substring("Bearer ".Length) : null;
    }

    internal static byte[]? Body(string id)
    {
        var body = Request(id).OptionalText("body");
        return body == null ? null : Encoding.UTF8.GetBytes(body);
    }
}

/// <summary>A request as the loopback server read it off the wire.</summary>
internal sealed class RecordedHttpRequest
{
    internal string RequestLine { get; init; } = string.Empty;

    internal List<KeyValuePair<string, string>> Headers { get; } = new();

    internal byte[] Body { get; set; } = Array.Empty<byte>();

    internal string? Header(string name)
    {
        var values = Headers.Where(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Select(header => header.Value).ToList();
        return values.Count == 0 ? null : string.Join(",", values);
    }

    internal int Count(string name) => Headers.Count(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A plain HTTP/1.1 server on the loopback address only, one handler call per connection, for the client's wire tests.
/// It reads a request head and its Content-Length body, then lets the handler write whatever bytes it likes, as fast or
/// as slowly as it likes, and closes. With a certificate it speaks TLS first.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Func<RecordedHttpRequest, Stream, CancellationToken, Task> handle;
    private readonly X509Certificate2? certificate;
    private readonly CancellationTokenSource stopping = new();
    private readonly Thread thread;
    private int connections;

    internal LoopbackHttpServer(Func<RecordedHttpRequest, Stream, CancellationToken, Task> handle, X509Certificate2? certificate = null)
    {
        this.handle = handle;
        this.certificate = certificate;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        thread = new Thread(Run) { IsBackground = true, Name = "TopiaForge test control plane" };
        thread.Start();
    }

    internal int Port { get; }

    /// <summary>Gets how many connections the server accepted.</summary>
    internal int Connections => Volatile.Read(ref connections);

    internal ConcurrentQueue<RecordedHttpRequest> Requests { get; } = new();

    /// <summary>A server that writes <paramref name="answer"/> whole and closes.</summary>
    internal static LoopbackHttpServer Answering(string answer) => Answering(Encoding.ASCII.GetBytes(answer));

    internal static LoopbackHttpServer Answering(byte[] answer) =>
        new((_, stream, token) => stream.WriteAsync(answer, 0, answer.Length, token));

    internal static string Answer(int status, string? body, string contentType = "application/json; charset=utf-8", string extraHeaders = "")
    {
        var bytes = body == null ? 0 : Encoding.UTF8.GetByteCount(body);
        return "HTTP/1.1 " + status + " X\r\n" + (body == null ? string.Empty : "Content-Type: " + contentType + "\r\nContent-Length: " + bytes + "\r\n") +
            extraHeaders + "Connection: close\r\n\r\n" + (body ?? string.Empty);
    }

    public void Dispose()
    {
        stopping.Cancel();
        listener.Stop();
        thread.Join(2000);
    }

    private void Run()
    {
        while (!stopping.IsCancellationRequested)
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

            Interlocked.Increment(ref connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                if (certificate != null)
                {
                    var tls = new SslStream(stream, false);
                    await tls.AuthenticateAsServerAsync(certificate, false, false).ConfigureAwait(false);
                    stream = tls;
                }

                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request == null) return;
                Requests.Enqueue(request);
                await handle(request, stream, stopping.Token).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException || exception is SocketException || exception is ObjectDisposedException ||
                                              exception is OperationCanceledException || exception is System.Security.Authentication.AuthenticationException)
            {
                // The client went away or refused the certificate; the test reads what it got.
            }
        }
    }

    private static async Task<RecordedHttpRequest?> ReadRequestAsync(Stream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 65_536)
        {
            if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) return null;
            head.Add(one[0]);
            if (head.Count >= 4 && head[^4] == 13 && head[^3] == 10 && head[^2] == 13 && head[^1] == 10) break;
        }

        var lines = Encoding.ASCII.GetString(head.ToArray(), 0, head.Count - 4).Split("\r\n");
        var request = new RecordedHttpRequest { RequestLine = lines[0] };
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            request.Headers.Add(new KeyValuePair<string, string>(line.Substring(0, colon), line.Substring(colon + 1).Trim()));
        }

        var length = request.Header("Content-Length");
        if (length != null)
        {
            var body = new byte[int.Parse(length, System.Globalization.CultureInfo.InvariantCulture)];
            var read = 0;
            while (read < body.Length)
            {
                var count = await stream.ReadAsync(body, read, body.Length - read).ConfigureAwait(false);
                if (count == 0) break;
                read += count;
            }

            request.Body = body;
        }

        return request;
    }

    /// <summary>A self-signed certificate for <paramref name="name"/>, which no system trusts.</summary>
    internal static X509Certificate2 SelfSigned(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}
