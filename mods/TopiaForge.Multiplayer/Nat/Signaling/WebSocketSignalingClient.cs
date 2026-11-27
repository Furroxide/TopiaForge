using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.NatTraversal.Signaling
{
    /// <summary>The outcome of opening a signaling socket: the socket, upgraded and ready, or why there is none.</summary>
    internal sealed class SignalingConnection
    {
        private SignalingConnection(WebSocket? socket, SignalingCloseReason failure)
        {
            Socket = socket;
            Failure = failure;
        }

        /// <summary>Gets the upgraded socket, or null when opening failed.</summary>
        internal WebSocket? Socket { get; }

        /// <summary>Gets why opening failed: <see cref="SignalingCloseReason.Unreachable"/> or <see cref="SignalingCloseReason.Refused"/>.</summary>
        internal SignalingCloseReason Failure { get; }

        internal static SignalingConnection Opened(WebSocket socket) =>
            new SignalingConnection(socket ?? throw new ArgumentNullException(nameof(socket)), SignalingCloseReason.None);

        internal static SignalingConnection Failed(SignalingCloseReason reason)
        {
            if (reason != SignalingCloseReason.Unreachable && reason != SignalingCloseReason.Refused)
            {
                throw new ArgumentOutOfRangeException(nameof(reason), "Opening fails as unreachable or refused.");
            }

            return new SignalingConnection(null, reason);
        }
    }

    /// <summary>
    /// The signaling socket over WebSocket: <c>GET /v1/signal/{serverId}</c> on the control plane's origin, the host token
    /// or join ticket in the Authorization header and never in the URL. Whoever opens it makes the connection and the
    /// upgrade: in the game, the HTTPS client's own connection (<c>SignalingUpgrade</c>), so no runtime's WebSocket client,
    /// with its own redirect and proxy rules, is involved. The socket runs its own receive and send loops, so every call
    /// the agent makes returns at once. Frames are canonical JSON text of at most 8 KiB; a binary or oversized frame from
    /// the service closes the socket. Failures become a <see cref="SignalingCloseReason"/>, never an exception or a message
    /// that could name anything.
    /// </summary>
    internal sealed class WebSocketSignalingClient : ISignalingClient
    {
        internal const int MaximumQueued = 256;
        internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

        private readonly ConcurrentQueue<SignalMessage> inbound = new ConcurrentQueue<SignalMessage>();
        private readonly ConcurrentQueue<byte[]> outbound = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim wake = new SemaphoreSlim(0);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private WebSocket? socket;
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
        /// Starts opening a signaling socket through <paramref name="connect"/>, which makes the connection and the upgrade
        /// within the token it is given: <paramref name="connectTimeout"/>, 10 s by default, or until the socket is
        /// disposed. Returns at once.
        /// </summary>
        internal static WebSocketSignalingClient Open(Func<CancellationToken, Task<SignalingConnection>> connect, TimeSpan? connectTimeout = null)
        {
            if (connect == null) throw new ArgumentNullException(nameof(connect));
            var client = new WebSocketSignalingClient();
            var timeout = connectTimeout ?? ConnectTimeout;
            Task.Run(() => client.RunAsync(connect, timeout));
            return client;
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
                // Cancelling first ends an open still under way; a socket that lands meanwhile is released by the open.
                lifetime.Cancel();
                var current = Volatile.Read(ref socket);
                current?.Abort();
                current?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }
        }

        private async Task RunAsync(Func<CancellationToken, Task<SignalingConnection>> connect, TimeSpan timeout)
        {
            SignalingConnection connection;
            try
            {
                using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    connecting.CancelAfter(timeout);
                    connection = await connect(connecting.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // An opener should answer rather than throw; one that throws reads as a connection that failed.
                Finish(SignalingCloseReason.Unreachable);
                return;
            }

            var opened = connection.Socket;
            if (opened == null)
            {
                Finish(connection.Failure);
                return;
            }

            Interlocked.Exchange(ref socket, opened);
            if (lifetime.IsCancellationRequested)
            {
                // Disposed while the upgrade landed: the socket it made is let go at once.
                Release(opened);
                return;
            }

            Interlocked.CompareExchange(ref state, (int)SignalingState.Open, (int)SignalingState.Connecting);
            var sending = SendLoopAsync(opened);
            await ReceiveLoopAsync(opened).ConfigureAwait(false);
            lifetime.Cancel();
            try
            {
                await sending.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The send loop ends with the socket.
            }

            Release(opened);
        }

        private async Task ReceiveLoopAsync(WebSocket open)
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
                            await CloseAsync(open, WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
                            Finish(SignalingCloseReason.Invalid);
                            return;
                        }

                        result = await open.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), lifetime.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Finish(Reason(open.CloseStatus));
                            return;
                        }

                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            await CloseAsync(open, WebSocketCloseStatus.InvalidMessageType).ConfigureAwait(false);
                            Finish(SignalingCloseReason.Invalid);
                            return;
                        }

                        length += result.Count;
                    }
                    while (!result.EndOfMessage);

                    if (length > SignalCodec.MaximumFrameBytes)
                    {
                        await CloseAsync(open, WebSocketCloseStatus.MessageTooBig).ConfigureAwait(false);
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

        private async Task SendLoopAsync(WebSocket open)
        {
            while (!lifetime.IsCancellationRequested)
            {
                await wake.WaitAsync(lifetime.Token).ConfigureAwait(false);
                while (outbound.TryDequeue(out var frame))
                {
                    Interlocked.Decrement(ref outboundCount);
                    await open.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Text, true, lifetime.Token).ConfigureAwait(false);
                }

                if (Volatile.Read(ref closing) == 0) continue;

                // Everything queued has gone: close, and give the service a moment to answer before letting go.
                await CloseAsync(open, WebSocketCloseStatus.NormalClosure).ConfigureAwait(false);
                Finish(SignalingCloseReason.Normal);
                lifetime.CancelAfter(CloseTimeout);
                return;
            }
        }

        private static async Task CloseAsync(WebSocket open, WebSocketCloseStatus status)
        {
            try
            {
                using (var budget = new CancellationTokenSource(CloseTimeout))
                {
                    await open.CloseOutputAsync(status, string.Empty, budget.Token).ConfigureAwait(false);
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

        private static void Release(WebSocket open)
        {
            try
            {
                open.Dispose();
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
    }
}
