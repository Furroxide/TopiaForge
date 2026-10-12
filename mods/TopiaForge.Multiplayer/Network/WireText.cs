using System;
using System.Security.Cryptography;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// The text encodings the game shares with the launcher and the control plane: lowercase hexadecimal and unpadded
    /// base64url (RFC 4648 section 5). Parsing is strict, so every value has exactly one accepted spelling.
    /// </summary>
    internal static class WireText
    {
        private const string HexDigits = "0123456789abcdef";
        private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

        internal static string ToHex(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var characters = new char[bytes.Length * 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                characters[index * 2] = HexDigits[bytes[index] >> 4];
                characters[(index * 2) + 1] = HexDigits[bytes[index] & 0xF];
            }

            return new string(characters);
        }

        /// <summary>Parses exactly <paramref name="byteCount"/> bytes of lowercase hexadecimal.</summary>
        internal static bool TryParseHex(string? text, int byteCount, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (text == null || text.Length != byteCount * 2) return false;
            var parsed = new byte[byteCount];
            for (var index = 0; index < byteCount; index++)
            {
                var high = HexValue(text[index * 2]);
                var low = HexValue(text[(index * 2) + 1]);
                if (high < 0 || low < 0) return false;
                parsed[index] = (byte)((high << 4) | low);
            }

            bytes = parsed;
            return true;
        }

        internal static bool IsHex(string? text, int byteCount) => TryParseHex(text, byteCount, out _);

        internal static string ToBase64Url(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var characters = new char[((bytes.Length * 8) + 5) / 6];
            var written = 0;
            var buffer = 0;
            var bits = 0;
            foreach (var value in bytes)
            {
                buffer = (buffer << 8) | value;
                bits += 8;
                while (bits >= 6)
                {
                    bits -= 6;
                    characters[written++] = Base64UrlAlphabet[(buffer >> bits) & 0x3F];
                }
            }

            if (bits > 0) characters[written++] = Base64UrlAlphabet[(buffer << (6 - bits)) & 0x3F];
            return new string(characters, 0, written);
        }

        /// <summary>
        /// Parses unpadded base64url that encodes exactly <paramref name="byteCount"/> bytes. The unused low bits of
        /// the last character must be zero, so no two spellings decode to the same bytes.
        /// </summary>
        internal static bool TryParseBase64Url(string? text, int byteCount, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (text == null || text.Length != ((byteCount * 8) + 5) / 6) return false;
            var parsed = new byte[byteCount];
            var written = 0;
            var buffer = 0;
            var bits = 0;
            foreach (var character in text)
            {
                var value = Base64UrlAlphabet.IndexOf(character);
                if (value < 0) return false;
                buffer = ((buffer << 6) | value) & 0xFFFF;
                bits += 6;
                if (bits >= 8)
                {
                    bits -= 8;
                    if (written == byteCount) return false;
                    parsed[written++] = (byte)(buffer >> bits);
                }
            }

            if (written != byteCount || (buffer & ((1 << bits) - 1)) != 0) return false;
            bytes = parsed;
            return true;
        }

        internal static byte[] Sha256(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            using (var hash = SHA256.Create())
            {
                return hash.ComputeHash(data);
            }
        }

        /// <summary>Compares two byte arrays in time that depends only on their lengths.</summary>
        internal static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        /// <summary>
        /// Whether <paramref name="text"/> is a display string the wire formats accept: 1 to <paramref name="maximum"/>
        /// UTF-16 code units, no surrounding whitespace, no control characters and no unpaired surrogates. Every
        /// language involved counts length in UTF-16 code units, so the limit means the same everywhere.
        /// </summary>
        internal static bool IsDisplayText(string? text, int maximum)
        {
            if (string.IsNullOrEmpty(text) || text!.Length > maximum) return false;
            if (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1])) return false;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (char.IsControl(character)) return false;
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])) return false;
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
            }

            return true;
        }

        private static int HexValue(char character)
        {
            if (character >= '0' && character <= '9') return character - '0';
            if (character >= 'a' && character <= 'f') return character - 'a' + 10;
            return -1;
        }
    }
}
