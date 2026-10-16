using System;
using System.Collections.Concurrent;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// One live connection as the session engine sees it, handed between the network thread, which owns the transport,
    /// and the main thread, which owns the engine. Both directions are bounded queues:
    /// <list type="bullet">
    /// <item>The network thread moves session frames out of the transport only while the main thread has less than
    /// <see cref="InboundBudgetBytes"/> waiting, so a stalled main thread holds the sender back through the transport's
    /// own flow control instead of growing memory. Each frame also costs <see cref="FrameOverheadBytes"/>, so even
    /// empty frames fill the budget.</item>
    /// <item>Realtime and voice frames keep only the newest <see cref="MaximumInboundDatagrams"/>, as the transport
    /// does.</item>
    /// <item>The main thread may queue up to <see cref="OutboundLimitBytes"/>; past that the link closes, since the
    /// transport would refuse the backlog anyway.</item>
    /// </list>
    /// The link reports <see cref="EngineLinkState.Closed"/> only once the main thread has taken every session frame
    /// delivered before the close, so a reader that drains it and then checks its state never drops the last frames.
    /// A link names its peer by key and never by address.
    /// </summary>
    internal sealed class NetworkLink : IEngineLink
    {
        internal const long InboundBudgetBytes = 2 * 1024 * 1024;
        internal const int FrameOverheadBytes = 64;
        internal const int MaximumInboundDatagrams = 128;
        internal const long OutboundLimitBytes = 32L * 1024 * 1024;

        private readonly ConcurrentQueue<byte[]> inboundSession = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<Datagram> inboundDatagrams = new ConcurrentQueue<Datagram>();
        private readonly ConcurrentQueue<Datagram> outbound = new ConcurrentQueue<Datagram>();
        private readonly byte[] channelBinding;
        private long inboundBytes;
        private int inboundDatagramCount;
        private long outboundBytes;
        private int state;
        private int closed;
        private int closeRequested;
        private int overloaded;

        internal NetworkLink(long number, byte[] remoteStaticKey, MultiplayerPlayerKey playerKey, byte[] channelBinding, SourceNetwork? sourceNetwork = null)
        {
            if (remoteStaticKey == null || remoteStaticKey.Length != 32) throw new ArgumentException("A peer key is 32 bytes.", nameof(remoteStaticKey));
            if (channelBinding == null || channelBinding.Length != 32) throw new ArgumentException("A channel binding is 32 bytes.", nameof(channelBinding));
            Number = number;
            RemoteStaticKey = (byte[])remoteStaticKey.Clone();
            PeerIdentity = WireText.ToHex(remoteStaticKey);
            PlayerKey = playerKey;
            this.channelBinding = (byte[])channelBinding.Clone();
            SourceNetwork = sourceNetwork;
        }

        /// <summary>Gets the connection's endpoint-local number, the only name diagnostics use for it.</summary>
        internal long Number { get; }

        /// <summary>
        /// Gets the network an inbound link came from, as the opaque value its stack computed for a host's password limits;
        /// null for an outbound link and where sources cannot be told apart, such as through a relay.
        /// </summary>
        internal SourceNetwork? SourceNetwork { get; }

        internal byte[] RemoteStaticKey { get; }

        public string PeerIdentity { get; }

        public MultiplayerPlayerKey PlayerKey { get; }

        public EngineLinkState State
        {
            get
            {
                // The close is read before the queue: every frame delivered before the close is then visible here, so
                // an empty queue means the main thread has read them all.
                if (Volatile.Read(ref closed) != 0 && inboundSession.IsEmpty) return EngineLinkState.Closed;
                return (EngineLinkState)Volatile.Read(ref state);
            }
        }

        /// <summary>Gets a copy of the transport handshake hash, which password proofs bind to.</summary>
        public byte[] ChannelBinding => (byte[])channelBinding.Clone();

        /// <summary>Gets whether the main thread queued more than the link carries, which closed it.</summary>
        internal bool Overloaded => Volatile.Read(ref overloaded) != 0;

        internal long InboundBytes => Interlocked.Read(ref inboundBytes);

        /// <summary>Gets the bytes the main thread queued that the network thread has not taken yet.</summary>
        internal long OutboundBytes => Interlocked.Read(ref outboundBytes);

        /// <summary>Gets whether the main thread has room for more session frames: the network thread checks before it moves any.</summary>
        internal bool HasInboundRoom => Interlocked.Read(ref inboundBytes) < InboundBudgetBytes;

        internal bool CloseRequested => Volatile.Read(ref closeRequested) != 0;

        /// <summary>Gets whether the network thread has closed the link, whether or not the main thread has read it all.</summary>
        internal bool IsClosed => Volatile.Read(ref closed) != 0;

        public void Send(EngineChannel channel, ArraySegment<byte> frame)
        {
            if (frame.Array == null) throw new ArgumentNullException(nameof(frame));
            if (IsClosed || CloseRequested) return;
            var copy = new byte[frame.Count];
            Buffer.BlockCopy(frame.Array, frame.Offset, copy, 0, frame.Count);
            if (Interlocked.Add(ref outboundBytes, copy.Length) > OutboundLimitBytes)
            {
                Volatile.Write(ref overloaded, 1);
                Close();
                return;
            }

            outbound.Enqueue(new Datagram(channel, copy));
        }

        /// <summary>Takes the next frame, unreliable channels first as the transport delivers them.</summary>
        public bool TryReceive(out EngineChannel channel, out byte[] frame)
        {
            if (inboundDatagrams.TryDequeue(out var datagram))
            {
                Interlocked.Decrement(ref inboundDatagramCount);
                channel = datagram.Channel;
                frame = datagram.Bytes;
                return true;
            }

            if (inboundSession.TryDequeue(out var session))
            {
                Interlocked.Add(ref inboundBytes, -(session.Length + FrameOverheadBytes));
                channel = EngineChannel.Session;
                frame = session;
                return true;
            }

            channel = EngineChannel.Session;
            frame = Array.Empty<byte>();
            return false;
        }

        public void Close() => Volatile.Write(ref closeRequested, 1);

        /// <summary>Network thread: hands a session frame to the main thread.</summary>
        internal void DeliverSession(byte[] frame)
        {
            Interlocked.Add(ref inboundBytes, frame.Length + FrameOverheadBytes);
            inboundSession.Enqueue(frame);
        }

        /// <summary>Network thread: hands a realtime or voice frame over, dropping the oldest once the main thread falls behind.</summary>
        internal void DeliverDatagram(EngineChannel channel, byte[] frame)
        {
            inboundDatagrams.Enqueue(new Datagram(channel, frame));
            if (Interlocked.Increment(ref inboundDatagramCount) > MaximumInboundDatagrams && inboundDatagrams.TryDequeue(out _))
            {
                Interlocked.Decrement(ref inboundDatagramCount);
            }
        }

        /// <summary>Network thread: takes the next frame the main thread queued.</summary>
        internal bool TryTakeOutbound(out EngineChannel channel, out byte[] frame)
        {
            if (outbound.TryDequeue(out var item))
            {
                Interlocked.Add(ref outboundBytes, -item.Bytes.Length);
                channel = item.Channel;
                frame = item.Bytes;
                return true;
            }

            channel = EngineChannel.Session;
            frame = Array.Empty<byte>();
            return false;
        }

        /// <summary>Network thread: publishes the connection's state. Closed is final and follows the last frame delivered.</summary>
        internal void Publish(EngineLinkState value)
        {
            if (IsClosed) return;
            if (value != EngineLinkState.Closed)
            {
                Volatile.Write(ref state, (int)value);
                return;
            }

            Volatile.Write(ref closed, 1);

            // Nothing more will go out, so whatever the main thread queued is released at once.
            while (outbound.TryDequeue(out var dropped)) Interlocked.Add(ref outboundBytes, -dropped.Bytes.Length);
        }

        private readonly struct Datagram
        {
            internal Datagram(EngineChannel channel, byte[] bytes)
            {
                Channel = channel;
                Bytes = bytes;
            }

            internal EngineChannel Channel { get; }

            internal byte[] Bytes { get; }
        }
    }
}
