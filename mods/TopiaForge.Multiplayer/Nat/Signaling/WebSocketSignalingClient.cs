using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.NatTraversal.Signaling
{
    /// <summary>
    /// The signaling socket over WebSocket: <c>GET /v1/signal/{serverId}</c> on the control plane's origin, the host token
    /// or join ticket in the Authorization header and never in the URL. It runs its own receive and send loops, so every
    /// call the agent makes returns at once. Frames are canonical JSON text of at most 8 KiB; a binary or oversized frame
    /// from the service closes the socket. The origin must be https, which makes the socket wss; plain ws is accepted
    /// only to a loopback address, for a control plane running on this machine during development. The game's Mono runs
    /// ClientWebSocket over TLS 1.2 (MultiplayerMonoSpike.md, section 2); nothing outside this class depends on that.
    /// Failures become a <see cref="SignalingCloseReason"/>, never an exception or a message that could name anything.
    /// </summary>
    internal sealed class WebSocketSignalingClient : ISignalingClient
    {
        internal const int MaximumQueued = 256;
        internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly ConcurrentQueue<SignalMessage> inbound = new ConcurrentQueue<SignalMessage>();
        private readonly ConcurrentQueue<byte[]> outbound = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private int state = (int)SignalingState.Connecting;
        private int closeReason;
        private int inboundCount;
        private int outboundCount;
        private int closing;

        private WebSocketSignalingClient()
        {
        }

        public SignalingState State => (SignalingState)Volatile.Read(ref state);

        public SignalingCloseReason CloseReason => (SignalingCloseReason)Volatile.Read(ref closeReason);

        /// <summary>
        /// Opens the session's signaling room. <paramref name="credential"/> is the host session token or the join ticket,
        /// sent only in the Authorization header.
        /// </summary>
        internal static WebSocketSignalingClient Open(Uri origin, string serverId, string credential, TimeSpan? connectTimeout = null)
        {
            var endpoint = Endpoint(origin, serverId);
            if (!IsCredential(credential)) throw new ArgumentException("A signaling credential is a token or ticket of printable ASCII.", nameof(credential));
            var client = new WebSocketSignalingClient();
            // The room is the control plane's own origin, reached directly: never through a proxy the system names.
            client.socket.Options.Proxy = null;
            client.socket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
            client.socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            var timeout = connectTimeout ?? ConnectTimeout;
            Task.Run(() => client.RunAsync(endpoint, timeout));
            return client;
        }

        /// <summary>Builds the room's URL: wss for an https origin, ws only for a loopback http origin.</summary>
        internal static Uri Endpoint(Uri origin, string serverId)
        {
            if (origin == null) throw new ArgumentNullException(nameof(origin));
            if (!Network.WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            var secure = origin.Scheme == Uri.UriSchemeHttps;
            var local = origin.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(origin.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
            if (!secure && !local) throw new ArgumentException("The control plane is reached over https, or plain http on this machine only.", nameof(origin));
            if (!string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment) || origin.AbsolutePath != "/") throw new ArgumentException("An origin has no path, query or fragment.", nameof(origin));
            var builder = new UriBuilder(origin) { Scheme = secure ? "wss" : "ws", Path = "/v1/signal/" + serverId };
            return builder.Uri;
        }

        public bool TrySend(SignalMessage message)
        {
            if (message == null || State == SignalingState.Closed || Volatile.Read(ref closing) != 0) return false;
            byte[] frame;
            try
            {
                frame = SignalCodec.Encode(message);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (Interlocked.Increment(ref outboundCount) > MaximumQueued)
            {
                Interlocked.Decrement(ref outboundCount);
                return false;
            }

            outbound.Enqueue(frame);
            wake.Release();
            return true;
        }

        public bool TryReceive(out SignalMessage message)
        {
            if (!inbound.TryDequeue(out message!)) return false;
            Interlocked.Decrement(ref inboundCount);
            return true;
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closing, 1) != 0) return;
            wake.Release();
        }

        public void Dispose()
        {
            Finish(SignalingCloseReason.Normal);
            try
            {
                lifetime.Cancel();
                socket.Abort();
                socket.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }
        }

        private async Task RunAsync(Uri endpoint, TimeSpan timeout)
        {
            try
            {
                using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    connecting.CancelAfter(timeout);
                    await socket.ConnectAsync(endpoint, connecting.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                Finish(IsNetworkFailure(exception) ? SignalingCloseReason.Unreachable : SignalingCloseReason.Refused);
                Release();
                return;
            }

            Interlocked.CompareExchange(ref state, (int)SignalingState.Open, (int)SignalingState.Connecting);
            var sending = SendLoopAsync();
            await ReceiveLoopAsync().ConfigureAwait(false);
            lifetime.Cancel();
            try
            {
                await sending.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The send loop ends with the socket.
            }

            Release();
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[SignalCodec.MaximumFrameBytes + 1];
            try
            {
                while (true)
                {
                    var length = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        if (length >= buffer.Length)
                        {
                            await CloseAsync(WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                            Finish(SignalingCloseReason.Invalid);
                            return;
                        }

                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), lifetime.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Finish(Reason(socket.CloseStatus));
                            return;
                        }

                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            await CloseAsync(WebSocketCloseStatus.InvalidMessageType).ConfigureAwait(false);
                            Finish(SignalingCloseReason.Invalid);
                            return;
                        }

                        length += result.Count;
                    }
                    while (!result.EndOfMessage);

                    if (length > SignalCodec.MaximumFrameBytes)
                    {
                        await CloseAsync(WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                        Finish(SignalingCloseReason.Invalid);
                        return;
                    }

                    var frame = new byte[length];
                    Buffer.BlockCopy(buffer, 0, frame, 0, length);
                    if (!SignalCodec.TryDecode(frame, false, out var message)) continue;
                    if (Interlocked.Increment(ref inboundCount) > MaximumQueued)
                    {
                        Interlocked.Decrement(ref inboundCount);
                        continue;
                    }

                    inbound.Enqueue(message);
                }
            }
            catch (Exception exception)
            {
                Finish(exception is OperationCanceledException ? SignalingCloseReason.Normal : SignalingCloseReason.Unreachable);
            }
        }

        private async Task SendLoopAsync()
        {
            while (!lifetime.IsCancellationRequested)
            {
                await wake.WaitAsync(lifetime.Token).ConfigureAwait(false);
                while (outbound.TryDequeue(out var frame))
                {
                    Interlocked.Decrement(ref outboundCount);
                    await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Text, true, lifetime.Token).ConfigureAwait(false);
                }

                if (Volatile.Read(ref closing) == 0) continue;

                // Everything queued has gone: close, and give the service a moment to answer before letting go.
                await CloseAsync(WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                Finish(SignalingCloseReason.Normal);
                lifetime.CancelAfter(CloseTimeout);
                return;
            }
        }

        private async Task CloseAsync(WebSocketCloseStatus status)
        {
            try
            {
                using (var budget = new CancellationTokenSource(CloseTimeout))
                {
                    await socket.CloseOutputAsync(status, string.Empty, budget.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // A socket that cannot close politely is aborted below.
            }
        }

        private void Finish(SignalingCloseReason reason)
        {
            if (Interlocked.Exchange(ref state, (int)SignalingState.Closed) == (int)SignalingState.Closed) return;
            Volatile.Write(ref closeReason, (int)reason);
        }

        private void Release()
        {
            try
            {
                socket.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already released.
            }
        }

        private static SignalingCloseReason Reason(WebSocketCloseStatus? status)
        {
            switch ((int?)status)
            {
                case 1000:
                    return SignalingCloseReason.Normal;
                case 4409:
                    return SignalingCloseReason.Replaced;
                case 4410:
                    return SignalingCloseReason.SessionEnded;
                case 4411:
                    return SignalingCloseReason.Expired;
                case 4408:
                    return SignalingCloseReason.RateLimited;
                case 4403:
                    return SignalingCloseReason.Revoked;
                case 4400:
                case 1003:
                case 1009:
                    return SignalingCloseReason.Invalid;
                default:
                    return SignalingCloseReason.Unreachable;
            }
        }

        private static bool IsNetworkFailure(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is SocketException || current is IOException || current is OperationCanceledException) return true;
            }

            return false;
        }

        private static bool IsCredential(string? credential)
        {
            if (string.IsNullOrEmpty(credential) || credential!.Length > 256) return false;
            foreach (var character in credential)
            {
                if (character < 0x21 || character > 0x7E) return false;
            }

            return true;
        }
    }
}
