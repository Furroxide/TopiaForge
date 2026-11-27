using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace TopiaForge.Multiplayer.NatTraversal.Stun
{
    /// <summary>
    /// One parsed STUN message (RFC 8489). Parsing copies the datagram and checks the header, every attribute's bounds
    /// and padding, and FINGERPRINT when present. Attributes that follow MESSAGE-INTEGRITY are dropped, except
    /// MESSAGE-INTEGRITY-SHA256 and FINGERPRINT, so nothing unauthenticated can ride behind an integrity check. Nothing a
    /// peer or server sends makes parsing or any accessor throw; a malformed value reads as absent.
    /// </summary>
    internal sealed class StunMessage
    {
        internal const int HeaderSize = 20;
        internal const uint MagicCookie = 0x2112A442;

        /// <summary>The largest message accepted: a Data indication carrying a full transport datagram fits easily.</summary>
        internal const int MaximumBytes = 1500;

        internal const int MaximumAttributes = 32;

        /// <summary>The most bytes a USERNAME, REALM or NONCE may decode to (RFC 8489, section 14).</summary>
        internal const int MaximumTextBytes = 763;

        internal const uint FingerprintXor = 0x5354554E;
        internal const int IntegritySize = 20;

        private readonly byte[] bytes;
        private readonly List<Entry> entries;
        private readonly List<ushort> unknownRequired;
        private readonly int integrityOffset;

        private StunMessage(byte[] bytes, int method, StunClass messageClass, List<Entry> entries, List<ushort> unknownRequired, int integrityOffset, bool fingerprint)
        {
            this.bytes = bytes;
            Method = method;
            Class = messageClass;
            TransactionId = StunTransactionId.Read(bytes, 8);
            this.entries = entries;
            this.unknownRequired = unknownRequired;
            this.integrityOffset = integrityOffset;
            HasFingerprint = fingerprint;
        }

        internal int Method { get; }

        internal StunClass Class { get; }

        internal StunTransactionId TransactionId { get; }

        internal int Length => bytes.Length;

        internal bool HasIntegrity => integrityOffset >= 0;

        internal bool HasFingerprint { get; }

        /// <summary>Gets the comprehension-required attribute types this layer does not know, in order of appearance.</summary>
        internal IReadOnlyList<ushort> UnknownRequired => unknownRequired;

        /// <summary>Gets the message's bytes, for reading a DATA value in place. Never hand them to anything above the NAT layer.</summary>
        internal byte[] Bytes => bytes;

        /// <summary>Decodes the method and class from a message type, or false when the type is not a STUN type.</summary>
        internal static bool TryDecodeType(int type, out int method, out StunClass messageClass)
        {
            method = (type & 0x000F) | ((type & 0x00E0) >> 1) | ((type & 0x3E00) >> 2);
            messageClass = (StunClass)(((type >> 4) & 1) | ((type >> 7) & 2));
            return (type & 0xC000) == 0;
        }

        internal static int EncodeType(int method, StunClass messageClass)
        {
            var bits = (int)messageClass;
            return (method & 0x000F) | ((method & 0x0070) << 1) | ((method & 0x0F80) << 2) | ((bits & 1) << 4) | ((bits & 2) << 7);
        }

        /// <summary>Parses <paramref name="count"/> bytes at <paramref name="offset"/>; false for anything that is not a valid message.</summary>
        internal static bool TryParse(byte[]? buffer, int offset, int count, out StunMessage message)
        {
            message = null!;
            if (buffer == null || offset < 0 || count < HeaderSize || count > MaximumBytes || offset > buffer.Length - count) return false;
            var type = (buffer[offset] << 8) | buffer[offset + 1];
            var length = (buffer[offset + 2] << 8) | buffer[offset + 3];
            if (!TryDecodeType(type, out var method, out var messageClass)) return false;
            if (length != count - HeaderSize || (length & 3) != 0) return false;
            if (StunTransactionId.ReadUInt32(buffer, offset + 4) != MagicCookie) return false;
            if (!IsValidPairing(method, messageClass)) return false;

            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            var entries = new List<Entry>(8);
            var unknown = new List<ushort>(0);
            var integrity = -1;
            var fingerprint = false;
            var afterIntegrity = false;
            var afterSha256 = false;
            var position = HeaderSize;
            while (position < count)
            {
                if (fingerprint || position + 4 > count) return false;
                var attribute = (ushort)((copy[position] << 8) | copy[position + 1]);
                var valueLength = (copy[position + 2] << 8) | copy[position + 3];
                var padded = (valueLength + 3) & ~3;
                if (padded > count - position - 4) return false;
                if (attribute == StunAttribute.Fingerprint)
                {
                    if (valueLength != 4) return false;
                    var expected = StunCrc32.Compute(copy, 0, position) ^ FingerprintXor;
                    if (StunTransactionId.ReadUInt32(copy, position + 4) != expected) return false;
                    fingerprint = true;
                }
                else if (afterSha256 || (afterIntegrity && attribute != StunAttribute.MessageIntegritySha256))
                {
                    // Anything behind an integrity check is outside it, so it is ignored as RFC 8489 requires.
                }
                else if (attribute == StunAttribute.MessageIntegrity)
                {
                    if (valueLength != IntegritySize) return false;
                    integrity = position;
                    afterIntegrity = true;
                }
                else if (attribute == StunAttribute.MessageIntegritySha256)
                {
                    if (valueLength < 16 || valueLength > 32 || (valueLength & 3) != 0) return false;
                    afterSha256 = true;
                }
                else
                {
                    if (entries.Count >= MaximumAttributes) return false;
                    entries.Add(new Entry(attribute, position + 4, valueLength));
                    if (StunAttribute.IsUnknownRequired(attribute)) unknown.Add(attribute);
                }

                position += 4 + padded;
            }

            message = new StunMessage(copy, method, messageClass, entries, unknown, integrity, fingerprint);
            return true;
        }

        /// <summary>Gets whether a datagram's first two bytes make it a Data indication, the type a TURN server relays data in.</summary>
        internal static bool IsDataIndication(byte[] buffer, int offset, int count) =>
            buffer != null && offset >= 0 && count >= HeaderSize && offset <= buffer.Length - count && buffer[offset] == 0x00 && buffer[offset + 1] == 0x17;

        /// <summary>
        /// Reads a Data indication where it lies, copying and allocating nothing: the XOR-PEER-ADDRESS, unmasked into
        /// <paramref name="address"/> (16 bytes; an IPv4 address fills the first four), its port, and where the DATA
        /// value lies in <paramref name="buffer"/>. It checks what <see cref="TryParse"/> checks: the header, every
        /// attribute's bounds and padding, at most 32 attributes and 1,500 bytes, and FINGERPRINT when present. An
        /// indication with an unknown comprehension-required attribute is refused, as RFC 8489 discards it. Never throws.
        /// </summary>
        internal static bool TryReadDataIndication(byte[]? buffer, int offset, int count, byte[] address, out int addressSize, out int port, out int dataOffset, out int dataLength)
        {
            addressSize = 0;
            port = 0;
            dataOffset = 0;
            dataLength = 0;
            if (address == null || address.Length < 16 || !IsDataIndication(buffer!, offset, count) || count > MaximumBytes) return false;
            var length = (buffer![offset + 2] << 8) | buffer[offset + 3];
            if (length != count - HeaderSize || (length & 3) != 0 || StunTransactionId.ReadUInt32(buffer, offset + 4) != MagicCookie) return false;
            var end = offset + count;
            var position = offset + HeaderSize;
            var attributes = 0;
            var fingerprint = false;
            var afterIntegrity = false;
            var peerFound = false;
            var dataFound = false;
            while (position < end)
            {
                if (fingerprint || position + 4 > end) return false;
                var attribute = (ushort)((buffer[position] << 8) | buffer[position + 1]);
                var valueLength = (buffer[position + 2] << 8) | buffer[position + 3];
                var padded = (valueLength + 3) & ~3;
                if (padded > end - position - 4) return false;
                if (attribute == StunAttribute.Fingerprint)
                {
                    if (valueLength != 4) return false;
                    var expected = StunCrc32.Compute(buffer, offset, position - offset) ^ FingerprintXor;
                    if (StunTransactionId.ReadUInt32(buffer, position + 4) != expected) return false;
                    fingerprint = true;
                }
                else if (attribute == StunAttribute.MessageIntegrity || attribute == StunAttribute.MessageIntegritySha256)
                {
                    // An indication from the server carries no integrity; whatever follows one is outside it and ignored.
                    var valid = attribute == StunAttribute.MessageIntegrity ? valueLength == IntegritySize : valueLength >= 16 && valueLength <= 32 && (valueLength & 3) == 0;
                    if (!valid) return false;
                    afterIntegrity = true;
                }
                else if (!afterIntegrity)
                {
                    if (++attributes > MaximumAttributes || StunAttribute.IsUnknownRequired(attribute)) return false;
                    if (attribute == StunAttribute.XorPeerAddress && !peerFound)
                    {
                        if (!TryUnmaskAddress(buffer, offset, position + 4, valueLength, address, out addressSize, out port)) return false;
                        peerFound = true;
                    }
                    else if (attribute == StunAttribute.Data && !dataFound)
                    {
                        dataOffset = position + 4;
                        dataLength = valueLength;
                        dataFound = true;
                    }
                }

                position += 4 + padded;
            }

            return peerFound && dataFound && port != 0;
        }

        /// <summary>Checks a short-term or long-term MESSAGE-INTEGRITY (HMAC-SHA1) in constant time.</summary>
        internal bool VerifyIntegrity(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (integrityOffset < 0) return false;
            var originalHigh = bytes[2];
            var originalLow = bytes[3];
            var covered = integrityOffset + 4 + IntegritySize - HeaderSize;
            bytes[2] = (byte)(covered >> 8);
            bytes[3] = (byte)covered;
            byte[] mac;
            try
            {
                using (var hmac = new HMACSHA1(key)) mac = hmac.ComputeHash(bytes, 0, integrityOffset);
            }
            finally
            {
                bytes[2] = originalHigh;
                bytes[3] = originalLow;
            }

            var difference = 0;
            for (var index = 0; index < IntegritySize; index++) difference |= mac[index] ^ bytes[integrityOffset + 4 + index];
            return difference == 0;
        }

        internal bool Has(ushort type) => IndexOf(type) >= 0;

        /// <summary>Finds the first occurrence of <paramref name="type"/>: the offset of its value in <see cref="Bytes"/> and its length.</summary>
        internal bool TryGetValue(ushort type, out int offset, out int length)
        {
            var index = IndexOf(type);
            if (index < 0)
            {
                offset = 0;
                length = 0;
                return false;
            }

            offset = entries[index].Offset;
            length = entries[index].Length;
            return true;
        }

        /// <summary>Reads an address attribute; XOR forms are unmasked. An unknown family, a bad length or port 0 reads as absent.</summary>
        internal bool TryGetAddress(ushort type, out IPEndPoint endpoint)
        {
            endpoint = null!;
            if (!TryGetValue(type, out var offset, out var length) || length < 8) return false;
            var family = bytes[offset + 1];
            var size = family == 1 ? 4 : family == 2 ? 16 : 0;
            if (size == 0 || length != 4 + size) return false;
            var port = (bytes[offset + 2] << 8) | bytes[offset + 3];
            var address = new byte[size];
            Buffer.BlockCopy(bytes, offset + 4, address, 0, size);
            if (IsXorAddress(type))
            {
                port ^= (int)(MagicCookie >> 16);
                for (var index = 0; index < size; index++) address[index] ^= bytes[4 + index];
            }

            if (port == 0) return false;
            endpoint = new IPEndPoint(new IPAddress(address), port);
            return true;
        }

        internal bool TryGetUInt32(ushort type, out uint value)
        {
            value = 0;
            if (!TryGetValue(type, out var offset, out var length) || length != 4) return false;
            value = StunTransactionId.ReadUInt32(bytes, offset);
            return true;
        }

        internal bool TryGetUInt64(ushort type, out ulong value)
        {
            value = 0;
            if (!TryGetValue(type, out var offset, out var length) || length != 8) return false;
            value = ((ulong)StunTransactionId.ReadUInt32(bytes, offset) << 32) | StunTransactionId.ReadUInt32(bytes, offset + 4);
            return true;
        }

        /// <summary>Reads a text attribute: strict UTF-8 of at most <paramref name="maximumBytes"/>, with no control characters.</summary>
        internal bool TryGetText(ushort type, int maximumBytes, out string text)
        {
            text = string.Empty;
            if (!TryGetValue(type, out var offset, out var length) || length == 0 || length > maximumBytes) return false;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes, offset, length);
            }
            catch (ArgumentException)
            {
                return false;
            }

            foreach (var character in text)
            {
                if (character < 0x20 || character == 0x7F) return false;
            }

            return true;
        }

        /// <summary>Reads the numeric ERROR-CODE, 300 to 699. The reason phrase is free text and is never read.</summary>
        internal bool TryGetErrorCode(out int code)
        {
            code = 0;
            if (!TryGetValue(StunAttribute.ErrorCode, out var offset, out var length) || length < 4) return false;
            var errorClass = bytes[offset + 2] & 0x07;
            var number = bytes[offset + 3];
            if (errorClass < 3 || errorClass > 6 || number > 99) return false;
            code = (errorClass * 100) + number;
            return true;
        }

        /// <summary>Reads CHANNEL-NUMBER: the number, whatever range it claims; callers check the range.</summary>
        internal bool TryGetChannelNumber(out ushort channel)
        {
            channel = 0;
            if (!TryGetValue(StunAttribute.ChannelNumber, out var offset, out var length) || length != 4) return false;
            channel = (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
            return true;
        }

        /// <summary>Reads REQUESTED-TRANSPORT's protocol number.</summary>
        internal bool TryGetRequestedTransport(out byte protocol)
        {
            protocol = 0;
            if (!TryGetValue(StunAttribute.RequestedTransport, out var offset, out var length) || length != 4) return false;
            protocol = bytes[offset];
            return true;
        }

        /// <summary>Reads UNKNOWN-ATTRIBUTES from a 420 response: at most 16 types.</summary>
        internal bool TryGetUnknownAttributes(out ushort[] types)
        {
            types = Array.Empty<ushort>();
            if (!TryGetValue(StunAttribute.UnknownAttributes, out var offset, out var length) || length == 0 || (length & 1) != 0 || length > 32) return false;
            types = new ushort[length / 2];
            for (var index = 0; index < types.Length; index++) types[index] = (ushort)((bytes[offset + (2 * index)] << 8) | bytes[offset + (2 * index) + 1]);
            return true;
        }

        /// <summary>Unmasks an XOR address value in place: 4 or 16 address bytes into <paramref name="address"/>, and the port.</summary>
        private static bool TryUnmaskAddress(byte[] buffer, int message, int value, int length, byte[] address, out int size, out int port)
        {
            var family = buffer[value + 1];
            size = family == 1 ? 4 : family == 2 ? 16 : 0;
            port = 0;
            if (size == 0 || length != 4 + size) return false;
            port = ((buffer[value + 2] << 8) | buffer[value + 3]) ^ (int)(MagicCookie >> 16);
            for (var index = 0; index < size; index++) address[index] = (byte)(buffer[value + 4 + index] ^ buffer[message + 4 + index]);
            return true;
        }

        /// <summary>Gets whether every address-carrying attribute of <paramref name="type"/> is XORed with the magic cookie.</summary>
        internal static bool IsXorAddress(ushort type) =>
            type == StunAttribute.XorMappedAddress || type == StunAttribute.XorPeerAddress || type == StunAttribute.XorRelayedAddress;

        /// <summary>Requests and responses for the request methods; indications only for Send and Data, and for Binding keepalives.</summary>
        private static bool IsValidPairing(int method, StunClass messageClass)
        {
            if (!StunMethod.IsKnown(method)) return false;
            if (method == StunMethod.Send || method == StunMethod.Data) return messageClass == StunClass.Indication;
            if (messageClass == StunClass.Indication) return method == StunMethod.Binding;
            return true;
        }

        private int IndexOf(ushort type)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].Type == type) return index;
            }

            return -1;
        }

        private readonly struct Entry
        {
            internal Entry(ushort type, int offset, int length)
            {
                Type = type;
                Offset = offset;
                Length = length;
            }

            internal ushort Type { get; }

            internal int Offset { get; }

            internal int Length { get; }
        }
    }
}
