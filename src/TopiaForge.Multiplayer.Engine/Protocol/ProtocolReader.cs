using System;
using System.Text;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// Reads protocol primitives from untrusted bytes. Every read is bounded, and malformed input throws
    /// <see cref="ProtocolException"/> rather than an arbitrary runtime exception.
    /// </summary>
    internal sealed class ProtocolReader
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly byte[] buffer;
        private readonly int end;
        private int position;

        internal ProtocolReader(byte[] buffer)
            : this(buffer, 0, buffer?.Length ?? 0)
        {
        }

        internal ProtocolReader(byte[] buffer, int offset, int count)
        {
            this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            position = offset;
            end = offset + count;
        }

        internal bool AtEnd => position >= end;

        internal int Remaining => end - position;

        internal byte ReadByte()
        {
            if (position >= end) throw new ProtocolException("A message ended early.");
            return buffer[position++];
        }

        internal bool ReadBool()
        {
            var value = ReadByte();
            if (value > 1) throw new ProtocolException("A boolean field held a value other than 0 or 1.");
            return value == 1;
        }

        internal ulong ReadVarUInt()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var next = ReadByte();
                if (shift == 63 && next > 1) throw new ProtocolException("A varint overflowed 64 bits.");
                result |= (ulong)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    if (next == 0 && shift != 0) throw new ProtocolException("A varint used a non-minimal encoding.");
                    return result;
                }
            }

            throw new ProtocolException("A varint was longer than ten bytes.");
        }

        internal ulong ReadVarUInt(ulong maximum)
        {
            var value = ReadVarUInt();
            if (value > maximum) throw new ProtocolException("A numeric field exceeded its bound.");
            return value;
        }

        internal int ReadLength(int maximum)
        {
            var value = ReadVarUInt((ulong)maximum);
            if ((long)value > Remaining) throw new ProtocolException("A length prefix pointed past the end of the message.");
            return (int)value;
        }

        internal byte[] ReadBytes(int maximumLength)
        {
            var count = ReadLength(maximumLength);
            var copy = new byte[count];
            System.Buffer.BlockCopy(buffer, position, copy, 0, count);
            position += count;
            return copy;
        }

        internal string ReadString(int maximumCharacters)
        {
            var count = ReadLength(maximumCharacters * 4);
            string value;
            try
            {
                value = Utf8.GetString(buffer, position, count);
            }
            catch (DecoderFallbackException)
            {
                throw new ProtocolException("A string field was not valid UTF-8.");
            }

            position += count;
            if (value.Length > maximumCharacters) throw new ProtocolException("A string field exceeded its bound.");
            return value;
        }

        internal string? ReadOptionalString(int maximumCharacters) =>
            ReadBool() ? ReadString(maximumCharacters) : null;

        /// <summary>Reads a bounded opaque identity: non-blank, at most 128 characters, no control characters.</summary>
        internal string ReadIdentity()
        {
            var value = ReadString(ProtocolLimits.IdentityCharacters);
            if (!ProtocolLimits.IsValidIdentity(value)) throw new ProtocolException("An identity field was blank or held control characters.");
            return value;
        }

        internal string? ReadOptionalIdentity() => ReadBool() ? ReadIdentity() : null;

        /// <summary>Returns a reader over the next <paramref name="count"/> bytes and skips past them.</summary>
        internal ProtocolReader Slice(int count)
        {
            if (count < 0 || count > Remaining) throw new ProtocolException("A message body pointed past the end of the frame.");
            var slice = new ProtocolReader(buffer, position, count);
            position += count;
            return slice;
        }

        internal void Skip(int count)
        {
            if (count < 0 || count > Remaining) throw new ProtocolException("A skipped field pointed past the end of the message.");
            position += count;
        }
    }

    /// <summary>A peer sent bytes that violate the session protocol.</summary>
    internal sealed class ProtocolException : Exception
    {
        internal ProtocolException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Bounds every decoder applies to untrusted input.</summary>
    internal static class ProtocolLimits
    {
        internal const int IdentityCharacters = 128;
        internal const int MessageCharacters = 512;
        internal const int MaximumFrameBytes = (1024 * 1024) + (64 * 1024);
        internal const int MaximumPayloadBytes = 1024 * 1024;
        internal const int MaximumOpaqueBytes = 64 * 1024;
        internal const int MaximumContracts = 256;
        internal const int MaximumParticipants = 256;
        internal const int MaximumInternedStrings = 4096;

        internal static bool IsValidIdentity(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value!.Length > IdentityCharacters) return false;
            foreach (var character in value)
            {
                if (char.IsControl(character)) return false;
            }

            return true;
        }

        internal static string BoundMessage(string? message)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;
            if (message!.Length <= MessageCharacters) return message;
            var cut = char.IsHighSurrogate(message[MessageCharacters - 1]) ? MessageCharacters - 1 : MessageCharacters;
            return message.Substring(0, cut);
        }
    }
}
