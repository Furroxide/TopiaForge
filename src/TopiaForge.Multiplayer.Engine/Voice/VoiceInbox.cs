using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>A received voice frame waiting for playback: one speaker's codec packet and its place in their stream.</summary>
    internal readonly struct VoiceFrame
    {
        internal VoiceFrame(ParticipantId speaker, ulong stream, ulong sequence, ulong timestamp, VoiceFrameFlags flags, ArraySegment<byte> payload, long arrivalMicros)
        {
            Speaker = speaker;
            Stream = stream;
            Sequence = sequence;
            Timestamp = timestamp;
            Flags = flags;
            Payload = payload;
            ArrivalMicros = arrivalMicros;
        }

        /// <summary>Gets the speaker, as the host stamped it.</summary>
        internal ParticipantId Speaker { get; }

        /// <summary>
        /// Gets the generation of the speaker's stream, as the host stamped it. It changes when the talker starts a new
        /// stream, after a rejoin or a restart, and is never reused within the host's session, so a jitter buffer starts
        /// again whenever it changes. Generations from different sessions are unrelated.
        /// </summary>
        internal ulong Stream { get; }

        internal ulong Sequence { get; }

        /// <summary>Gets the frame's position in the speaker's stream, in codec frames.</summary>
        internal ulong Timestamp { get; }

        internal VoiceFrameFlags Flags { get; }

        /// <summary>Gets the codec packet. It points into the received datagram, which nothing else reuses.</summary>
        internal ArraySegment<byte> Payload { get; }

        /// <summary>Gets when the engine received the frame, in engine microseconds, for jitter estimates.</summary>
        internal long ArrivalMicros { get; }
    }

    /// <summary>
    /// Received voice frames waiting for the provider, oldest first. It holds a fixed number; when full, the oldest frame
    /// makes room, since a frame that waited that long is too late to play anyway.
    /// </summary>
    internal sealed class VoiceInbox
    {
        private readonly VoiceFrame[] ring;
        private int head;
        private int count;

        internal VoiceInbox(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            ring = new VoiceFrame[capacity];
        }

        internal int Count => count;

        internal int Capacity => ring.Length;

        /// <summary>Queues a frame; returns false when it displaced the oldest one.</summary>
        internal bool Enqueue(VoiceFrame frame)
        {
            var displaced = count == ring.Length;
            if (displaced)
            {
                head = (head + 1) % ring.Length;
                count--;
            }

            ring[(head + count) % ring.Length] = frame;
            count++;
            return !displaced;
        }

        internal bool TryDequeue(out VoiceFrame frame)
        {
            if (count == 0)
            {
                frame = default;
                return false;
            }

            frame = ring[head];
            ring[head] = default;
            head = (head + 1) % ring.Length;
            count--;
            return true;
        }

        internal void Clear()
        {
            Array.Clear(ring, 0, ring.Length);
            head = 0;
            count = 0;
        }
    }
}
