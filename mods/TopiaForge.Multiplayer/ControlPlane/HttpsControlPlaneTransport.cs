using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// The game's HTTPS client for the TopiaForge service, deliberately small. It opens one connection per request to the
    /// origin's own name and port and to nothing else: there is no proxy, since it never asks the system for one, no
    /// redirect, since an answer in the 300s is a failure, no cookie and no compression. TLS must verify the certificate
    /// for the origin's name. Every step shares one deadline, so a service that answers slowly, a byte at a time, cannot
    /// hold a request past it, and the answer is read within the request's bound (<see cref="HttpAnswerReader"/>). Nothing
    /// throws out of <see cref="SendAsync"/>: a failure is a <see cref="ControlPlaneTransportFailure"/>, which never names
    /// an address, and no exception's text is ever read. The signaling socket is opened over a connection made here too
    /// (<see cref="ConnectAsync"/>, <see cref="SignalingUpgrade"/>), so it keeps every one of these rules.
    /// </summary>
    internal sealed class HttpsControlPlaneTransport : IControlPlaneTransport
    {
        internal static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan DefaultTotalTimeout = TimeSpan.FromSeconds(20);
        private const int AddressesPerFamily = 4;

        private readonly ControlPlaneOrigin origin;
        private readonly Func<string, Task<IPAddress[]>> resolve;
        private readonly RemoteCertificateValidationCallback validate;

        /// <param name="resolve">Looks the origin's name up; null uses the system resolver. Tests watch what it is asked.</param>
        /// <param name="validate">Checks the service's certificate; null accepts only one that verifies with no error at all.</param>
        internal HttpsControlPlaneTransport(ControlPlaneOrigin origin, Func<string, Task<IPAddress[]>>? resolve = null, RemoteCertificateValidationCallback? validate = null)
        {
            this.origin = origin ?? throw new ArgumentNullException(nameof(origin));
            this.resolve = resolve ?? Dns.GetHostAddressesAsync;
            this.validate = validate ?? Verified;
        }

        /// <summary>Gets the origin every connection goes to.</summary>
        internal ControlPlaneOrigin Origin => origin;

        /// <summary>Gets or sets how long each connection attempt may take, within the request's whole deadline.</summary>
        internal TimeSpan ConnectTimeout { get; set; } = DefaultConnectTimeout;

        /// <summary>Gets or sets how long a whole request may take: lookup, connection, TLS, writing and reading the answer.</summary>
        internal TimeSpan TotalTimeout { get; set; } = DefaultTotalTimeout;

        public async Task<ControlPlaneResponse> SendAsync(ControlPlaneRequest request, CancellationToken cancellation)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (cancellation.IsCancellationRequested) return ControlPlaneResponse.Failed(ControlPlaneTransportFailure.Cancelled);
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                budget.CancelAfter(TotalTimeout);
                try
                {
                    return await ExchangeAsync(request, budget.Token).ConfigureAwait(false);
                }
                catch (ControlPlaneProtocolException exception)
                {
                    return ControlPlaneResponse.Failed(exception.Failure);
                }
                catch (Exception exception)
                {
                    // Any runtime's socket or TLS failure lands here, whatever its type: none may escape, and no message is read.
                    if (cancellation.IsCancellationRequested) return ControlPlaneResponse.Failed(ControlPlaneTransportFailure.Cancelled);
                    if (budget.IsCancellationRequested) return ControlPlaneResponse.Failed(ControlPlaneTransportFailure.TimedOut);
                    return ControlPlaneResponse.Failed(IsTls(exception) ? ControlPlaneTransportFailure.Tls : ControlPlaneTransportFailure.Unreachable);
                }
            }
        }

        /// <summary>Writes the request exactly as it goes out: no header but these, and a body only with POST and PUT.</summary>
        internal static byte[] Write(ControlPlaneOrigin origin, ControlPlaneRequest request)
        {
            var head = new StringBuilder(256);
            head.Append(request.MethodText).Append(' ').Append(request.Path).Append(" HTTP/1.1\r\n");
            head.Append("Host: ").Append(origin.Authority).Append("\r\n");
            head.Append("User-Agent: TopiaForge\r\n");
            head.Append("Accept: application/json\r\n");
            if (request.Bearer != null) head.Append("Authorization: Bearer ").Append(request.Bearer).Append("\r\n");
            if (request.Body != null)
            {
                head.Append("Content-Type: application/json\r\n");
                head.Append("Content-Length: ").Append(request.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }

            head.Append("Connection: close\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            if (request.Body == null) return bytes;
            var whole = new byte[bytes.Length + request.Body.Length];
            Buffer.BlockCopy(bytes, 0, whole, 0, bytes.Length);
            Buffer.BlockCopy(request.Body, 0, whole, bytes.Length, request.Body.Length);
            return whole;
        }

        /// <summary>
        /// Opens one connection to the origin, as every request does: its addresses in turn, each attempt within
        /// <see cref="ConnectTimeout"/>, then TLS that verifies the certificate for the origin's name, all within
        /// <paramref name="cancellation"/>, which closes the connection if it fires first. The caller owns the connection.
        /// A failure throws whatever the runtime's sockets and TLS throw; callers read none of its text.
        /// </summary>
        internal async Task<ControlPlaneConnection> ConnectAsync(CancellationToken cancellation)
        {
            var addresses = await ResolveAsync(cancellation).ConfigureAwait(false);
            TcpClient? client = null;
            foreach (var address in addresses)
            {
                client = await ConnectToAsync(address, cancellation).ConfigureAwait(false);
                if (client != null) break;
            }

            if (client == null) throw new SocketException((int)SocketError.HostUnreachable);
            Stream stream = client.GetStream();
            try
            {
                using (cancellation.Register(client.Dispose))
                {
                    if (origin.IsSecure)
                    {
                        var tls = new SslStream(stream, false, validate);
                        stream = tls;
                        await tls.AuthenticateAsClientAsync(origin.Host).ConfigureAwait(false);
                    }
                }

                cancellation.ThrowIfCancellationRequested();
                return new ControlPlaneConnection(client, stream);
            }
            catch (Exception)
            {
                stream.Dispose();
                client.Dispose();
                throw;
            }
        }

        private async Task<ControlPlaneResponse> ExchangeAsync(ControlPlaneRequest request, CancellationToken cancellation)
        {
            using (var connection = await ConnectAsync(cancellation).ConfigureAwait(false))
            using (cancellation.Register(connection.Dispose))
            {
                var bytes = Write(origin, request);
                await connection.Stream.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false);
                await connection.Stream.FlushAsync(cancellation).ConfigureAwait(false);
                return await HttpAnswerReader.ReadAsync(connection.Stream, request.MaximumResponseBytes, cancellation).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Finds the origin's addresses: a loopback literal is its own, and a name is looked up within the deadline. The
        /// order is IPv4 first, then IPv6, at most four of each, and an address no service could answer from is skipped.
        /// </summary>
        private async Task<IReadOnlyList<IPAddress>> ResolveAsync(CancellationToken cancellation)
        {
            if (IPAddress.TryParse(origin.Host, out var literal)) return new[] { literal };
            var lookup = resolve(origin.Host) ?? throw new SocketException((int)SocketError.HostNotFound);
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                var finished = await Task.WhenAny(lookup, Task.Delay(Timeout.Infinite, waiting.Token)).ConfigureAwait(false);
                waiting.Cancel();
                if (finished != lookup)
                {
                    // The lookup cannot be cancelled; it is left to finish, and whatever it throws is observed and never read.
                    _ = lookup.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    cancellation.ThrowIfCancellationRequested();
                }
            }

            var found = await lookup.ConfigureAwait(false) ?? Array.Empty<IPAddress>();
            var ordered = new List<IPAddress>();
            foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
            {
                var taken = 0;
                foreach (var address in found)
                {
                    if (address == null || address.AddressFamily != family || !IsConnectable(address) || taken == AddressesPerFamily) continue;
                    ordered.Add(address);
                    taken++;
                }
            }

            if (ordered.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
            return ordered;
        }

        /// <summary>Connects to one address within its own time limit; null when it refused or did not answer, so the next is tried.</summary>
        private async Task<TcpClient?> ConnectToAsync(IPAddress address, CancellationToken cancellation)
        {
            var client = new TcpClient(address.AddressFamily);
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                attempt.CancelAfter(ConnectTimeout);
                using (attempt.Token.Register(() => client.Dispose()))
                {
                    try
                    {
                        await client.ConnectAsync(address, origin.Port).ConfigureAwait(false);
                        if (attempt.IsCancellationRequested) throw new OperationCanceledException(attempt.Token);
                        return client;
                    }
                    catch (Exception)
                    {
                        // This address refused or stayed silent; the next one is tried while the deadline allows.
                        client.Dispose();
                        cancellation.ThrowIfCancellationRequested();
                        return null;
                    }
                }
            }
        }

        private static bool IsConnectable(IPAddress address)
        {
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast) || address.Equals(IPAddress.None)) return false;
            if (address.AddressFamily == AddressFamily.InterNetworkV6) return !address.IsIPv6Multicast;
            var first = address.GetAddressBytes()[0];
            return first != 0 && (first < 224);
        }

        /// <summary>The certificate must verify for the origin's name with no error at all; a mod's looser global callback is never consulted.</summary>
        private static bool Verified(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
            System.Security.Cryptography.X509Certificates.X509Chain? chain, SslPolicyErrors errors) => errors == SslPolicyErrors.None;

        private static bool IsTls(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is AuthenticationException) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// One connection the HTTPS client opened to the service: the socket, and the stream over it, TLS unless the origin is
    /// a loopback control plane. Disposing it closes both, from any thread and any number of times.
    /// </summary>
    internal sealed class ControlPlaneConnection : IDisposable
    {
        private readonly TcpClient client;

        internal ControlPlaneConnection(TcpClient client, Stream stream)
        {
            this.client = client ?? throw new ArgumentNullException(nameof(client));
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>Gets the stream requests are written to and answers read from.</summary>
        internal Stream Stream { get; }

        public void Dispose()
        {
            try
            {
                Stream.Dispose();
            }
            catch (Exception)
            {
                // A stream torn down mid-read may throw as it closes; this can run on a deadline's timer, so nothing escapes.
            }

            client.Dispose();
        }
    }
}
