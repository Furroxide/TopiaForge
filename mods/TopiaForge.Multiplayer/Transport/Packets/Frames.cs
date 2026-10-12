using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Transport.Packets
{
    internal static class FrameType
    {
        internal const byte Padding = 0x00;
        internal const byte Ping = 0x01;
        internal const byte Ack = 0x02;
        internal const byte Close = 0x03;
        internal const byte Stream = 0x10;
        internal const byte Window = 0x11;
        internal const byte Datagram = 0x20;
        internal const byte PathChallenge = 0x30;
        internal const byte PathResponse = 0x31;

        /// <summary>Gets the bytes a PATH_CHALLENGE or PATH_RESPONSE carries.</summary>
        internal const int PathDataSize = 8;

        /// <summary>
        /// Gets whether a frame asks the peer for an acknowledgement. Path frames do not: validation retries on its own
        /// timer, and its packets travel on a path the acknowledgements may never use.
        /// </summary>
        internal static bool IsAckEliciting(byte type) => type != Ack && type != Close && !IsProbing(type);

        /// <summary>Gets whether a frame only probes a path. A packet of nothing else never moves a connection.</summary>
        internal static bool IsProbing(byte type) => type == Padding || type == PathChallenge || type == PathResponse;
    }

    /// <summary>Why a connection closed. Codes travel in CLOSE frames; no frame carries free text.</summary>
    internal enum CloseCode
    {
        Normal = 0,
        ProtocolViolation = 1,
        FlowControl = 2,
        Overloaded = 3,
        IdleTimeout = 4,
        HandshakeFailed = 5
    }

    /// <summary>A packet-number range, inclusive at both ends.</summary>
    internal readonly struct PacketRange
    {
        internal PacketRange(ulong smallest, ulong largest)
        {
            Smallest = smallest;
            Largest = largest;
        }

        internal ulong Smallest { get; }

        internal ulong Largest { get; }

        internal bool Contains(ulong number) => number >= Smallest && number <= Largest;
    }

    /// <summary>Writes frames. Each method assumes the caller checked the size it needs.</summary>
    internal static class FrameWriter
    {
        internal const int AckDelayExponent = 3;

        internal static int AckSize(IReadOnlyList<PacketRange> ranges, ulong ackDelayMicros)
        {
            var size = 1 + PacketWriter.VarUIntSize(ranges[0].Largest) + PacketWriter.VarUIntSize(ackDelayMicros >> AckDelayExponent) +
                PacketWriter.VarUIntSize((ulong)(ranges.Count - 1)) + PacketWriter.VarUIntSize(ranges[0].Largest - ranges[0].Smallest);
            for (var index = 1; index < ranges.Count; index++)
            {
                size += PacketWriter.VarUIntSize(ranges[index - 1].Smallest - ranges[index].Largest - 2);
                size += PacketWriter.VarUIntSize(ranges[index].Largest - ranges[index].Smallest);
            }

            return size;
        }

        /// <summary>Writes an ACK for <paramref name="ranges"/>, which run from newest to oldest and never touch.</summary>
        internal static void WriteAck(PacketWriter writer, IReadOnlyList<PacketRange> ranges, ulong ackDelayMicros)
        {
            writer.WriteByte(FrameType.Ack);
            writer.WriteVarUInt(ranges[0].Largest);
            writer.WriteVarUInt(ackDelayMicros >> AckDelayExponent);
            writer.WriteVarUInt((ulong)(ranges.Count - 1));
            writer.WriteVarUInt(ranges[0].Largest - ranges[0].Smallest);
            for (var index = 1; index < ranges.Count; index++)
            {
                writer.WriteVarUInt(ranges[index - 1].Smallest - ranges[index].Largest - 2);
                writer.WriteVarUInt(ranges[index].Largest - ranges[index].Smallest);
            }
        }

        internal static void WriteClose(PacketWriter writer, CloseCode code)
        {
            writer.WriteByte(FrameType.Close);
            writer.WriteVarUInt((ulong)code);
        }

        internal static int StreamHeaderSize(ulong offset, int length) =>
            1 + PacketWriter.VarUIntSize(offset) + PacketWriter.VarUIntSize((ulong)length);

        internal static void WriteStreamHeader(PacketWriter writer, ulong offset, int length)
        {
            writer.WriteByte(FrameType.Stream);
            writer.WriteVarUInt(offset);
            writer.WriteVarUInt((ulong)length);
        }

        internal static int WindowSize(ulong maximumOffset) => 1 + PacketWriter.VarUIntSize(maximumOffset);

        internal static void WriteWindow(PacketWriter writer, ulong maximumOffset)
        {
            writer.WriteByte(FrameType.Window);
            writer.WriteVarUInt(maximumOffset);
        }

        internal static int DatagramSize(ulong sequence, int length) =>
            2 + PacketWriter.VarUIntSize(sequence) + PacketWriter.VarUIntSize((ulong)length) + length;

        internal static void WriteDatagram(PacketWriter writer, byte channel, ulong sequence, byte[] payload)
        {
            writer.WriteByte(FrameType.Datagram);
            writer.WriteByte(channel);
            writer.WriteVarUInt(sequence);
            writer.WriteVarUInt((ulong)payload.Length);
            writer.WriteBytes(payload, 0, payload.Length);
        }

        /// <summary>Writes a PATH_CHALLENGE or PATH_RESPONSE with its eight bytes.</summary>
        internal static void WritePathFrame(PacketWriter writer, byte type, byte[] data)
        {
            writer.WriteByte(type);
            writer.WriteBytes(data, 0, FrameType.PathDataSize);
        }
    }

    /// <summary>What a connection does with each frame of an authenticated packet.</summary>
    internal interface IFrameSink
    {
        void OnPing();

        void OnAck(List<PacketRange> ranges, ulong ackDelayMicros);

        void OnClose(CloseCode code);

        /// <summary>Takes stream bytes; returns false when they cannot be buffered now, so the packet goes unacknowledged.</summary>
        bool OnStream(ulong offset, byte[] data);

        void OnWindow(ulong maximumOffset);

        void OnDatagram(byte channel, ulong sequence, byte[] payload);

        void OnPathChallenge(byte[] data);

        void OnPathResponse(byte[] data);
    }

    /// <summary>What reading a packet's frames found.</summary>
    [Flags]
    internal enum FrameRead
    {
        None = 0,

        /// <summary>A frame asked for an acknowledgement.</summary>
        AckEliciting = 1,

        /// <summary>A frame other than PADDING, PATH_CHALLENGE or PATH_RESPONSE: the peer uses this path for real.</summary>
        NonProbing = 2,

        /// <summary>The sink refused stream bytes, so the packet must go unacknowledged and the rest went unread.</summary>
        Refused = 4
    }

    /// <summary>
    /// Parses the frames of an authenticated packet. Anything malformed or unknown throws
    /// <see cref="ProtocolException"/>, which the connection treats as a protocol violation.
    /// </summary>
    internal static class FrameReader
    {
        private const int MaximumRanges = 32;

        /// <summary>Delivers every frame to <paramref name="sink"/> and reports what the packet held.</summary>
        internal static FrameRead Read(byte[] buffer, int offset, int count, IFrameSink sink, List<PacketRange> scratch)
        {
            var reader = new ProtocolReader(buffer, offset, count);
            if (reader.AtEnd) throw new ProtocolException("A protected packet carried no frames.");
            var result = FrameRead.None;
            while (!reader.AtEnd)
            {
                var type = reader.ReadByte();
                if (FrameType.IsAckEliciting(type)) result |= FrameRead.AckEliciting;
                if (!FrameType.IsProbing(type)) result |= FrameRead.NonProbing;
                switch (type)
                {
                    case FrameType.Padding:
                        break;
                    case FrameType.PathChallenge:
                        sink.OnPathChallenge(ReadPathData(reader));
                        break;
                    case FrameType.PathResponse:
                        sink.OnPathResponse(ReadPathData(reader));
                        break;
                    case FrameType.Ping:
                        sink.OnPing();
                        break;
                    case FrameType.Ack:
                        ReadAck(reader, scratch, out var delay);
                        sink.OnAck(scratch, delay);
                        break;
                    case FrameType.Close:
                        var code = reader.ReadVarUInt(int.MaxValue);
                        sink.OnClose(Enum.IsDefined(typeof(CloseCode), (int)code) ? (CloseCode)(int)code : CloseCode.ProtocolViolation);
                        break;
                    case FrameType.Stream:
                        var streamOffset = reader.ReadVarUInt();
                        var data = reader.ReadBytes(TransportOptions.MaximumDatagramBytes);
                        if (streamOffset > ulong.MaxValue - (ulong)data.Length) throw new ProtocolException("A stream frame overflowed the stream.");
                        if (!sink.OnStream(streamOffset, data)) return result | FrameRead.Refused;
                        break;
                    case FrameType.Window:
                        sink.OnWindow(reader.ReadVarUInt());
                        break;
                    case FrameType.Datagram:
                        var channel = reader.ReadByte();
                        var sequence = reader.ReadVarUInt();
                        sink.OnDatagram(channel, sequence, reader.ReadBytes(TransportOptions.MaximumDatagramBytes));
                        break;
                    default:
                        throw new ProtocolException("A protected packet carried an unknown frame type.");
                }
            }

            return result;
        }

        private static byte[] ReadPathData(ProtocolReader reader)
        {
            var data = new byte[FrameType.PathDataSize];
            for (var index = 0; index < data.Length; index++) data[index] = reader.ReadByte();
            return data;
        }

        private static void ReadAck(ProtocolReader reader, List<PacketRange> ranges, out ulong delay)
        {
            ranges.Clear();
            var largest = reader.ReadVarUInt();
            delay = reader.ReadVarUInt(ulong.MaxValue >> FrameWriter.AckDelayExponent) << FrameWriter.AckDelayExponent;
            var additional = reader.ReadVarUInt(MaximumRanges - 1);
            var first = reader.ReadVarUInt(largest);
            var smallest = largest - first;
            ranges.Add(new PacketRange(smallest, largest));
            for (var index = 0UL; index < additional; index++)
            {
                var gap = reader.ReadVarUInt();
                if (gap >= smallest || smallest - gap < 2) throw new ProtocolException("An ACK range ran below packet zero.");
                var nextLargest = smallest - gap - 2;
                var length = reader.ReadVarUInt(nextLargest);
                smallest = nextLargest - length;
                ranges.Add(new PacketRange(smallest, nextLargest));
            }
        }
    }
}
