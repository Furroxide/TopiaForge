using System;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Stun
{
    /// <summary>The two class bits of a STUN message type (RFC 8489, section 5).</summary>
    internal enum StunClass
    {
        Request = 0,
        Indication = 1,
        Success = 2,
        Error = 3
    }

    /// <summary>The STUN and TURN methods this layer speaks. Every one is below 0x080, so its first byte is 0 or 1.</summary>
    internal static class StunMethod
    {
        internal const int Binding = 0x001;
        internal const int Allocate = 0x003;
        internal const int Refresh = 0x004;
        internal const int Send = 0x006;
        internal const int Data = 0x007;
        internal const int CreatePermission = 0x008;
        internal const int ChannelBind = 0x009;

        internal static bool IsKnown(int method) =>
            method == Binding || method == Allocate || method == Refresh || method == Send || method == Data ||
            method == CreatePermission || method == ChannelBind;
    }

    /// <summary>Attribute types from RFC 8489, RFC 8656 and RFC 8445.</summary>
    internal static class StunAttribute
    {
        internal const ushort MappedAddress = 0x0001;
        internal const ushort Username = 0x0006;
        internal const ushort MessageIntegrity = 0x0008;
        internal const ushort ErrorCode = 0x0009;
        internal const ushort UnknownAttributes = 0x000A;
        internal const ushort ChannelNumber = 0x000C;
        internal const ushort Lifetime = 0x000D;
        internal const ushort XorPeerAddress = 0x0012;
        internal const ushort Data = 0x0013;
        internal const ushort Realm = 0x0014;
        internal const ushort Nonce = 0x0015;
        internal const ushort XorRelayedAddress = 0x0016;
        internal const ushort RequestedAddressFamily = 0x0017;
        internal const ushort EvenPort = 0x0018;
        internal const ushort RequestedTransport = 0x0019;
        internal const ushort DontFragment = 0x001A;
        internal const ushort MessageIntegritySha256 = 0x001C;
        internal const ushort PasswordAlgorithm = 0x001D;
        internal const ushort UserHash = 0x001E;
        internal const ushort XorMappedAddress = 0x0020;
        internal const ushort ReservationToken = 0x0022;
        internal const ushort Priority = 0x0024;
        internal const ushort UseCandidate = 0x0025;
        internal const ushort AdditionalAddressFamily = 0x8000;
        internal const ushort AddressErrorCode = 0x8001;
        internal const ushort PasswordAlgorithms = 0x8002;
        internal const ushort AlternateDomain = 0x8003;
        internal const ushort Icmp = 0x8004;
        internal const ushort Software = 0x8022;
        internal const ushort AlternateServer = 0x8023;
        internal const ushort Fingerprint = 0x8028;
        internal const ushort IceControlled = 0x8029;
        internal const ushort IceControlling = 0x802A;

        /// <summary>Gets whether an agent that does not understand <paramref name="type"/> must refuse the message.</summary>
        internal static bool IsComprehensionRequired(ushort type) => type < 0x8000;

        /// <summary>
        /// Gets whether a message carrying <paramref name="type"/> must be refused here: a comprehension-required type this
        /// layer does not know. Comprehension-optional types are always skipped silently.
        /// </summary>
        internal static bool IsUnknownRequired(ushort type) => IsComprehensionRequired(type) && !IsKnownRequired(type);

        private static bool IsKnownRequired(ushort type)
        {
            switch (type)
            {
                case MappedAddress:
                case Username:
                case MessageIntegrity:
                case ErrorCode:
                case UnknownAttributes:
                case ChannelNumber:
                case Lifetime:
                case XorPeerAddress:
                case Data:
                case Realm:
                case Nonce:
                case XorRelayedAddress:
                case RequestedAddressFamily:
                case EvenPort:
                case RequestedTransport:
                case DontFragment:
                case MessageIntegritySha256:
                case PasswordAlgorithm:
                case UserHash:
                case XorMappedAddress:
                case ReservationToken:
                case Priority:
                case UseCandidate:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>A 96-bit transaction id, held as three words so it can key a table without allocating.</summary>
    internal readonly struct StunTransactionId : IEquatable<StunTransactionId>
    {
        internal const int Size = 12;

        private readonly uint high;
        private readonly uint middle;
        private readonly uint low;

        private StunTransactionId(uint high, uint middle, uint low)
        {
            this.high = high;
            this.middle = middle;
            this.low = low;
        }

        /// <summary>Draws a new id from <paramref name="random"/>, using <paramref name="scratch"/> (12 bytes) as space.</summary>
        internal static StunTransactionId New(ITransportRandom random, byte[] scratch)
        {
            random.Fill(scratch);
            return Read(scratch, 0);
        }

        internal static StunTransactionId Read(byte[] buffer, int offset) =>
            new StunTransactionId(ReadUInt32(buffer, offset), ReadUInt32(buffer, offset + 4), ReadUInt32(buffer, offset + 8));

        internal void Write(byte[] buffer, int offset)
        {
            WriteUInt32(buffer, offset, high);
            WriteUInt32(buffer, offset + 4, middle);
            WriteUInt32(buffer, offset + 8, low);
        }

        public bool Equals(StunTransactionId other) => high == other.high && middle == other.middle && low == other.low;

        public override bool Equals(object? obj) => obj is StunTransactionId other && Equals(other);

        public override int GetHashCode() => unchecked((int)(high ^ (middle * 16777619u) ^ (low * 2166136261u)));

        /// <summary>A transaction id is random, not secret, but it is not something to print either.</summary>
        public override string ToString() => "STUN transaction";

        internal static uint ReadUInt32(byte[] buffer, int offset) =>
            ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

        internal static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }

    /// <summary>Credential keys. Short-term keys are the password's bytes; long-term keys are MD5(user:realm:password).</summary>
    internal static class StunCredentials
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        /// <summary>The ICE key: the UTF-8 bytes of the password the other side chose (RFC 8445, section 7.2.2).</summary>
        internal static byte[] ShortTermKey(string password)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));
            return Utf8.GetBytes(password);
        }

        /// <summary>
        /// The TURN key for the long-term mechanism with MD5 (RFC 8489, section 9.2.2). The strings must already be in
        /// their prepared form; the control plane's credentials and every realm this client accepts are printable ASCII,
        /// which preparation leaves unchanged.
        /// </summary>
        internal static byte[] LongTermKey(string username, string realm, string password)
        {
            if (username == null) throw new ArgumentNullException(nameof(username));
            if (realm == null) throw new ArgumentNullException(nameof(realm));
            if (password == null) throw new ArgumentNullException(nameof(password));
            var input = Utf8.GetBytes(username + ":" + realm + ":" + password);
            try
            {
                using (var md5 = MD5.Create()) return md5.ComputeHash(input);
            }
            finally
            {
                Array.Clear(input, 0, input.Length);
            }
        }

        /// <summary>Gets whether every character of <paramref name="text"/> is printable ASCII, 1 to <paramref name="maximum"/> of them.</summary>
        internal static bool IsPrintableAscii(string? text, int maximum)
        {
            if (string.IsNullOrEmpty(text) || text!.Length > maximum) return false;
            foreach (var character in text)
            {
                if (character < 0x21 || character > 0x7E) return false;
            }

            return true;
        }
    }

    /// <summary>The IEEE 802.3 CRC-32 that STUN's FINGERPRINT uses, before its XOR with 0x5354554E.</summary>
    internal static class StunCrc32
    {
        private static readonly uint[] Table = BuildTable();

        internal static uint Compute(byte[] buffer, int offset, int count)
        {
            var crc = 0xFFFFFFFFu;
            for (var index = offset; index < offset + count; index++) crc = Table[(crc ^ buffer[index]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < 256; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                table[index] = value;
            }

            return table;
        }
    }
}
