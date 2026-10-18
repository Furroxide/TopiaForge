using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>One system channel over the loopback network: a stream per peer, with its window, queues and credits.</summary>
    internal sealed class LoopbackChannel : IInternalSystemChannel
    {
        private readonly LoopbackControl owner;
        private readonly Dictionary<ParticipantId, Stream> streams = new Dictionary<ParticipantId, Stream>();
        private readonly Queue<Arrived> inbox = new Queue<Arrived>();
        private readonly List<Packet> arriving = new List<Packet>();
        private long sent;
        private long received;
        private long dropped;
        private long refused;

        internal LoopbackChannel(LoopbackControl owner, InternalSystemChannelKind kind)
        {
            this.owner = owner;
            Kind = kind;
        }

        public InternalSystemChannelKind Kind { get; }

        public bool IsOpen { get; private set; } = true;

        public ulong Generation => owner.Generation;

        /// <summary>Gets or sets where every queued message is copied, with its recipient, when a test wants to read the traffic.</summary>
        internal List<(ParticipantId To, byte[] Payload)>? Log { get; set; }

        /// <summary>Gets the messages this channel sent, one per recipient, as the engine counts them.</summary>
        internal long SentMessages => sent;

        internal long DroppedMessages => dropped;

        internal int Waiting => inbox.Count;

        /// <summary>Gets what this sender has outstanding to <paramref name="peer"/>: sent, and not yet credited back.</summary>
        internal (int Messages, int Bytes) Outstanding(ParticipantId peer) =>
            streams.TryGetValue(peer, out var stream) ? (stream.OutstandingMessages, stream.OutstandingBytes) : (0, 0);

        public ulong GetGeneration(ParticipantId peer) => streams.TryGetValue(peer, out var stream) ? stream.Generation : 0;

        public bool CanSend(InternalSystemTarget target, int payloadBytes)
        {
            if (!IsOpen || payloadBytes > InternalSystemLimits.MaximumPayloadBytes) return false;
            if (!owner.IsHost) return target.Kind == InternalSystemTargetKind.Host && streams.TryGetValue(HostId, out var host) && HasRoom(host, payloadBytes);
            if (target.Kind == InternalSystemTargetKind.Host) return false;
            foreach (var pair in streams)
            {
                if (target.Selects(pair.Key) && !HasRoom(pair.Value, payloadBytes)) return false;
            }

            return true;
        }

        public OperationResult<int> Send(ArraySegment<byte> payload, InternalSystemTarget target, ICollection<ParticipantId>? skipped = null)
        {
            if (!IsOpen) return OperationResult<int>.Failure(ModErrorCode.InvalidState, "The system channel is closed.");
            if (payload.Count > InternalSystemLimits.MaximumPayloadBytes) return OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "Too large.");
            var bytes = new byte[payload.Count];
            Buffer.BlockCopy(payload.Array!, payload.Offset, bytes, 0, payload.Count);
            if (!owner.IsHost)
            {
                if (target.Kind != InternalSystemTargetKind.Host) return OperationResult<int>.Failure(ModErrorCode.NotAuthoritative, "Players send to the host only.");
                if (!streams.TryGetValue(HostId, out var host)) return OperationResult<int>.Failure(ModErrorCode.Unavailable, "Not joined.");
                if (!HasRoom(host, bytes.Length))
                {
                    refused++;
                    return OperationResult<int>.Failure(ModErrorCode.RateLimited, "The host has not taken the earlier messages yet.");
                }

                Queue(host, HostId, bytes);
                return OperationResult<int>.Success(1);
            }

            if (target.Kind == InternalSystemTargetKind.Host) return OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "The host does not send to itself.");
            if (target.Kind == InternalSystemTargetKind.Only)
            {
                if (!streams.TryGetValue(target.Participant, out var only)) return OperationResult<int>.Success(0);
                if (!HasRoom(only, bytes.Length))
                {
                    refused++;
                    return OperationResult<int>.Failure(ModErrorCode.RateLimited, "The player has not taken the earlier messages yet.");
                }

                Queue(only, target.Participant, bytes);
                return OperationResult<int>.Success(1);
            }

            var recipients = 0;
            foreach (var pair in streams)
            {
                if (!target.Selects(pair.Key)) continue;
                if (!HasRoom(pair.Value, bytes.Length))
                {
                    refused++;
                    skipped?.Add(pair.Key);
                    continue;
                }

                Queue(pair.Value, pair.Key, bytes);
                recipients++;
            }

            return OperationResult<int>.Success(recipients);
        }

        public bool TryReceive(out InternalSystemMessage message)
        {
            while (inbox.Count > 0)
            {
                var next = inbox.Dequeue();
                if (!streams.TryGetValue(next.Sender, out var stream) || stream.Id != next.StreamId)
                {
                    dropped++;
                    continue;
                }

                stream.ConsumedMessages++;
                stream.ConsumedBytes += next.Payload.Length;
                received++;
                message = new InternalSystemMessage(next.Sender, next.Payload);
                return true;
            }

            message = default;
            return false;
        }

        public InternalSystemChannelStatistics GetStatistics() => new InternalSystemChannelStatistics(Kind, sent, received, dropped, inbox.Count, refused);

        public void Dispose()
        {
            IsOpen = false;
            dropped += inbox.Count;
            inbox.Clear();
        }

        /// <summary>Puts a message from <paramref name="sender"/> straight into the inbox, as a hostile or broken peer's would arrive.</summary>
        internal void Inject(ParticipantId sender, byte[] payload)
        {
            if (!streams.TryGetValue(sender, out var stream)) throw new InvalidOperationException("No stream with that peer.");
            inbox.Enqueue(new Arrived(sender, stream.Id, payload));
        }

        internal void StartStream(ParticipantId peer, long id) => streams[peer] = new Stream(id, owner.Generation);

        internal void EndStream(ParticipantId peer)
        {
            if (!streams.Remove(peer)) return;
            var kept = new List<Arrived>();
            while (inbox.Count > 0)
            {
                var next = inbox.Dequeue();
                if (next.Sender.Equals(peer)) dropped++;
                else kept.Add(next);
            }

            foreach (var next in kept) inbox.Enqueue(next);
        }

        /// <summary>The flush: queued messages and the credits for what the owner took leave for their peers.</summary>
        internal void Transmit(LoopbackNetPair network)
        {
            foreach (var pair in streams)
            {
                var peer = network.Find(pair.Key)?.Channel(Kind);
                var stream = pair.Value;
                if (peer == null)
                {
                    dropped += stream.Outbox.Count;
                    stream.Outbox.Clear();
                    continue;
                }

                foreach (var bytes in stream.Outbox) peer.arriving.Add(new Packet(owner.Id, stream.Id, bytes, 0, 0));
                stream.Outbox.Clear();
                if (stream.ConsumedMessages > 0)
                {
                    peer.arriving.Add(new Packet(owner.Id, stream.Id, null, stream.ConsumedMessages, stream.ConsumedBytes));
                    stream.ConsumedMessages = 0;
                    stream.ConsumedBytes = 0;
                }
            }
        }

        /// <summary>The pump: what arrived from current streams lands in the inbox, and credits reopen the windows.</summary>
        internal void Deliver()
        {
            foreach (var packet in arriving)
            {
                if (!streams.TryGetValue(packet.Sender, out var stream) || stream.Id != packet.StreamId)
                {
                    if (packet.Payload != null) dropped++;
                    continue;
                }

                if (packet.Payload == null)
                {
                    stream.OutstandingMessages -= packet.CreditMessages;
                    stream.OutstandingBytes -= packet.CreditBytes;
                    continue;
                }

                inbox.Enqueue(new Arrived(packet.Sender, packet.StreamId, packet.Payload));
            }

            arriving.Clear();
        }

        private ParticipantId HostId => owner.Network.Host.Id;

        private bool HasRoom(Stream stream, int payloadBytes)
        {
            var windowMessages = owner.IsHost ? HostLimits.Messages(owner) : InternalSystemLimits.PlayerWindowMessages;
            var windowBytes = owner.IsHost ? HostLimits.Bytes(owner) : InternalSystemLimits.PlayerWindowBytes;
            return stream.OutstandingMessages + 1 <= windowMessages && stream.OutstandingBytes + payloadBytes <= windowBytes;
        }

        private void Queue(Stream stream, ParticipantId to, byte[] bytes)
        {
            Log?.Add((to, bytes));
            stream.Outbox.Add(bytes);
            stream.OutstandingMessages++;
            stream.OutstandingBytes += bytes.Length;
            sent++;
        }

        private sealed class Stream
        {
            internal Stream(long id, ulong generation)
            {
                Id = id;
                Generation = generation;
            }

            internal long Id { get; }

            internal ulong Generation { get; }

            internal List<byte[]> Outbox { get; } = new List<byte[]>();

            internal int OutstandingMessages { get; set; }

            internal int OutstandingBytes { get; set; }

            internal int ConsumedMessages { get; set; }

            internal int ConsumedBytes { get; set; }
        }

        private readonly struct Packet
        {
            internal Packet(ParticipantId sender, long streamId, byte[]? payload, int creditMessages, int creditBytes)
            {
                Sender = sender;
                StreamId = streamId;
                Payload = payload;
                CreditMessages = creditMessages;
                CreditBytes = creditBytes;
            }

            internal ParticipantId Sender { get; }

            internal long StreamId { get; }

            internal byte[]? Payload { get; }

            internal int CreditMessages { get; }

            internal int CreditBytes { get; }
        }

        private readonly struct Arrived
        {
            internal Arrived(ParticipantId sender, long streamId, byte[] payload)
            {
                Sender = sender;
                StreamId = streamId;
                Payload = payload;
            }

            internal ParticipantId Sender { get; }

            internal long StreamId { get; }

            internal byte[] Payload { get; }
        }

        /// <summary>The host's window, which a test may narrow to make a full window easy to reach.</summary>
        private static class HostLimits
        {
            internal static int Messages(LoopbackControl host) => host.Network.HostWindowMessages;

            internal static int Bytes(LoopbackControl host) => host.Network.HostWindowBytes;
        }
    }
}
