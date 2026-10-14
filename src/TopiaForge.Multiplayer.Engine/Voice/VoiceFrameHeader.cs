using System;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>What a voice frame says about its talk spurt.</summary>
    [Flags]
    internal enum VoiceFrameFlags : byte
    {
        None = 0,

        /// <summary>The frame is silence under discontinuous transmission; frames may be absent until talk resumes.</summary>
        Discontinuous = 1,

        /// <summary>The last frame of a talk spurt: push-to-talk was released or voice activity ended.</summary>
        EndOfTalk = 2
    }

    /// <summary>One frame's place in its speaker's stream and where its codec packet sits in the datagram.</summary>
    internal readonly struct VoiceFrameHeader
    {
        internal VoiceFrameHeader(ulong speaker, ulong stream, ulong sequence, ulong timestamp, VoiceFrameFlags flags, int payloadOffset, int payloadLength)
        {
            Speaker = speaker;
            Stream = stream;
            Sequence = sequence;
            Timestamp = timestamp;
            Flags = flags;
            PayloadOffset = payloadOffset;
            PayloadLength = payloadLength;
        }

        /// <summary>Gets the host-assigned speaker index of a relayed frame, or zero on an uplink frame.</summary>
        internal ulong Speaker { get; }

        /// <summary>
        /// Gets the generation of the speaker's stream on a relayed frame, or zero on an uplink frame. The host gives each
        /// new stream one of its own, so a change means the talker started again, whatever the sequence numbers say.
        /// </summary>
        internal ulong Stream { get; }

        /// <summary>Gets the frame's sequence: frames sent, from 1, so a gap means loss.</summary>
        internal ulong Sequence { get; }

        /// <summary>Gets the frame's stream time in codec frames, so a jump with no sequence gap means the talker paused.</summary>
        internal ulong Timestamp { get; }

        internal VoiceFrameFlags Flags { get; }
        internal int PayloadOffset { get; }
        internal int PayloadLength { get; }
    }

    /// <summary>
    /// The voice datagram on <see cref="EngineChannel.Voice"/>: one or two consecutive codec frames of one speaker. Two
    /// 20 ms frames per datagram make 25 datagrams a second, within the plan's 30, and halve what the transport's
    /// per-datagram overhead costs against its 32 kbit/s client voice class. A talker sends uplink datagrams to the host, which
    /// carry no speaker: the host knows the sender from the connection. The host relays them with the speaker index it
    /// assigned, so no client can speak as someone else, and with the generation of the speaker's stream, so a listener
    /// notices a restart even when the new stream's first datagram never arrives.
    /// </summary>
    /// <remarks>
    /// <code>
    /// uplink:  01, sequence, timestamp, count, then count times: flags, length, payload
    /// relayed: 02, speaker, stream, sequence, timestamp, count, then count times: flags, length, payload
    /// </code>
    /// Numbers are minimal LEB128 varints; the sequence and timestamp are the first frame's, and the second frame's are one
    /// more. Parsing never throws: a datagram that breaks any rule is refused whole.
    /// </remarks>
    internal readonly struct VoicePacket
    {
        internal const byte UplinkKind = 0x01;
        internal const byte RelayedKind = 0x02;
        internal const int MaximumFrames = 2;
        internal const VoiceFrameFlags KnownFlags = VoiceFrameFlags.Discontinuous | VoiceFrameFlags.EndOfTalk;

        private VoicePacket(int count, VoiceFrameHeader first, VoiceFrameHeader second)
        {
            Count = count;
            First = first;
            Second = second;
        }

        internal int Count { get; }
        internal VoiceFrameHeader First { get; }
        internal VoiceFrameHeader Second { get; }

        /// <summary>Gets the most bytes a datagram of two <paramref name="maximumPayload"/>-byte frames can take.</summary>
        internal static int MaximumBytes(int maximumPayload) => 1 + (4 * 10) + 1 + (MaximumFrames * (1 + 5 + maximumPayload));

        internal VoiceFrameHeader this[int index] => index == 0 ? First : Second;

        /// <summary>
        /// Writes a talker's uplink datagram of one frame, or two when <paramref name="secondPayload"/> is not empty, and
        /// returns its length.
        /// </summary>
        internal static int WriteUplink(
            Span<byte> destination,
            ulong sequence,
            ulong timestamp,
            VoiceFrameFlags firstFlags,
            ReadOnlySpan<byte> firstPayload,
            VoiceFrameFlags secondFlags,
            ReadOnlySpan<byte> secondPayload)
        {
            var position = 0;
            destination[position++] = UplinkKind;
            return WriteFrames(destination, position, sequence, timestamp, firstFlags, firstPayload, secondFlags, secondPayload);
        }

        /// <summary>
        /// Writes a host's relayed datagram, stamped with the <paramref name="speaker"/> index and the generation of their
        /// <paramref name="stream"/>, and returns its length.
        /// </summary>
        internal static int WriteRelayed(
            Span<byte> destination,
            ulong speaker,
            ulong stream,
            ulong sequence,
            ulong timestamp,
            VoiceFrameFlags firstFlags,
            ReadOnlySpan<byte> firstPayload,
            VoiceFrameFlags secondFlags,
            ReadOnlySpan<byte> secondPayload)
        {
            if (speaker == 0) throw new ArgumentOutOfRangeException(nameof(speaker), "Speaker index zero is never assigned.");
            var position = 0;
            destination[position++] = RelayedKind;
            WriteVarUInt(destination, ref position, speaker);
            WriteVarUInt(destination, ref position, stream);
            return WriteFrames(destination, position, sequence, timestamp, firstFlags, firstPayload, secondFlags, secondPayload);
        }

        private static int WriteFrames(
            Span<byte> destination,
            int position,
            ulong sequence,
            ulong timestamp,
            VoiceFrameFlags firstFlags,
            ReadOnlySpan<byte> firstPayload,
            VoiceFrameFlags secondFlags,
            ReadOnlySpan<byte> secondPayload)
        {
            WriteVarUInt(destination, ref position, sequence);
            WriteVarUInt(destination, ref position, timestamp);
            destination[position++] = secondPayload.IsEmpty ? (byte)1 : (byte)2;
            WriteFrame(destination, ref position, firstFlags, firstPayload);
            if (!secondPayload.IsEmpty) WriteFrame(destination, ref position, secondFlags, secondPayload);
            return position;
        }

        /// <summary>Reads an uplink datagram; false when it breaks any rule of the format.</summary>
        internal static bool TryReadUplink(ReadOnlySpan<byte> datagram, int maximumPayload, out VoicePacket packet) =>
            TryRead(datagram, UplinkKind, maximumPayload, out packet);

        /// <summary>Reads a relayed datagram; false when it breaks any rule of the format.</summary>
        internal static bool TryReadRelayed(ReadOnlySpan<byte> datagram, int maximumPayload, out VoicePacket packet) =>
            TryRead(datagram, RelayedKind, maximumPayload, out packet);

        private static bool TryRead(ReadOnlySpan<byte> datagram, byte kind, int maximumPayload, out VoicePacket packet)
        {
            packet = default;
            var position = 0;
            if (datagram.Length < 1 || datagram[position++] != kind) return false;
            ulong speaker = 0;
            ulong stream = 0;
            if (kind == RelayedKind && (!TryReadVarUInt(datagram, ref position, out speaker) || speaker == 0)) return false;
            if (kind == RelayedKind && !TryReadVarUInt(datagram, ref position, out stream)) return false;
            if (!TryReadVarUInt(datagram, ref position, out var sequence) || sequence == 0 || sequence == ulong.MaxValue) return false;
            if (!TryReadVarUInt(datagram, ref position, out var timestamp) || timestamp == ulong.MaxValue) return false;
            if (position >= datagram.Length) return false;
            var count = datagram[position++];
            if (count < 1 || count > MaximumFrames) return false;
            if (!TryReadFrame(datagram, ref position, maximumPayload, out var flags, out var offset, out var length)) return false;
            var first = new VoiceFrameHeader(speaker, stream, sequence, timestamp, flags, offset, length);
            var second = default(VoiceFrameHeader);
            if (count == 2)
            {
                if (!TryReadFrame(datagram, ref position, maximumPayload, out flags, out offset, out length)) return false;
                second = new VoiceFrameHeader(speaker, stream, sequence + 1, timestamp + 1, flags, offset, length);
            }

            if (position != datagram.Length) return false;
            packet = new VoicePacket(count, first, second);
            return true;
        }

        private static void WriteFrame(Span<byte> destination, ref int position, VoiceFrameFlags flags, ReadOnlySpan<byte> payload)
        {
            destination[position++] = (byte)flags;
            WriteVarUInt(destination, ref position, (ulong)payload.Length);
            payload.CopyTo(destination.Slice(position));
            position += payload.Length;
        }

        private static bool TryReadFrame(
            ReadOnlySpan<byte> datagram,
            ref int position,
            int maximumPayload,
            out VoiceFrameFlags flags,
            out int offset,
            out int length)
        {
            flags = default;
            offset = 0;
            length = 0;
            if (position >= datagram.Length) return false;
            flags = (VoiceFrameFlags)datagram[position++];
            if ((flags & ~KnownFlags) != 0) return false;
            if (!TryReadVarUInt(datagram, ref position, out var size)) return false;
            if (size == 0 || size > (ulong)maximumPayload || size > (ulong)(datagram.Length - position)) return false;
            offset = position;
            length = (int)size;
            position += length;
            return true;
        }

        private static void WriteVarUInt(Span<byte> destination, ref int position, ulong value)
        {
            while (value >= 0x80)
            {
                destination[position++] = (byte)(value | 0x80);
                value >>= 7;
            }

            destination[position++] = (byte)value;
        }

        /// <summary>Reads a minimal LEB128 varint of at most ten bytes, with the same rules as the session protocol.</summary>
        private static bool TryReadVarUInt(ReadOnlySpan<byte> buffer, ref int position, out ulong value)
        {
            value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (position >= buffer.Length) return false;
                var next = buffer[position++];
                if (shift == 63 && next > 1) return false;
                value |= (ulong)(next & 0x7F) << shift;
                if ((next & 0x80) == 0) return next != 0 || shift == 0;
            }

            return false;
        }
    }
}
