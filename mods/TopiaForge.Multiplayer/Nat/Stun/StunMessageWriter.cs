using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TopiaForge.Multiplayer.NatTraversal.Stun
{
    /// <summary>
    /// Writes one STUN message into a caller's buffer. Begin, add attributes, then Finish, which appends
    /// MESSAGE-INTEGRITY and FINGERPRINT when asked and fixes the length. A writer is reused across messages, and
    /// writing a channel's worth of Send indications allocates nothing once the peer's address bytes are cached.
    /// </summary>
    internal sealed class StunMessageWriter
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private byte[] buffer = Array.Empty<byte>();
        private int position;

        /// <summary>Gets how many bytes the message holds so far.</summary>
        internal int Length => position;

        internal void Begin(byte[] target, int method, StunClass messageClass, StunTransactionId transactionId)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target.Length < StunMessage.HeaderSize) throw new ArgumentException("A STUN message needs at least 20 bytes.", nameof(target));
            buffer = target;
            var type = StunMessage.EncodeType(method, messageClass);
            buffer[0] = (byte)(type >> 8);
            buffer[1] = (byte)type;
            buffer[2] = 0;
            buffer[3] = 0;
            StunTransactionId.WriteUInt32(buffer, 4, StunMessage.MagicCookie);
            transactionId.Write(buffer, 8);
            position = StunMessage.HeaderSize;
        }

        /// <summary>Adds an address; the XOR attribute types are masked. An IPv4-mapped IPv6 address goes out as IPv4.</summary>
        internal void AddAddress(ushort type, IPEndPoint endpoint)
        {
            if (endpoint == null) throw new ArgumentNullException(nameof(endpoint));
            var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
            AddAddressBytes(type, address.GetAddressBytes(), endpoint.Port);
        }

        /// <summary>Adds an address from its raw bytes, 4 or 16 of them, which the caller may cache to avoid allocating.</summary>
        internal void AddAddressBytes(ushort type, byte[] address, int port)
        {
            if (address == null || (address.Length != 4 && address.Length != 16)) throw new ArgumentException("An address is 4 or 16 bytes.", nameof(address));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            var start = Header(type, 4 + address.Length);
            buffer[start] = 0;
            buffer[start + 1] = address.Length == 4 ? (byte)1 : (byte)2;
            var xor = StunMessage.IsXorAddress(type);
            var wirePort = xor ? port ^ (int)(StunMessage.MagicCookie >> 16) : port;
            buffer[start + 2] = (byte)(wirePort >> 8);
            buffer[start + 3] = (byte)wirePort;
            for (var index = 0; index < address.Length; index++) buffer[start + 4 + index] = xor ? (byte)(address[index] ^ buffer[4 + index]) : address[index];
            Pad(start, 4 + address.Length);
        }

        internal void AddUInt32(ushort type, uint value)
        {
            var start = Header(type, 4);
            StunTransactionId.WriteUInt32(buffer, start, value);
            position = start + 4;
        }

        internal void AddUInt64(ushort type, ulong value)
        {
            var start = Header(type, 8);
            StunTransactionId.WriteUInt32(buffer, start, (uint)(value >> 32));
            StunTransactionId.WriteUInt32(buffer, start + 4, (uint)value);
            position = start + 8;
        }

        /// <summary>Adds an attribute with no value, such as USE-CANDIDATE or DONT-FRAGMENT.</summary>
        internal void AddFlag(ushort type) => position = Header(type, 0);

        internal void AddText(ushort type, string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var encoded = Utf8.GetBytes(text);
            AddBytes(type, encoded, 0, encoded.Length);
        }

        internal void AddBytes(ushort type, byte[] value, int offset, int count)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (offset < 0 || count < 0 || offset > value.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            var start = Header(type, count);
            Buffer.BlockCopy(value, offset, buffer, start, count);
            Pad(start, count);
        }

        /// <summary>Adds CHANNEL-NUMBER: the number, then two reserved zero bytes.</summary>
        internal void AddChannelNumber(ushort channel)
        {
            var start = Header(StunAttribute.ChannelNumber, 4);
            buffer[start] = (byte)(channel >> 8);
            buffer[start + 1] = (byte)channel;
            buffer[start + 2] = 0;
            buffer[start + 3] = 0;
            position = start + 4;
        }

        /// <summary>Adds REQUESTED-TRANSPORT for a protocol number; 17 is UDP, the only transport TURN allocates here.</summary>
        internal void AddRequestedTransport(byte protocol)
        {
            var start = Header(StunAttribute.RequestedTransport, 4);
            buffer[start] = protocol;
            buffer[start + 1] = 0;
            buffer[start + 2] = 0;
            buffer[start + 3] = 0;
            position = start + 4;
        }

        /// <summary>Adds ERROR-CODE with an empty reason phrase: this layer never sends free text.</summary>
        internal void AddErrorCode(int code)
        {
            if (code < 300 || code > 699) throw new ArgumentOutOfRangeException(nameof(code));
            var start = Header(StunAttribute.ErrorCode, 4);
            buffer[start] = 0;
            buffer[start + 1] = 0;
            buffer[start + 2] = (byte)(code / 100);
            buffer[start + 3] = (byte)(code % 100);
            position = start + 4;
        }

        internal void AddUnknownAttributes(IReadOnlyList<ushort> types)
        {
            if (types == null) throw new ArgumentNullException(nameof(types));
            var count = Math.Min(types.Count, 16);
            var start = Header(StunAttribute.UnknownAttributes, 2 * count);
            for (var index = 0; index < count; index++)
            {
                buffer[start + (2 * index)] = (byte)(types[index] >> 8);
                buffer[start + (2 * index) + 1] = (byte)types[index];
            }

            Pad(start, 2 * count);
        }

        /// <summary>Appends MESSAGE-INTEGRITY when <paramref name="integrityKey"/> is given, then FINGERPRINT when asked; returns the length.</summary>
        internal int Finish(byte[]? integrityKey, bool fingerprint)
        {
            if (integrityKey != null)
            {
                Ensure(4 + StunMessage.IntegritySize);
                SetLength(position + 4 + StunMessage.IntegritySize);
                byte[] mac;
                using (var hmac = new HMACSHA1(integrityKey)) mac = hmac.ComputeHash(buffer, 0, position);
                WriteHeader(StunAttribute.MessageIntegrity, StunMessage.IntegritySize);
                Buffer.BlockCopy(mac, 0, buffer, position + 4, StunMessage.IntegritySize);
                position += 4 + StunMessage.IntegritySize;
            }

            if (fingerprint)
            {
                Ensure(8);
                SetLength(position + 8);
                var crc = StunCrc32.Compute(buffer, 0, position) ^ StunMessage.FingerprintXor;
                WriteHeader(StunAttribute.Fingerprint, 4);
                StunTransactionId.WriteUInt32(buffer, position + 4, crc);
                position += 8;
            }

            SetLength(position);
            return position;
        }

        /// <summary>Writes an attribute header for <paramref name="length"/> value bytes; returns where the value starts.</summary>
        private int Header(ushort type, int length)
        {
            if (length > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(length));
            Ensure(4 + ((length + 3) & ~3));
            WriteHeader(type, length);
            return position + 4;
        }

        private void WriteHeader(ushort type, int length)
        {
            buffer[position] = (byte)(type >> 8);
            buffer[position + 1] = (byte)type;
            buffer[position + 2] = (byte)(length >> 8);
            buffer[position + 3] = (byte)length;
        }

        private void Pad(int start, int length)
        {
            var end = start + length;
            var padded = start + ((length + 3) & ~3);
            for (var index = end; index < padded; index++) buffer[index] = 0;
            position = padded;
        }

        private void SetLength(int total)
        {
            var length = total - StunMessage.HeaderSize;
            buffer[2] = (byte)(length >> 8);
            buffer[3] = (byte)length;
        }

        private void Ensure(int more)
        {
            if (buffer.Length - position < more) throw new InvalidOperationException("The STUN message does not fit its buffer.");
        }
    }
}
