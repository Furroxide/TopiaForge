using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Transport.Crypto;
using TopiaForge.Multiplayer.Transport.Packets;
using TopiaForge.Multiplayer.Transport.Recovery;
using TopiaForge.Multiplayer.Transport.Streams;

namespace TopiaForge.Multiplayer.Transport
{
    internal enum TransportConnectionState
    {
        /// <summary>The handshake is under way; nothing protected flows yet.</summary>
        Handshaking = 0,

        /// <summary>Both directions are keyed and frames flow.</summary>
        Established = 1,

        /// <summary>The connection is ending: the session stream drains before CLOSE goes out.</summary>
        Closing = 2,

        /// <summary>CLOSE was sent or received. The connection waits out stray packets and sends nothing new.</summary>
        Draining = 3,

        /// <summary>The connection is gone. Frames already received can still be read.</summary>
        Closed = 4
    }

    internal enum TransportSendResult
    {
        Queued = 0,

        /// <summary>The connection is not open for this channel.</summary>
        NotOpen = 1,

        /// <summary>The frame is larger than its channel carries.</summary>
        TooLarge = 2,

        /// <summary>The unacknowledged backlog would pass its cap, so the connection closed.</summary>
        Overloaded = 3
    }

    /// <summary>Why a connection ended, as this side saw it.</summary>
    internal enum TransportCloseReason
    {
        None = 0,

        /// <summary>This side closed it and the session stream was delivered.</summary>
        Local = 1,

        /// <summary>The peer sent CLOSE.</summary>
        Peer = 2,

        /// <summary>No authenticated packet arrived within the idle timeout.</summary>
        IdleTimeout = 3,

        /// <summary>The handshake failed, timed out, or met an unexpected key.</summary>
        HandshakeFailed = 4,

        /// <summary>The peer broke the protocol or its flow control.</summary>
        ProtocolViolation = 5,

        /// <summary>The send backlog or the endpoint's capacity ran out.</summary>
        Overloaded = 6
    }

    /// <summary>
    /// One secure connection: the handshake, protected packets, recovery, the session stream and the datagram channels.
    /// Its endpoint drives it; it owns no socket, thread or clock, and every call happens on the endpoint's thread.
    /// </summary>
    internal sealed partial class TransportConnection : IFrameSink
    {
        private const int MaximumInboundDatagrams = 128;

        private readonly TransportEndpoint endpoint;
        private readonly TransportOptions options;
        private readonly AckTracker acks = new AckTracker();
        private readonly LossDetector loss = new LossDetector();
        private readonly RttEstimator rtt;
        private readonly Pacer pacer;
        private readonly SessionSendStream sendStream = new SessionSendStream(0);
        private readonly SessionReceiveStream receiveStream;
        private readonly DatagramChannel realtime = new DatagramChannel((byte)EngineChannel.Realtime);
        private readonly DatagramChannel voice = new DatagramChannel((byte)EngineChannel.Voice);
        private readonly Queue<KeyValuePair<EngineChannel, byte[]>> inboundDatagrams = new Queue<KeyValuePair<EngineChannel, byte[]>>();
        private readonly ReplayWindow replay = new ReplayWindow();
        private readonly List<PacketRange> ackRanges = new List<PacketRange>(AckTracker.MaximumRanges);
        private readonly Action<SentPacket> onAcknowledged;
        private readonly Action<SentPacket> onLost;
        private CipherState? sendKey;
        private CipherState? receiveKey;
        private byte[]? remoteStaticKey;
        private byte[]? handshakeHash;
        private long now;
        private long lastReceivedAt;
        private long lastSentAt;

        internal TransportConnection(TransportEndpoint endpoint, TransportPath path, bool initiator, ulong localConnectionId, long number, long now)
        {
            this.endpoint = endpoint;
            options = endpoint.Options;
            Path = path;
            IsInitiator = initiator;
            LocalConnectionId = localConnectionId;
            Number = number;
            this.now = now;
            rtt = new RttEstimator(options.InitialRttMicros);
            pacer = new Pacer(options.SustainedBytesPerSecond, options.BurstBytesPerSecond, options.BurstMicros, now);
            receiveStream = new SessionReceiveStream(options.StreamWindowBytes, options.MaximumFrameBytes);
            onAcknowledged = OnPacketAcknowledged;
            onLost = OnPacketLost;
        }

        /// <summary>Gets the endpoint-local number diagnostics use for this connection. It is not a network identity.</summary>
        internal long Number { get; }

        internal bool IsInitiator { get; }

        internal TransportPath Path { get; }

        internal ulong LocalConnectionId { get; }

        internal ulong RemoteConnectionId { get; private set; }

        internal TransportConnectionState State { get; private set; }

        /// <summary>Gets whether the peer has been silent past the stall time; reliable frames wait meanwhile.</summary>
        internal bool IsStalled { get; private set; }

        internal TransportCloseReason CloseReason { get; private set; }

        /// <summary>Gets the code the peer's CLOSE carried, when <see cref="CloseReason"/> is <see cref="TransportCloseReason.Peer"/>.</summary>
        internal CloseCode PeerCloseCode { get; private set; }

        /// <summary>Gets the peer's authenticated static key, known once the handshake completes.</summary>
        internal byte[] RemoteStaticKey =>
            remoteStaticKey != null ? (byte[])remoteStaticKey.Clone() : throw new InvalidOperationException("The handshake has not completed.");

        /// <summary>Gets the Noise handshake hash, a channel binding for proofs such as a server password.</summary>
        internal byte[] HandshakeHash =>
            handshakeHash != null ? (byte[])handshakeHash.Clone() : throw new InvalidOperationException("The handshake has not completed.");

        internal long SmoothedRttMicros => rtt.SmoothedRtt;

        internal long PacketsSent { get; private set; }

        internal long PacketsReceived { get; private set; }

        internal long PacketsLost { get; private set; }

        internal long BytesSent { get; private set; }

        /// <summary>Gets the session bytes waiting for the reader, which count toward the endpoint's reassembly cap.</summary>
        internal long BufferedBytes => receiveStream.BufferedBytes;

        /// <summary>Gets or sets whether the endpoint still routes to this connection and counts its buffers.</summary>
        internal bool IsRegistered { get; set; }

        /// <summary>Gets or sets whether the endpoint counts this connection as a half-open handshake.</summary>
        internal bool CountsAsHalfOpen { get; set; }

        /// <summary>
        /// Queues one frame. Session frames are reliable and ordered and may be queued during the handshake; Realtime
        /// and Voice frames are sequenced datagrams that must fit one packet.
        /// </summary>
        internal TransportSendResult Send(EngineChannel channel, byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (channel == EngineChannel.Session)
            {
                if (State != TransportConnectionState.Handshaking && State != TransportConnectionState.Established) return TransportSendResult.NotOpen;
                if (count > options.MaximumFrameBytes) return TransportSendResult.TooLarge;
                if (sendStream.Backlog + count + TransportOptions.FramePrefixBytes > options.SendBacklogBytes)
                {
                    CloseWithError(CloseCode.Overloaded, TransportCloseReason.Overloaded, "The session backlog passed its cap.");
                    return TransportSendResult.Overloaded;
                }

                sendStream.AppendFrame(data, offset, count);
                return TransportSendResult.Queued;
            }

            var target = ChannelFor((byte)channel) ?? throw new ArgumentOutOfRangeException(nameof(channel));
            if (State != TransportConnectionState.Established) return TransportSendResult.NotOpen;
            if (count > ProtectedPacket.MaximumDatagramPayload) return TransportSendResult.TooLarge;
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            target.Enqueue(copy);
            return TransportSendResult.Queued;
        }

        /// <summary>Dequeues the next received frame: datagrams first, then session frames in order.</summary>
        internal bool TryReceive(out EngineChannel channel, out byte[] frame)
        {
            if (inboundDatagrams.Count > 0)
            {
                var next = inboundDatagrams.Dequeue();
                channel = next.Key;
                frame = next.Value;
                return true;
            }

            channel = EngineChannel.Session;
            var before = receiveStream.BufferedBytes;
            if (!receiveStream.TryDequeue(out frame)) return false;
            endpoint.OnReassemblyChanged(this, receiveStream.BufferedBytes - before);
            return true;
        }

        /// <summary>
        /// Closes the connection once the session frames already sent are acknowledged. A handshake in progress is
        /// abandoned at once.
        /// </summary>
        internal void Close()
        {
            if (State == TransportConnectionState.Handshaking)
            {
                Finish(TransportCloseReason.Local);
                return;
            }

            if (State != TransportConnectionState.Established) return;
            State = TransportConnectionState.Closing;
            gracefulClose = true;
            realtime.ClearQueued();
            voice.ClearQueued();
        }

        /// <summary>Ends the connection at once with no CLOSE, as when its endpoint shuts down.</summary>
        internal void Abort() => Finish(TransportCloseReason.Local);

        private DatagramChannel? ChannelFor(byte channel) =>
            channel == (byte)EngineChannel.Realtime ? realtime : channel == (byte)EngineChannel.Voice ? voice : null;

        /// <summary>Ends the connection for good. Diagnostics hear of it once, by number and fixed text.</summary>
        private void Finish(TransportCloseReason reason)
        {
            if (State == TransportConnectionState.Closed) return;
            State = TransportConnectionState.Closed;
            if (CloseReason == TransportCloseReason.None) CloseReason = reason;
            DisposeHandshake();
            loss.Clear();
            realtime.ClearQueued();
            voice.ClearQueued();
            if (CloseReason != TransportCloseReason.HandshakeFailed)
            {
                endpoint.Report(TransportDiagnosticKind.Closed, Number, Describe(CloseReason));
            }
        }

        private static string Describe(TransportCloseReason reason)
        {
            switch (reason)
            {
                case TransportCloseReason.Peer:
                    return "The peer closed the connection.";
                case TransportCloseReason.IdleTimeout:
                    return "The connection timed out.";
                case TransportCloseReason.ProtocolViolation:
                    return "The connection closed after a protocol violation.";
                case TransportCloseReason.Overloaded:
                    return "The connection closed because it was overloaded.";
                default:
                    return "The connection closed.";
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }
}
