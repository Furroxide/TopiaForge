using System;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Transport.Packets
{
    /// <summary>The first byte of every datagram, chosen to coexist with STUN and TURN ChannelData (RFC 7983).</summary>
    internal static class PacketType
    {
        internal const byte Initiation = 0xE0;
        internal const byte Retry = 0xE1;
        internal const byte Response = 0xE2;
        internal const byte Completion = 0xE3;
        internal const byte InfoQuery = 0xE4;
        internal const byte InfoReply = 0xE5;
        internal const byte Protected = 0xE8;

        /// <summary>Gets whether a datagram belongs to STUN or TURN rather than to this transport.</summary>
        internal static bool IsForeign(byte first) => first <= 0x03 || (first >= 0x40 && first <= 0x4F);
    }

    /// <summary>Layouts of the handshake datagrams. Every reply is smaller than the Initiation it answers.</summary>
    internal static class HandshakePackets
    {
        internal const byte Version = 1;
        internal const int ConnectionIdSize = 8;
        internal const int CookieSize = 16;
        internal const int InitiationSize = TransportOptions.MaximumDatagramBytes;
        internal const int Message1Size = 32;
        internal const int RetrySize = 1 + ConnectionIdSize + CookieSize;

        /// <summary>Gets the Noise prologue that binds the initiator's connection id into the transcript.</summary>
        internal static byte[] Prologue(ulong initiatorConnectionId)
        {
            var label = System.Text.Encoding.ASCII.GetBytes("TopiaForge transport 1");
            var prologue = new byte[label.Length + ConnectionIdSize];
            Buffer.BlockCopy(label, 0, prologue, 0, label.Length);
            for (var index = 0; index < ConnectionIdSize; index++) prologue[label.Length + index] = (byte)(initiatorConnectionId >> (8 * index));
            return prologue;
        }

        internal static void WriteInitiation(PacketWriter writer, ulong initiatorConnectionId, byte[]? cookie, byte[] message1)
        {
            if (message1.Length != Message1Size) throw new ArgumentException("Noise message 1 carries no payload here.", nameof(message1));
            writer.WriteByte(PacketType.Initiation);
            writer.WriteByte(Version);
            writer.WriteUInt64(initiatorConnectionId);
            writer.WriteByte((byte)(cookie?.Length ?? 0));
            if (cookie != null) writer.WriteBytes(cookie, 0, cookie.Length);
            writer.WriteBytes(message1, 0, message1.Length);
            writer.WriteZeros(writer.Remaining);
        }

        /// <summary>Parses an Initiation. It must be exactly one full datagram, so it can never be answered with more.</summary>
        internal static bool TryReadInitiation(
            byte[] data,
            int offset,
            int count,
            out byte version,
            out ulong initiatorConnectionId,
            out byte[]? cookie,
            out byte[] message1)
        {
            version = 0;
            initiatorConnectionId = 0;
            cookie = null;
            message1 = Array.Empty<byte>();
            if (count != InitiationSize || data[offset] != PacketType.Initiation) return false;
            version = data[offset + 1];
            initiatorConnectionId = PacketWriter.ReadUInt64(data, offset + 2);
            var cookieLength = data[offset + 10];
            if (cookieLength != 0 && cookieLength != CookieSize) return false;
            var position = offset + 11;
            if (cookieLength == CookieSize)
            {
                cookie = Slice(data, position, CookieSize);
                position += CookieSize;
            }

            message1 = Slice(data, position, Message1Size);
            return true;
        }

        internal static void WriteRetry(PacketWriter writer, ulong initiatorConnectionId, byte[] cookie)
        {
            writer.WriteByte(PacketType.Retry);
            writer.WriteUInt64(initiatorConnectionId);
            writer.WriteBytes(cookie, 0, CookieSize);
        }

        internal static bool TryReadRetry(byte[] data, int offset, int count, out ulong initiatorConnectionId, out byte[] cookie)
        {
            initiatorConnectionId = 0;
            cookie = Array.Empty<byte>();
            if (count != RetrySize || data[offset] != PacketType.Retry) return false;
            initiatorConnectionId = PacketWriter.ReadUInt64(data, offset + 1);
            cookie = Slice(data, offset + 1 + ConnectionIdSize, CookieSize);
            return true;
        }

        /// <summary>Writes a Response or Completion: the type, the receiver's connection id, then the Noise message.</summary>
        internal static void WriteAddressed(PacketWriter writer, byte type, ulong receiverConnectionId, byte[] message)
        {
            writer.WriteByte(type);
            writer.WriteUInt64(receiverConnectionId);
            writer.WriteBytes(message, 0, message.Length);
        }

        internal static bool TryReadAddressed(byte[] data, int offset, int count, byte type, out ulong receiverConnectionId, out byte[] message)
        {
            receiverConnectionId = 0;
            message = Array.Empty<byte>();
            if (count <= 1 + ConnectionIdSize || data[offset] != type) return false;
            receiverConnectionId = PacketWriter.ReadUInt64(data, offset + 1);
            message = Slice(data, offset + 1 + ConnectionIdSize, count - 1 - ConnectionIdSize);
            return true;
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, offset, slice, 0, count);
            return slice;
        }
    }

    /// <summary>
    /// The encrypted payloads of handshake messages 2 and 3. The Response carries the responder's connection id and
    /// its initial stream window; the Completion carries the initiator's initial stream window. Both parse strictly.
    /// </summary>
    internal static class HandshakePayloads
    {
        internal static byte[] WriteResponse(ulong responderConnectionId, ulong window)
        {
            var payload = new byte[HandshakePackets.ConnectionIdSize + PacketWriter.VarUIntSize(window)];
            var writer = new PacketWriter();
            writer.Reset(payload, 0, payload.Length);
            writer.WriteUInt64(responderConnectionId);
            writer.WriteVarUInt(window);
            return payload;
        }

        internal static bool TryReadResponse(byte[] payload, out ulong responderConnectionId, out ulong window)
        {
            responderConnectionId = 0;
            window = 0;
            if (payload.Length <= HandshakePackets.ConnectionIdSize) return false;
            responderConnectionId = PacketWriter.ReadUInt64(payload, 0);
            return TryReadWindow(payload, HandshakePackets.ConnectionIdSize, out window);
        }

        internal static byte[] WriteCompletion(ulong window)
        {
            var payload = new byte[PacketWriter.VarUIntSize(window)];
            var writer = new PacketWriter();
            writer.Reset(payload, 0, payload.Length);
            writer.WriteVarUInt(window);
            return payload;
        }

        internal static bool TryReadCompletion(byte[] payload, out ulong window) => TryReadWindow(payload, 0, out window);

        private static bool TryReadWindow(byte[] payload, int offset, out ulong window)
        {
            window = 0;
            try
            {
                var reader = new ProtocolReader(payload, offset, payload.Length - offset);
                window = reader.ReadVarUInt();
                return reader.AtEnd;
            }
            catch (ProtocolException)
            {
                return false;
            }
        }
    }
}
