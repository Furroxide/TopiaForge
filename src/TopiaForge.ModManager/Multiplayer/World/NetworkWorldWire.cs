using System;
using System.Text;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The messages world replication sends on the <c>WorldReplication</c> system channel. Every message starts with the
    /// version byte and its type. Types 1 to 31 go from the host to its players, 32 to 63 from a player to its host, and
    /// 64 to 127 are reserved for authority routing (WS3.7), which shares the channel. A reader skips a type it does not
    /// know, so a minor version can add one.
    /// </summary>
    internal static class WorldMessages
    {
        internal const byte Version = 1;

        /// <summary>
        /// Host: a baseline starts. <c>[baseline varuint][expected entries varuint][host time varuint][world instance u64]</c>;
        /// the world instance is never zero, and a new one means another world, as after the host's process restarted.
        /// </summary>
        internal const byte BaselineBegin = 1;

        /// <summary>Host: part of a baseline. <c>[baseline varuint][count varuint][entries]</c>.</summary>
        internal const byte BaselineEntries = 2;

        /// <summary>Host: a baseline is complete. <c>[baseline varuint]</c>.</summary>
        internal const byte BaselineEnd = 3;

        /// <summary>Host: entities that appeared, or whose description changed. <c>[count varuint][entries]</c>.</summary>
        internal const byte Spawn = 4;

        /// <summary>Host: entities that ended. <c>[count varuint][id varuint]...</c>.</summary>
        internal const byte Despawn = 5;

        /// <summary>Host: poses at one host time. <c>[host time varuint][count varuint]([id varuint][transform])...</c>.</summary>
        internal const byte Snapshot = 6;

        /// <summary>Host: scene objects that are gone, unbound or not. <c>[count varuint][key u64]...</c>.</summary>
        internal const byte SceneRemoved = 7;

        /// <summary>Player: asks for a baseline. <c>[reason byte]</c>: 0 when a stream starts, 1 to resynchronize.</summary>
        internal const byte BaselineRequest = 32;

        /// <summary>
        /// Player: how much of a baseline has arrived. <c>[baseline varuint][chunks read varuint]</c>, sent at most once a frame
        /// while chunks arrive, so the host keeps only a few chunks in flight.
        /// </summary>
        internal const byte BaselineProgress = 33;

        /// <summary>The first type authority routing (WS3.7) may claim.</summary>
        internal const byte FirstRoutingType = 64;

        /// <summary>The last type authority routing (WS3.7) may claim.</summary>
        internal const byte LastRoutingType = 127;

        /// <summary>The largest message world replication writes: below the channel's 32 KiB, so one entry always fits.</summary>
        internal const int MaximumMessageBytes = 24 * 1024;

        /// <summary>The most items one message lists: entries, ids, keys or poses.</summary>
        internal const int MaximumItems = 4096;

        /// <summary>The most entries a baseline may announce.</summary>
        internal const int MaximumBaselineEntries = 1 << 20;

        /// <summary>The largest host time a message carries: the interpolation buffer's bound.</summary>
        internal const long MaximumTimeMicros = SnapshotInterpolationBuffer.MaximumTimeMicros - 1;
    }

    /// <summary>A fixed buffer world replication writes one message into. It never allocates after construction.</summary>
    internal sealed class WorldWireWriter
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly byte[] buffer;
        private readonly NetBitWriter record = new NetBitWriter(32);
        private int length;

        internal WorldWireWriter(int capacity = WorldMessages.MaximumMessageBytes)
        {
            if (capacity < 16 || capacity > Mods.Internal.InternalSystemLimits.MaximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(capacity));
            buffer = new byte[capacity];
        }

        internal int Length => length;

        internal int Capacity => buffer.Length;

        internal ArraySegment<byte> Segment => new ArraySegment<byte>(buffer, 0, length);

        /// <summary>Starts a message of <paramref name="type"/>, dropping whatever the writer held.</summary>
        internal void Begin(byte type)
        {
            length = 0;
            WriteByte(WorldMessages.Version);
            WriteByte(type);
        }

        internal void Reset() => length = 0;

        /// <summary>Whether <paramref name="bytes"/> more fit.</summary>
        internal bool Fits(int bytes) => bytes >= 0 && length + bytes <= buffer.Length;

        /// <summary>Appends what another writer holds, when it fits; nothing is written otherwise.</summary>
        internal bool TryAppend(WorldWireWriter other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            if (!Fits(other.length)) return false;
            Buffer.BlockCopy(other.buffer, 0, buffer, length, other.length);
            length += other.length;
            return true;
        }

        internal void WriteByte(byte value)
        {
            Require(1);
            buffer[length++] = value;
        }

        /// <summary>Writes an unsigned LEB128 varint in its shortest form.</summary>
        internal void WriteVarUInt(ulong value)
        {
            do
            {
                var next = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) next |= 0x80;
                WriteByte(next);
            }
            while (value != 0);
        }

        internal void WriteUInt64(ulong value)
        {
            Require(8);
            for (var index = 0; index < 8; index++) buffer[length++] = (byte)(value >> (8 * index));
        }

        internal void WriteSingle(float value)
        {
            var bits = (uint)BitConverter.SingleToInt32Bits(value);
            Require(4);
            for (var index = 0; index < 4; index++) buffer[length++] = (byte)(bits >> (8 * index));
        }

        /// <summary>Writes a string as its UTF-8 byte length and bytes.</summary>
        internal void WriteString(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var count = Utf8.GetByteCount(value);
            WriteVarUInt((ulong)count);
            Require(count);
            length += Utf8.GetBytes(value, 0, value.Length, buffer, length);
        }

        internal void WriteBytes(ReadOnlySpan<byte> value)
        {
            WriteVarUInt((ulong)value.Length);
            Require(value.Length);
            value.CopyTo(new Span<byte>(buffer, length, value.Length));
            length += value.Length;
        }

        /// <summary>
        /// Writes a pose as the replication model's transform record: a flags byte (bit 0, velocity), then the 99-bit record
        /// in 13 bytes or the 132-bit one in 17, spare bits zero.
        /// </summary>
        internal void WriteTransform(in NetPose pose)
        {
            record.Reset();
            NetQuantization.WriteTransform(
                record,
                NetQuantization.QuantizePosition(pose.Position),
                NetQuantization.QuantizeRotation(pose.Rotation),
                pose.HasVelocity ? NetQuantization.QuantizeVelocity(pose.Velocity) : (NetQuantizedVelocity?)null);
            WriteByte(pose.HasVelocity ? (byte)1 : (byte)0);
            var bytes = record.AsSegment();
            Require(bytes.Count);
            Buffer.BlockCopy(bytes.Array!, bytes.Offset, buffer, length, bytes.Count);
            length += bytes.Count;
        }

        private void Require(int bytes)
        {
            if (length + bytes > buffer.Length) throw new InvalidOperationException("The world message is full.");
        }
    }

    /// <summary>
    /// Reads what <see cref="WorldWireWriter"/> wrote, from a peer, so nothing is trusted: every read checks its bounds
    /// and reports failure instead of throwing, varints must be minimal, strings valid UTF-8, and a message must end
    /// exactly where its last field does.
    /// </summary>
    internal sealed class WorldWireReader
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly NetBitReader record = new NetBitReader();
        private byte[] data = Array.Empty<byte>();
        private int position;
        private int end;

        internal int Position => position;

        internal int Remaining => end - position;

        internal bool AtEnd => position == end;

        internal void Reset(byte[] payload)
        {
            data = payload ?? throw new ArgumentNullException(nameof(payload));
            position = 0;
            end = payload.Length;
        }

        /// <summary>Reads the version and type that start every message.</summary>
        internal bool TryReadHeader(out byte type)
        {
            type = 0;
            return TryReadByte(out var version) && version == WorldMessages.Version && TryReadByte(out type);
        }

        internal bool TryReadByte(out byte value)
        {
            if (position >= end)
            {
                value = 0;
                return false;
            }

            value = data[position++];
            return true;
        }

        /// <summary>Reads a minimal LEB128 varint of at most <paramref name="maximum"/>.</summary>
        internal bool TryReadVarUInt(ulong maximum, out ulong value)
        {
            value = 0;
            var start = position;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (!TryReadByte(out var next)) break;
                var part = (ulong)(next & 0x7F);
                if (shift == 63 && part > 1) break;
                value |= part << shift;
                if ((next & 0x80) != 0) continue;

                // A trailing zero group would give the value a second spelling.
                if (next == 0 && position - start > 1) break;
                if (value > maximum) break;
                return true;
            }

            position = start;
            value = 0;
            return false;
        }

        internal bool TryReadUInt32(uint maximum, out uint value)
        {
            var read = TryReadVarUInt(maximum, out var wide);
            value = (uint)wide;
            return read;
        }

        internal bool TryReadInt32(int maximum, out int value)
        {
            var read = TryReadVarUInt((ulong)Math.Max(0, maximum), out var wide);
            value = (int)wide;
            return read;
        }

        internal bool TryReadUInt64(out ulong value)
        {
            value = 0;
            if (end - position < 8) return false;
            for (var index = 0; index < 8; index++) value |= (ulong)data[position + index] << (8 * index);
            position += 8;
            return true;
        }

        internal bool TryReadSingle(out float value)
        {
            value = 0f;
            if (end - position < 4) return false;
            var bits = 0u;
            for (var index = 0; index < 4; index++) bits |= (uint)data[position + index] << (8 * index);
            position += 4;
            value = BitConverter.Int32BitsToSingle((int)bits);
            return true;
        }

        /// <summary>Reads a string of at most <paramref name="maximumBytes"/> UTF-8 bytes; malformed UTF-8 is refused.</summary>
        internal bool TryReadString(int maximumBytes, out string value)
        {
            value = string.Empty;
            var start = position;
            if (!TryReadInt32(maximumBytes, out var count) || end - position < count)
            {
                position = start;
                return false;
            }

            try
            {
                value = Utf8.GetString(data, position, count);
            }
            catch (ArgumentException)
            {
                position = start;
                return false;
            }

            position += count;
            return true;
        }

        /// <summary>Reads a byte string of at most <paramref name="maximumBytes"/>, copied out.</summary>
        internal bool TryReadBytes(int maximumBytes, out byte[] value)
        {
            value = Array.Empty<byte>();
            var start = position;
            if (!TryReadInt32(maximumBytes, out var count) || end - position < count)
            {
                position = start;
                return false;
            }

            if (count > 0)
            {
                value = new byte[count];
                Buffer.BlockCopy(data, position, value, 0, count);
            }

            position += count;
            return true;
        }

        /// <summary>Reads a transform record; its flags, its length and its spare bits must all be exact.</summary>
        internal bool TryReadTransform(out NetPose pose)
        {
            pose = default;
            var start = position;
            if (!TryReadByte(out var flags) || flags > 1)
            {
                position = start;
                return false;
            }

            var bytes = flags == 1 ? 17 : 13;
            if (end - position < bytes)
            {
                position = start;
                return false;
            }

            record.Reset(new ArraySegment<byte>(data, position, bytes));
            if (!NetQuantization.TryReadTransform(record, out var quantized, out var rotation, out var velocity) ||
                velocity.HasValue != (flags == 1) || !record.IsAtCanonicalEnd)
            {
                position = start;
                return false;
            }

            position += bytes;
            var at = NetQuantization.DequantizePosition(quantized);
            var turned = NetQuantization.DequantizeRotation(rotation);
            pose = velocity.HasValue ? new NetPose(at, turned, NetQuantization.DequantizeVelocity(velocity.Value)) : new NetPose(at, turned);
            return true;
        }
    }
}
