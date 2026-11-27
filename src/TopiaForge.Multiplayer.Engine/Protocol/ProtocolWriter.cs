using System;
using System.Text;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// Appends protocol primitives to a reusable buffer: LEB128 varints, length-prefixed bytes, and length-prefixed
    /// UTF-8 strings. One writer builds one frame or one message body at a time.
    /// </summary>
    internal sealed class ProtocolWriter
    {
        // Writing never throws on a lone surrogate from mod text; it becomes U+FFFD. Readers stay strict.
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);
        private byte[] buffer;
        private int length;

        internal ProtocolWriter(int capacity = 256)
        {
            buffer = new byte[Math.Max(16, capacity)];
        }

        internal int Length => length;

        internal byte[] Buffer => buffer;

        internal void Reset() => length = 0;

        internal void WriteByte(byte value)
        {
            Ensure(1);
            buffer[length++] = value;
        }

        internal void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        internal void WriteVarUInt(ulong value)
        {
            Ensure(10);
            while (value >= 0x80)
            {
                buffer[length++] = (byte)(value | 0x80);
                value >>= 7;
            }

            buffer[length++] = (byte)value;
        }

        internal void WriteBytes(byte[] value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            WriteVarUInt((ulong)value.Length);
            WriteRaw(value, 0, value.Length);
        }

        internal void WriteRaw(byte[] source, int offset, int count)
        {
            if (count == 0) return;
            Ensure(count);
            System.Buffer.BlockCopy(source, offset, buffer, length, count);
            length += count;
        }

        internal void WriteString(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var byteCount = Utf8.GetByteCount(value);
            WriteVarUInt((ulong)byteCount);
            Ensure(byteCount);
            length += Utf8.GetBytes(value, 0, value.Length, buffer, length);
        }

        internal void WriteOptionalString(string? value)
        {
            WriteBool(value != null);
            if (value != null) WriteString(value);
        }

        /// <summary>Appends one complete envelope: the type, the body length, and the body.</summary>
        internal void WriteEnvelope(byte type, ProtocolWriter body)
        {
            WriteVarUInt(type);
            WriteVarUInt((ulong)body.length);
            WriteRaw(body.buffer, 0, body.length);
        }

        internal ArraySegment<byte> AsSegment() => new ArraySegment<byte>(buffer, 0, length);

        internal byte[] ToArray()
        {
            var copy = new byte[length];
            System.Buffer.BlockCopy(buffer, 0, copy, 0, length);
            return copy;
        }

        private void Ensure(int additional)
        {
            var required = length + additional;
            if (required <= buffer.Length) return;
            var capacity = buffer.Length;
            while (capacity < required) capacity = capacity <= int.MaxValue / 2 ? capacity * 2 : int.MaxValue;
            Array.Resize(ref buffer, capacity);
        }
    }
}
