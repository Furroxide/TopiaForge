using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>
    /// The loopback channel's realtime lane, which keeps the engine's datagram contract: a datagram leaves at its sender's
    /// flush and arrives at its receiver's next pump, stamped with its stream, so one from an ended stream is dropped; a
    /// joined player keeps to its rate; a receiver keeps at most a queue's worth, the oldest going first; and nothing is
    /// sent twice. Datagrams travel in buffers the network reuses, so the lane allocates nothing once warm and a test can
    /// measure what the runtime itself allocates. A received datagram's payload stays good until the next one is taken,
    /// as the engine's does. A test may lose datagrams on purpose through <see cref="LoseDatagram"/>.
    /// </summary>
    internal sealed partial class LoopbackChannel
    {
        /// <summary>The datagrams a joined player may send between two of its pumps.</summary>
        internal const int DatagramsPerFrame = 4;

        // What a send answers, made once, as the engine's are: a result never changes, so sending allocates nothing.
        private static readonly OperationResult<int>[] SentResults = { OperationResult<int>.Success(0), OperationResult<int>.Success(1) };

        private readonly Queue<ArrivedDatagram> datagramInbox = new Queue<ArrivedDatagram>();
        private readonly List<ArrivedDatagram> arrivingDatagrams = new List<ArrivedDatagram>();
        private byte[]? lastTaken;
        private long datagramsSent;
        private long datagramsReceived;
        private long datagramsDropped;
        private int datagramsThisFrame;

        /// <summary>Gets or sets where every datagram sent is copied, with its recipient, when a test wants to read the traffic.</summary>
        internal List<(ParticipantId To, byte[] Payload)>? DatagramLog { get; set; }

        /// <summary>Gets or sets a rule that loses a datagram on its way to a recipient, as a lossy path would.</summary>
        internal Func<ParticipantId, ArraySegment<byte>, bool>? LoseDatagram { get; set; }

        /// <summary>Gets the datagrams this channel sent, one per recipient.</summary>
        internal long DatagramsSent => datagramsSent;

        internal int DatagramsWaiting => datagramInbox.Count;

        public OperationResult<int> SendDatagram(ArraySegment<byte> payload, InternalSystemTarget target)
        {
            if (payload.Array == null) throw new ArgumentNullException(nameof(payload));
            if (!IsOpen) return OperationResult<int>.Failure(ModErrorCode.InvalidState, "The system channel is closed.");
            if (Kind == InternalSystemChannelKind.Time) return OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "The time channel has no datagram lane.");
            if (payload.Count > InternalSystemLimits.MaximumDatagramBytes) return OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "Too large.");
            if (!owner.IsHost)
            {
                if (target.Kind != InternalSystemTargetKind.Host) return OperationResult<int>.Failure(ModErrorCode.NotAuthoritative, "Players send to the host only.");
                if (!streams.TryGetValue(HostId, out var host)) return OperationResult<int>.Failure(ModErrorCode.Unavailable, "Not joined.");
                if (!TakeDatagramToken()) return OperationResult<int>.Failure(ModErrorCode.RateLimited, "Faster than the host takes datagrams.");
                QueueDatagram(host, HostId, payload);
                return SentResults[1];
            }

            if (target.Kind == InternalSystemTargetKind.Host) return OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "The host does not send to itself.");
            var recipients = 0;
            foreach (var pair in streams)
            {
                if (!target.Selects(pair.Key)) continue;
                QueueDatagram(pair.Value, pair.Key, payload);
                recipients++;
            }

            return recipients < SentResults.Length ? SentResults[recipients] : OperationResult<int>.Success(recipients);
        }

        public bool TryReceiveDatagram(out InternalSystemDatagram datagram)
        {
            ReleaseTaken();
            if (datagramInbox.Count == 0)
            {
                datagram = default;
                return false;
            }

            var next = datagramInbox.Dequeue();
            if (next.Pooled) lastTaken = next.Buffer;
            datagram = new InternalSystemDatagram(next.Sender, new ArraySegment<byte>(next.Buffer, 0, next.Count));
            return true;
        }

        /// <summary>Puts a datagram from <paramref name="sender"/> straight into the queue, as a hostile or broken peer's would arrive.</summary>
        internal void InjectDatagram(ParticipantId sender, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (!streams.TryGetValue(sender, out var stream)) throw new InvalidOperationException("No stream with that peer.");
            Enqueue(new ArrivedDatagram(sender, stream.Id, payload, payload.Length, false));
        }

        private void QueueDatagram(Stream stream, ParticipantId to, ArraySegment<byte> payload)
        {
            var pool = owner.Network.DatagramBuffers;
            var buffer = pool.Count > 0 ? pool.Pop() : new byte[InternalSystemLimits.MaximumDatagramBytes];
            Buffer.BlockCopy(payload.Array!, payload.Offset, buffer, 0, payload.Count);
            if (DatagramLog != null)
            {
                var copy = new byte[payload.Count];
                Buffer.BlockCopy(payload.Array!, payload.Offset, copy, 0, payload.Count);
                DatagramLog.Add((to, copy));
            }

            stream.Datagrams.Add(new PooledDatagram(buffer, payload.Count));
            datagramsSent++;
        }

        private void TransmitDatagrams(LoopbackChannel peer, Stream stream)
        {
            foreach (var datagram in stream.Datagrams)
            {
                if (LoseDatagram != null && LoseDatagram(peer.owner.Id, new ArraySegment<byte>(datagram.Buffer, 0, datagram.Count)))
                {
                    Release(datagram.Buffer);
                    continue;
                }

                peer.arrivingDatagrams.Add(new ArrivedDatagram(owner.Id, stream.Id, datagram.Buffer, datagram.Count, true));
            }

            stream.Datagrams.Clear();
        }

        /// <summary>Drops what a stream had queued to send, as a flush to a peer that is gone does.</summary>
        private void DropQueued(Stream stream)
        {
            foreach (var datagram in stream.Datagrams) Release(datagram.Buffer);
            datagramsDropped += stream.Datagrams.Count;
            stream.Datagrams.Clear();
        }

        private void DeliverDatagrams()
        {
            datagramsThisFrame = 0;
            foreach (var datagram in arrivingDatagrams)
            {
                if (!streams.TryGetValue(datagram.Sender, out var stream) || stream.Id != datagram.StreamId)
                {
                    datagramsDropped++;
                    if (datagram.Pooled) Release(datagram.Buffer);
                    continue;
                }

                Enqueue(datagram);
            }

            arrivingDatagrams.Clear();
        }

        private void Enqueue(ArrivedDatagram datagram)
        {
            if (datagramInbox.Count >= InternalSystemLimits.DatagramQueueLength)
            {
                var oldest = datagramInbox.Dequeue();
                if (oldest.Pooled) Release(oldest.Buffer);
                datagramsDropped++;
            }

            datagramInbox.Enqueue(datagram);
            datagramsReceived++;
        }

        /// <summary>Drops what waits from <paramref name="peer"/>, or from everyone, as a stream that ends or a channel that closes does.</summary>
        private void DropDatagrams(ParticipantId? peer)
        {
            if (datagramInbox.Count == 0) return;
            var kept = datagramInbox.ToArray();
            datagramInbox.Clear();
            foreach (var datagram in kept)
            {
                if (peer == null || datagram.Sender.Equals(peer.Value))
                {
                    datagramsDropped++;
                    if (datagram.Pooled) Release(datagram.Buffer);
                }
                else
                {
                    datagramInbox.Enqueue(datagram);
                }
            }
        }

        /// <summary>The payload taken last is no longer the receiver's once it asks for the next, so its buffer goes back.</summary>
        private void ReleaseTaken()
        {
            var taken = lastTaken;
            lastTaken = null;
            if (taken != null) Release(taken);
        }

        private void Release(byte[] buffer) => owner.Network.DatagramBuffers.Push(buffer);

        /// <summary>A joined player's rate: a few datagrams a frame, more than an honest player sends and fewer than a flood.</summary>
        private bool TakeDatagramToken()
        {
            if (datagramsThisFrame >= DatagramsPerFrame) return false;
            datagramsThisFrame++;
            return true;
        }

        /// <summary>One datagram queued on a stream, in a buffer the network lends.</summary>
        private readonly struct PooledDatagram
        {
            internal PooledDatagram(byte[] buffer, int count)
            {
                Buffer = buffer;
                Count = count;
            }

            internal byte[] Buffer { get; }

            internal int Count { get; }
        }

        private readonly struct ArrivedDatagram
        {
            internal ArrivedDatagram(ParticipantId sender, long streamId, byte[] buffer, int count, bool pooled)
            {
                Sender = sender;
                StreamId = streamId;
                Buffer = buffer;
                Count = count;
                Pooled = pooled;
            }

            internal ParticipantId Sender { get; }

            internal long StreamId { get; }

            internal byte[] Buffer { get; }

            internal int Count { get; }

            /// <summary>Gets whether the buffer is the network's, to go back once taken, rather than a test's own.</summary>
            internal bool Pooled { get; }
        }
    }
}
