using System;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.NatTraversal.Signaling;
using TopiaForge.Multiplayer.Transport.Crypto;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// Opens the signaling socket over a connection the HTTPS client makes (<see cref="HttpsControlPlaneTransport"/>), so
    /// the socket keeps that client's rules on every runtime: the origin's own name and port, no proxy, a certificate that
    /// verifies for the name, the client's deadlines, and no redirect. The upgrade is written and read here, its answer's
    /// head as strictly as any other answer's, and the connection is then handed to <see cref="WebSocket.CreateFromStream"/>.
    /// The runtime's own WebSocket client is never used: under the game's Mono it followed no redirect, while on .NET it
    /// followed one to another address, so the tests and the game opened the socket by different rules.
    /// </summary>
    internal static class SignalingUpgrade
    {
        /// <summary>The GUID RFC 6455 appends to the key before hashing it into the accept value.</summary>
        internal const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        /// <summary>How often the socket proves it is alive while nothing else is sent, as before.</summary>
        internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Opens the signaling room of <paramref name="serverId"/> with a host token or a join ticket, which travels only in
        /// the Authorization header. Returns at once; the socket opens within <paramref name="connectTimeout"/>, 10 s by
        /// default, and within the HTTPS client's whole deadline. Nothing is contacted when an argument is refused.
        /// </summary>
        internal static WebSocketSignalingClient Open(HttpsControlPlaneTransport transport, string serverId, string credential, TimeSpan? connectTimeout = null)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            var path = Path(serverId);
            if (!ControlPlaneRequest.IsCredential(credential)) throw new ArgumentException("A signaling credential is 1 to 256 base64url characters.", nameof(credential));
            return WebSocketSignalingClient.Open(cancellation => UpgradeAsync(transport, path, credential, cancellation), connectTimeout);
        }

        /// <summary>Gets the room's path: <c>/v1/signal/</c> and the server id.</summary>
        internal static string Path(string serverId)
        {
            if (!Network.WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            return "/v1/signal/" + serverId;
        }

        /// <summary>
        /// Connects to the origin and upgrades the connection within <paramref name="cancellation"/> and the HTTPS client's
        /// whole deadline. Nothing throws out of it: a lookup, connection, TLS or deadline failure, or a connection that
        /// ends before the answer does, is <see cref="SignalingCloseReason.Unreachable"/>; any answer but the upgrade, a
        /// redirect included, and an upgrade of the wrong shape, is <see cref="SignalingCloseReason.Refused"/>.
        /// </summary>
        internal static async Task<SignalingConnection> UpgradeAsync(HttpsControlPlaneTransport transport, string path, string credential, CancellationToken cancellation)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            ControlPlaneConnection? connection = null;
            try
            {
                using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    budget.CancelAfter(transport.TotalTimeout);
                    connection = await transport.ConnectAsync(budget.Token).ConfigureAwait(false);
                    var key = NewKey();
                    UpgradeAnswer answer;
                    using (budget.Token.Register(connection.Dispose))
                    {
                        var request = WriteRequest(transport.Origin, path, credential, key);
                        await connection.Stream.WriteAsync(request, 0, request.Length, budget.Token).ConfigureAwait(false);
                        await connection.Stream.FlushAsync(budget.Token).ConfigureAwait(false);
                        answer = await HttpAnswerReader.ReadUpgradeAsync(connection.Stream, budget.Token).ConfigureAwait(false);
                    }

                    // The deadline ends here: once upgraded, the socket lives as long as its session needs it.
                    budget.Token.ThrowIfCancellationRequested();
                    if (!Upgrades(answer, key)) return SignalingConnection.Failed(SignalingCloseReason.Refused);
                    var socket = WebSocket.CreateFromStream(new UpgradedStream(answer.Rest, connection), false, null, KeepAliveInterval);
                    connection = null;
                    return SignalingConnection.Opened(socket);
                }
            }
            catch (ControlPlaneProtocolException)
            {
                // The answer's head broke the reader's rules, so it upgrades nothing.
                return SignalingConnection.Failed(SignalingCloseReason.Refused);
            }
            catch (Exception)
            {
                // Whatever the runtime's exception type for a lookup, connection, TLS or deadline failure; no message is read.
                return SignalingConnection.Failed(SignalingCloseReason.Unreachable);
            }
            finally
            {
                // A connection no socket owns by now is closed: refused, failed or cancelled.
                connection?.Dispose();
            }
        }

        /// <summary>
        /// Writes the upgrade exactly as it goes out: these headers and no other, and no body. The credential is base64url,
        /// so it can never end a header line.
        /// </summary>
        internal static byte[] WriteRequest(ControlPlaneOrigin origin, string path, string credential, string key)
        {
            if (origin == null) throw new ArgumentNullException(nameof(origin));
            var head = new StringBuilder(320);
            head.Append("GET ").Append(path).Append(" HTTP/1.1\r\n");
            head.Append("Host: ").Append(origin.Authority).Append("\r\n");
            head.Append("User-Agent: TopiaForge\r\n");
            head.Append("Upgrade: websocket\r\n");
            head.Append("Connection: Upgrade\r\n");
            head.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n");
            head.Append("Sec-WebSocket-Version: 13\r\n");
            head.Append("Authorization: Bearer ").Append(credential).Append("\r\n\r\n");
            return Encoding.ASCII.GetBytes(head.ToString());
        }

        /// <summary>
        /// Whether an answer upgrades the connection, read as RFC 6455 requires and no looser: HTTP/1.1 with status 101, one
        /// <c>Upgrade: websocket</c>, a <c>Connection</c> that names the upgrade, and one <c>Sec-WebSocket-Accept</c> made
        /// from this request's key. Any other status, a redirect included, never upgrades and is never followed. An
        /// extension or a subprotocol this client never offered, or a body's framing, which a 101 never has, makes the
        /// answer the wrong shape.
        /// </summary>
        internal static bool Upgrades(UpgradeAnswer answer, string key)
        {
            if (answer == null) throw new ArgumentNullException(nameof(answer));
            if (!answer.Http11 || answer.Status != 101) return false;
            var upgrades = 0;
            var accepts = 0;
            var connectionUpgrade = false;
            string? accept = null;
            foreach (var header in answer.Headers)
            {
                if (Is(header.Key, "Upgrade"))
                {
                    upgrades++;
                    if (!string.Equals(header.Value, "websocket", StringComparison.OrdinalIgnoreCase)) return false;
                }
                else if (Is(header.Key, "Connection"))
                {
                    foreach (var token in header.Value.Split(','))
                    {
                        if (string.Equals(token.Trim(' ', '\t'), "upgrade", StringComparison.OrdinalIgnoreCase)) connectionUpgrade = true;
                    }
                }
                else if (Is(header.Key, "Sec-WebSocket-Accept"))
                {
                    accepts++;
                    accept = header.Value;
                }
                else if (Is(header.Key, "Sec-WebSocket-Extensions") || Is(header.Key, "Sec-WebSocket-Protocol") ||
                         Is(header.Key, "Content-Length") || Is(header.Key, "Transfer-Encoding"))
                {
                    return false;
                }
            }

            return upgrades == 1 && connectionUpgrade && accepts == 1 && string.Equals(accept, Accept(key), StringComparison.Ordinal);
        }

        /// <summary>The accept value RFC 6455 derives from a key: base64 of the SHA-1 of the key and the GUID.</summary>
        internal static string Accept(string key)
        {
            // RFC 6455 fixes SHA-1 here; it proves only that the server read this handshake, and secures nothing.
            using (var sha1 = SHA1.Create())
            {
                return Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
            }
        }

        private static string NewKey()
        {
            var nonce = new byte[16];
            CryptoRandom.Fill(nonce);
            return Convert.ToBase64String(nonce);
        }

        private static bool Is(string name, string expected) => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The upgraded connection as the WebSocket reads it: first whatever arrived with the upgrade's answer, then the
    /// connection itself. Disposing it closes the connection.
    /// </summary>
    internal sealed class UpgradedStream : Stream
    {
        private readonly ControlPlaneConnection connection;
        private readonly Stream inner;
        private readonly byte[] early;
        private int taken;

        internal UpgradedStream(byte[] early, ControlPlaneConnection connection)
        {
            this.early = early ?? throw new ArgumentNullException(nameof(early));
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
            inner = connection.Stream;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var copied = TakeEarly(new Span<byte>(buffer, offset, count));
            return copied > 0 ? copied : inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var copied = TakeEarly(new Span<byte>(buffer, offset, count));
            return copied > 0 ? Task.FromResult(copied) : inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var copied = TakeEarly(buffer.Span);
            return copied > 0 ? new ValueTask<int>(copied) : inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) connection.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>Copies what is left of the early bytes into <paramref name="destination"/>; zero once they are gone.</summary>
        private int TakeEarly(Span<byte> destination)
        {
            var count = Math.Min(destination.Length, early.Length - taken);
            if (count <= 0) return 0;
            new ReadOnlySpan<byte>(early, taken, count).CopyTo(destination);
            taken += count;
            return count;
        }
    }
}
