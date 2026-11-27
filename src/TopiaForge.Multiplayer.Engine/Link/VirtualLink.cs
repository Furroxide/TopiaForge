using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal readonly struct VirtualLinkPair
    {
        internal VirtualLinkPair(VirtualLinkEnd hostEnd, VirtualLinkEnd clientEnd)
        {
            HostEnd = hostEnd;
            ClientEnd = clientEnd;
        }

        internal VirtualLinkEnd HostEnd { get; }
        internal VirtualLinkEnd ClientEnd { get; }
    }

    /// <summary>One direction of a virtual link: its send sequence, FIFO schedule and delivery cursor.</summary>
    internal sealed class VirtualDirection
    {
        internal VirtualDirection(VirtualLinkEnd receiver)
        {
            Receiver = receiver;
        }

        internal VirtualLinkEnd Receiver { get; }
        internal ulong SentSequence { get; set; }
        internal ulong DeliveredSequence { get; set; }
        internal ulong LastDeliverAt { get; set; }
        internal bool Severed { get; set; }
        internal bool Closing { get; set; }

        /// <summary>Gets or sets whether due frames are held back, as on an interrupted path.</summary>
        internal bool Held { get; set; }

        /// <summary>Gets or sets the tick before which due frames are held, as during a partition.</summary>
        internal ulong HeldUntilTick { get; set; }

        /// <summary>Gets or sets whether a test is holding frames on this direction until it releases them.</summary>
        internal bool HoldsFrames { get; set; }

        internal int HoldBudget { get; set; }
        internal int DropBudget { get; set; }

        /// <summary>
        /// Gets or sets when the link is free for the next Session frame, in millionths of a tick
        /// (<see cref="VirtualEngineTransport.SubTicksPerTick"/>); datagram bytes push it back too.
        /// </summary>
        internal ulong SessionBusyUntil { get; set; }

        /// <summary>Gets or sets when the datagram lane is free, in millionths of a tick; it never waits for Session frames.</summary>
        internal ulong DatagramBusyUntil { get; set; }

        /// <summary>Gets the first tick at which the link is free for the next Session frame.</summary>
        internal ulong BusyUntilTick => VirtualEngineTransport.CeilingTick(SessionBusyUntil);
        internal byte[]? LastFrame { get; set; }
        internal ulong LastSequence { get; set; }
        internal EngineChannel LastChannel { get; set; }
        /// <summary>Gets the bytes sent on every channel together.</summary>
        internal long BytesSent { get; set; }

        internal long FramesSent { get; set; }

        /// <summary>Gets the bytes delivered on every channel together, not counting dropped copies.</summary>
        internal long BytesDelivered { get; set; }

        internal long FramesDelivered { get; set; }

        /// <summary>Gets each datagram channel's last sequence sent, indexed by <see cref="EngineChannel"/>.</summary>
        internal ulong[] DatagramSent { get; } = new ulong[ChannelCount];

        /// <summary>Gets each datagram channel's newest sequence delivered; anything older is dropped.</summary>
        internal ulong[] DatagramDelivered { get; } = new ulong[ChannelCount];

        /// <summary>Gets each datagram channel's datagrams lost, on the wire or to a path that was down.</summary>
        internal long[] DatagramsLost { get; } = new long[ChannelCount];

        /// <summary>Gets each datagram channel's datagrams dropped for arriving behind a newer one, duplicates included.</summary>
        internal long[] DatagramsStale { get; } = new long[ChannelCount];

        /// <summary>
        /// Gets each datagram channel's datagrams shed because the link could not carry them in time; see
        /// <see cref="VirtualEngineTransport.DatagramBacklogTicks"/>. They never reach the wire, so they count as neither
        /// sent nor lost.
        /// </summary>
        internal long[] DatagramsShed { get; } = new long[ChannelCount];

        /// <summary>
        /// Gets each datagram channel's remaining share of the link while the Session lane is behind, in millionths of a
        /// tick of link time. Each starts full.
        /// </summary>
        internal ulong[] ShareTokens { get; } =
        {
            VirtualEngineTransport.DatagramBacklogTicks * VirtualEngineTransport.SubTicksPerTick,
            VirtualEngineTransport.DatagramBacklogTicks * VirtualEngineTransport.SubTicksPerTick,
            VirtualEngineTransport.DatagramBacklogTicks * VirtualEngineTransport.SubTicksPerTick
        };

        /// <summary>Gets when each channel's share was last refilled, in millionths of a tick.</summary>
        internal ulong[] ShareRefilledAt { get; } = new ulong[ChannelCount];

        internal long[] ChannelBytesSent { get; } = new long[ChannelCount];
        internal long[] ChannelFramesSent { get; } = new long[ChannelCount];
        internal long[] ChannelBytesDelivered { get; } = new long[ChannelCount];
        internal long[] ChannelFramesDelivered { get; } = new long[ChannelCount];

        private const int ChannelCount = 3;

        internal void CountSent(EngineChannel channel, int bytes)
        {
            BytesSent += bytes;
            FramesSent++;
            ChannelBytesSent[(int)channel] += bytes;
            ChannelFramesSent[(int)channel]++;
        }

        internal void CountDelivered(EngineChannel channel, int bytes)
        {
            BytesDelivered += bytes;
            FramesDelivered++;
            ChannelBytesDelivered[(int)channel] += bytes;
            ChannelFramesDelivered[(int)channel]++;
        }
    }

    /// <summary>One end of a virtual link, as an engine sees it.</summary>
    internal sealed class VirtualLinkEnd : IEngineLink
    {
        private readonly VirtualEngineTransport transport;
        /// <summary>The datagrams a link holds for its reader, as the real transport's connection does; more drop the oldest.</summary>
        internal const int MaximumInboundDatagrams = 128;

        private readonly Queue<KeyValuePair<EngineChannel, byte[]>> datagrams = new Queue<KeyValuePair<EngineChannel, byte[]>>();
        private readonly Queue<byte[]> sessionFrames = new Queue<byte[]>();

        private readonly byte[] channelBinding;

        internal VirtualLinkEnd(VirtualEngineTransport transport, string peerIdentity, byte[] channelBinding)
        {
            this.transport = transport;
            PeerIdentity = peerIdentity;
            this.channelBinding = (byte[])channelBinding.Clone();
        }

        public string PeerIdentity { get; }

        public MultiplayerPlayerKey PlayerKey => default;

        public byte[] ChannelBinding => (byte[])channelBinding.Clone();

        public EngineLinkState State { get; internal set; } = EngineLinkState.Open;

        internal VirtualDirection? Outbound { get; set; }

        internal VirtualLinkEnd? Peer { get; set; }

        internal int PendingFrames => datagrams.Count + sessionFrames.Count;

        /// <summary>Gets the datagrams dropped because the reader left more than the link holds waiting.</summary>
        internal long DatagramsOverflowed { get; private set; }

        public void Send(EngineChannel channel, ArraySegment<byte> frame)
        {
            if (State == EngineLinkState.Closed) return;
            var copy = new byte[frame.Count];
            Buffer.BlockCopy(frame.Array!, frame.Offset, copy, 0, frame.Count);
            transport.Send(this, channel, copy);
        }

        /// <summary>Dequeues the next frame: waiting datagrams first, then session frames in order, as the real transport does.</summary>
        public bool TryReceive(out EngineChannel channel, out byte[] frame)
        {
            if (datagrams.Count > 0)
            {
                var next = datagrams.Dequeue();
                channel = next.Key;
                frame = next.Value;
                return true;
            }

            if (sessionFrames.Count > 0)
            {
                channel = EngineChannel.Session;
                frame = sessionFrames.Dequeue();
                return true;
            }

            channel = EngineChannel.Session;
            frame = Array.Empty<byte>();
            return false;
        }

        public void Close() => transport.Close(this);

        internal void Deliver(EngineChannel channel, byte[] frame)
        {
            if (channel == EngineChannel.Session)
            {
                sessionFrames.Enqueue(frame);
                return;
            }

            if (datagrams.Count == MaximumInboundDatagrams)
            {
                datagrams.Dequeue();
                DatagramsOverflowed++;
            }

            datagrams.Enqueue(new KeyValuePair<EngineChannel, byte[]>(channel, frame));
        }
    }
}
