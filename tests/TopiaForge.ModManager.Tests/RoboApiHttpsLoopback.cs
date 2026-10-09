using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class RoboApiClientTests
    {
        private sealed record HttpsReply(int Status, string Body, string? Location = null);
        private sealed record HttpsRequest(string Target, string Authorization, string Session, byte[] Body);

        /// <summary>A bounded real TLS/HTTP server. Only a fresh test key is used; no trust store is changed.</summary>
        private sealed class HttpsLoopback : IDisposable
        {
            private readonly RSA key = RSA.Create(2048);
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
            private readonly ConcurrentQueue<HttpsReply> replies = new ConcurrentQueue<HttpsReply>();
            private readonly ConcurrentQueue<HttpsRequest> requests = new ConcurrentQueue<HttpsRequest>();
            private readonly Task serving;
            private int connections;
            private int completed;
            internal string? FailureType { get; private set; }

            internal HttpsLoopback(params HttpsReply[] responses)
            {
                var request = new CertificateRequest(
                    "CN=TopiaForge synthetic loopback", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var names = new SubjectAlternativeNameBuilder();
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
                var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
                if (OperatingSystem.IsWindows())
                {
                    // Schannel cannot use an ephemeral private key. Import only this synthetic key into a
                    // temporary user key container, deleted when Certificate is disposed. Never add a trust
                    // entry or persist/export a file. https://github.com/dotnet/runtime/issues/23749
                    using (generated)
                    {
                        var encoded = generated.Export(X509ContentType.Pkcs12);
                        try { Certificate = X509CertificateLoader.LoadPkcs12(encoded, null, X509KeyStorageFlags.UserKeySet); }
                        finally { CryptographicOperations.ZeroMemory(encoded); }
                    }
                }
                else Certificate = generated;
                foreach (var response in responses) replies.Enqueue(response);
                try
                {
                    listener.Start();
                    Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    serving = ServeAsync();
                }
                catch
                {
                    listener.Stop();
                    Certificate.Dispose();
                    key.Dispose();
                    lifetime.Dispose();
                    throw;
                }
            }

            internal int Port { get; }
            internal string Root => "https://127.0.0.1:" + Port + "/v1";
            internal X509Certificate2 Certificate { get; }
            internal int Connections => Volatile.Read(ref connections);
            internal HttpsRequest[] Requests => requests.ToArray();

            internal async Task WaitForCompletedAsync(int count)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (Volatile.Read(ref completed) < count)
                    await Task.Delay(10, deadline.Token).ConfigureAwait(false);
            }

            private async Task ServeAsync()
            {
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        using var connection = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref connections);
                        try { await ServeConnectionAsync(connection).ConfigureAwait(false); }
                        finally { Interlocked.Increment(ref completed); }
                    }
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (SocketException) when (lifetime.IsCancellationRequested) { }
            }

            private async Task ServeConnectionAsync(TcpClient connection)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                using var tls = new SslStream(connection.GetStream());
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = Certificate,
                        EnabledSslProtocols = SslProtocols.Tls12,
                        ClientCertificateRequired = false,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    }, deadline.Token).ConfigureAwait(false);
                    var request = await ReadRequestAsync(tls, deadline.Token).ConfigureAwait(false);
                    if (request == null) return;
                    requests.Enqueue(request);
                    var reply = replies.TryDequeue(out var queued) ? queued : new HttpsReply(500, "unexpected request");
                    var body = Encoding.UTF8.GetBytes(reply.Body);
                    var header = "HTTP/1.1 " + reply.Status + " Fixture\r\nContent-Type: application/json\r\n" +
                                 "Content-Length: " + body.Length + "\r\nConnection: close\r\n" +
                                 (reply.Location == null ? string.Empty : "Location: " + reply.Location + "\r\n") + "\r\n";
                    await tls.WriteAsync(Encoding.ASCII.GetBytes(header), deadline.Token).ConfigureAwait(false);
                    await tls.WriteAsync(body, deadline.Token).ConfigureAwait(false);
                    await tls.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                // Default-client certificate refusal ends TLS before HTTP. No error text or request data is logged.
                catch (AuthenticationException ex) { FailureType = ex.GetType().Name + ":" + ex.HResult + "/" + ex.InnerException?.GetType().Name + ":" + ex.InnerException?.HResult; }
                catch (IOException ex) { FailureType = ex.GetType().Name + ":" + ex.HResult + "/" + ex.InnerException?.GetType().Name + ":" + ex.InnerException?.HResult; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }

            private static async Task<HttpsRequest?> ReadRequestAsync(Stream stream, CancellationToken token)
            {
                using var bytes = new MemoryStream();
                var one = new byte[1];
                var matched = 0;
                var delimiter = new byte[] { 13, 10, 13, 10 };
                while (matched < delimiter.Length)
                {
                    if (await stream.ReadAsync(one, token).ConfigureAwait(false) == 0) return null;
                    bytes.WriteByte(one[0]);
                    if (bytes.Length > 16 * 1024) throw new InvalidDataException("Fixture headers exceed their bound.");
                    matched = one[0] == delimiter[matched] ? matched + 1 : one[0] == 13 ? 1 : 0;
                }
                var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.None);
                var first = lines[0].Split(' ');
                if (first.Length != 3 || first[0] != "POST") throw new InvalidDataException("Fixture expects POST.");
                var length = 0;
                var authorization = string.Empty;
                var session = string.Empty;
                foreach (var line in lines)
                {
                    var colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    var name = line.Substring(0, colon);
                    var value = line.Substring(colon + 1).Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) length = int.Parse(value);
                    if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) authorization = value;
                    if (name.Equals("Session-Id", StringComparison.OrdinalIgnoreCase)) session = value;
                }
                if (length < 0 || length > 512 * 1024) throw new InvalidDataException("Fixture body exceeds its bound.");
                var body = new byte[length];
                await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
                return new HttpsRequest(first[1], authorization, session, body);
            }

            public void Dispose()
            {
                lifetime.Cancel();
                listener.Stop();
                try { serving.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
                finally { lifetime.Dispose(); Certificate.Dispose(); key.Dispose(); }
            }
        }

        private static HttpClient PinnedLoopbackTransport(params HttpsLoopback[] servers)
        {
            // Exercise the production handler's redirect/decompression policy. Trust is scoped to this
            // instance and only the exact in-memory certificate at each explicitly declared loopback port.
            var handler = RoboApiClient.CreateHttpHandler();
            handler.UseProxy = false;
            handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            {
                if (certificate == null || request.RequestUri == null ||
                    request.RequestUri.Host != "127.0.0.1" ||
                    (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
                foreach (var server in servers)
                {
                    if (request.RequestUri.Port == server.Port &&
                        CryptographicOperations.FixedTimeEquals(certificate.RawData, server.Certificate.RawData)) return true;
                }
                return false;
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }
    }
}
