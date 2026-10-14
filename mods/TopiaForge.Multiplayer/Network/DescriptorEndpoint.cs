using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// The host's bounded TCP endpoint, on the same port number as its game socket, that serves its signed descriptor to
    /// LAN and direct browsers and, in developer mode only, its package archives. One request per connection:
    /// <c>TFD1 descriptor\n</c> or <c>TFD1 package &lt;sha256&gt;\n</c>, answered by <c>TFD1 200 &lt;length&gt;\n</c> and the
    /// bytes, or by <c>TFD1 400</c>, <c>404</c>, <c>429</c> or <c>503</c> and a newline. It runs on the network thread
    /// without blocking: at most eight connections, a request of at most 128 bytes within five seconds, ten requests a
    /// second per address, and responses written 64 KiB per turn. A relay-only host serves LAN addresses only.
    /// </summary>
    internal sealed class DescriptorEndpoint : INetworkService
    {
        internal const int MaximumConnections = 8;
        internal const int MaximumRequestBytes = DescriptorRequest.MaximumBytes;
        internal const int RequestsPerSecond = 10;
        private const int ChunkBytes = 64 * 1024;
        private const int ChunksPerTurn = 4;
        private const int MaximumAcceptsPerTurn = 16;
        private const int TrackedSources = 256;

        private readonly Socket listener;
        private readonly List<Client> clients = new List<Client>();
        private readonly Dictionary<string, SourceBudget> budgets = new Dictionary<string, SourceBudget>(StringComparer.Ordinal);
        private readonly Func<string, Stream?>? packages;
        private readonly bool relayOnly;
        private readonly long requestTimeoutMicros;
        private readonly long transferTimeoutMicros;
        private readonly byte[] chunk = new byte[ChunkBytes];
        private byte[]? descriptor;

        /// <param name="port">The game port; zero takes a free one, as tests do.</param>
        /// <param name="packages">Opens a package archive by SHA-256 in developer mode; null serves none.</param>
        /// <param name="relayOnly">Serves only LAN addresses, for a host that hides its own.</param>
        /// <param name="bindAddress">The address to listen on, every local address by default; tests use loopback.</param>
        internal DescriptorEndpoint(
            int port,
            Func<string, Stream?>? packages = null,
            bool relayOnly = false,
            long requestTimeoutMicros = 5_000_000,
            long transferTimeoutMicros = 120_000_000,
            IPAddress? bindAddress = null)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            this.packages = packages;
            this.relayOnly = relayOnly;
            this.requestTimeoutMicros = requestTimeoutMicros;
            this.transferTimeoutMicros = transferTimeoutMicros;
            listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                listener.DualMode = true;
                listener.Blocking = false;
                if (port != 0 && Environment.OSVersion.Platform == PlatformID.Win32NT) listener.ExclusiveAddressUse = true;
                listener.Bind(new IPEndPoint(UdpSocketDriver.AsDualMode(bindAddress ?? IPAddress.IPv6Any), port));
                listener.Listen(16);
                LocalPort = ((IPEndPoint)listener.LocalEndPoint!).Port;
            }
            catch
            {
                listener.Dispose();
                throw;
            }
        }

        internal int LocalPort { get; }

        /// <summary>Gets how many connections are open, for tests.</summary>
        internal int ConnectionCount => clients.Count;

        /// <summary>Sets the descriptor to serve, or clears it; safe to call from any thread.</summary>
        internal void SetDescriptor(SignedServerDescriptor? signed) => Volatile.Write(ref descriptor, signed?.Bytes);

        public void Turn(NetworkStack stack, long now)
        {
            Accept(now);
            for (var index = clients.Count - 1; index >= 0; index--)
            {
                var client = clients[index];
                if (!Serve(client, now))
                {
                    client.Dispose();
                    clients.RemoveAt(index);
                }
            }
        }

        public bool TryClaim(TransportInfoReply reply, long now) => false;

        /// <summary>
        /// The endpoint sets no timer of its own: the thread turns at least every 2 ms, which bounds how long a TCP
        /// client waits, and nothing here may make the thread spin.
        /// </summary>
        public long NextTimeoutMicros() => long.MaxValue;

        public void Dispose()
        {
            foreach (var client in clients) client.Dispose();
            clients.Clear();
            listener.Dispose();
        }

        private void Accept(long now)
        {
            for (var count = 0; count < MaximumAcceptsPerTurn; count++)
            {
                Socket socket;
                try
                {
                    socket = listener.Accept();
                }
                catch (SocketException)
                {
                    return;
                }

                socket.Blocking = false;
                socket.NoDelay = true;
                var remote = (IPEndPoint)socket.RemoteEndPoint!;
                if (relayOnly && UdpPaths.Classify(remote.Address) != TransportPathKind.Lan)
                {
                    socket.Dispose();
                    continue;
                }

                var client = new Client(socket, SourceKey(remote.Address), now);
                if (clients.Count >= MaximumConnections)
                {
                    client.Respond("TFD1 503\n", null);
                    Flush(client);
                    client.Dispose();
                    continue;
                }

                clients.Add(client);
            }
        }

        /// <summary>Advances one connection; returns false once it is finished.</summary>
        private bool Serve(Client client, long now)
        {
            if (!client.Responding)
            {
                if (now - client.OpenedAt > requestTimeoutMicros) return false;
                if (!client.TryReadRequest(out var request, out var closed)) return !closed;
                Answer(client, request, now);
            }
            else if (now - client.OpenedAt > transferTimeoutMicros)
            {
                return false;
            }

            return Flush(client);
        }

        private void Answer(Client client, DescriptorRequest request, long now)
        {
            if (request.Kind == DescriptorRequestKind.Overlong)
            {
                client.Respond("TFD1 400\n", null);
                return;
            }

            if (!Admit(client.Source, now))
            {
                client.Respond("TFD1 429\n", null);
                return;
            }

            if (request.Kind == DescriptorRequestKind.Descriptor)
            {
                var bytes = Volatile.Read(ref descriptor);
                if (bytes == null) client.Respond("TFD1 404\n", null);
                else client.Respond("TFD1 200 " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n", new MemoryStream(bytes, false));
                return;
            }

            if (request.Kind == DescriptorRequestKind.Package)
            {
                Stream? stream = null;
                long length = 0;
                try
                {
                    stream = packages?.Invoke(request.Sha256!);
                    if (stream != null) length = stream.Length;
                }
                catch (Exception exception) when (IsArchiveFailure(exception))
                {
                    // An archive that cannot be opened is as good as missing, for this request alone.
                    stream?.Dispose();
                    stream = null;
                }

                if (stream == null) client.Respond("TFD1 404\n", null);
                else client.Respond("TFD1 200 " + length.ToString(CultureInfo.InvariantCulture) + "\n", stream);
                return;
            }

            client.Respond("TFD1 400\n", null);
        }

        /// <summary>Writes what the socket takes now; returns false once the response is complete or the peer is gone.</summary>
        private bool Flush(Client client)
        {
            try
            {
                // A turn writes at most a few chunks per connection, so one fast reader never delays the datagrams.
                for (var chunks = 0; chunks < ChunksPerTurn; chunks++)
                {
                    if (client.Pending.Count == 0 && !client.FillFromBody(chunk)) break;
                    var segment = client.Pending;
                    var sent = client.Socket.Send(segment.Array!, segment.Offset, segment.Count, SocketFlags.None);
                    client.Pending = new ArraySegment<byte>(segment.Array!, segment.Offset + sent, segment.Count - sent);
                    if (sent < segment.Count) return true;
                }
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
            {
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (Exception exception) when (IsArchiveFailure(exception))
            {
                // An archive that fails mid-read ends this one transfer: its reader sees a short body and gives up.
                return false;
            }

            return !client.Responding || !client.Complete;
        }

        /// <summary>Whether reading a package archive failed in a way that belongs to one request, not to the thread.</summary>
        private static bool IsArchiveFailure(Exception exception) =>
            exception is IOException || exception is UnauthorizedAccessException || exception is NotSupportedException || exception is ObjectDisposedException;

        private bool Admit(string source, long now)
        {
            if (!budgets.TryGetValue(source, out var budget))
            {
                if (budgets.Count >= TrackedSources) ForgetIdleSources(now);
                budget = new SourceBudget(now);
                budgets[source] = budget;
            }

            if (now - budget.WindowStart >= 1_000_000)
            {
                budget.WindowStart = now;
                budget.Count = 0;
            }

            return ++budget.Count <= RequestsPerSecond;
        }

        private void ForgetIdleSources(long now)
        {
            foreach (var key in new List<string>(budgets.Keys))
            {
                if (now - budgets[key].WindowStart >= 1_000_000) budgets.Remove(key);
            }

            if (budgets.Count >= TrackedSources) budgets.Clear();
        }

        /// <summary>Keys a source by its address alone; the key lives only in this table, in memory.</summary>
        private static string SourceKey(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return WireText.ToHex(address.GetAddressBytes());
        }

        private sealed class SourceBudget
        {
            internal SourceBudget(long now)
            {
                WindowStart = now;
            }

            internal long WindowStart { get; set; }
            internal int Count { get; set; }
        }

        private sealed class Client : IDisposable
        {
            private readonly byte[] request = new byte[MaximumRequestBytes];
            private int requestLength;
            private Stream? body;

            internal Client(Socket socket, string source, long openedAt)
            {
                Socket = socket;
                Source = source;
                OpenedAt = openedAt;
            }

            internal Socket Socket { get; }
            internal string Source { get; }
            internal long OpenedAt { get; }
            internal bool Responding { get; private set; }
            internal ArraySegment<byte> Pending { get; set; } = new ArraySegment<byte>(Array.Empty<byte>());

            /// <summary>Gets whether the header and the whole body have been handed to the socket.</summary>
            internal bool Complete => Pending.Count == 0 && body == null;

            /// <summary>Reads what has arrived; yields the request once its newline is in or its bytes run out.</summary>
            internal bool TryReadRequest(out DescriptorRequest parsed, out bool closed)
            {
                parsed = default;
                closed = false;
                try
                {
                    var read = Socket.Receive(request, requestLength, request.Length - requestLength, SocketFlags.None);
                    if (read == 0)
                    {
                        closed = true;
                        return false;
                    }

                    requestLength += read;
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
                {
                    return false;
                }
                catch (SocketException)
                {
                    closed = true;
                    return false;
                }

                // A line that matches no request, control characters and all, is answered 400 by the caller.
                parsed = DescriptorRequest.Read(request, requestLength);
                return parsed.Kind != DescriptorRequestKind.Incomplete;
            }

            internal void Respond(string header, Stream? content)
            {
                Responding = true;
                Pending = new ArraySegment<byte>(Encoding.ASCII.GetBytes(header));
                body = content;
            }

            /// <summary>Moves the next part of the body into <see cref="Pending"/>; false once the body is done.</summary>
            internal bool FillFromBody(byte[] buffer)
            {
                if (body == null) return false;
                var read = body.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    body.Dispose();
                    body = null;
                    ShutdownSend();
                    return false;
                }

                var copy = new byte[read];
                Buffer.BlockCopy(buffer, 0, copy, 0, read);
                Pending = new ArraySegment<byte>(copy);
                return true;
            }

            /// <summary>Tells the reader the body is complete; a peer that already left needs no telling.</summary>
            private void ShutdownSend()
            {
                if (Socket.Connected) Socket.Shutdown(SocketShutdown.Send);
            }

            public void Dispose()
            {
                body?.Dispose();
                body = null;
                Socket.Dispose();
            }
        }
    }
}
