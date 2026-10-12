using System;
using System.Security.Cryptography;

namespace TopiaForge.Multiplayer.Transport.Crypto
{
    /// <summary>HKDF with HMAC-SHA256 (RFC 5869), and the two-output form the Noise framework derives keys with.</summary>
    internal static class Hkdf
    {
        internal const int HashSize = 32;

        /// <summary>Extracts a pseudorandom key. An empty salt equals a salt of zeros, as RFC 5869 specifies.</summary>
        internal static byte[] Extract(byte[] salt, byte[] inputKeyMaterial)
        {
            if (salt == null) throw new ArgumentNullException(nameof(salt));
            if (inputKeyMaterial == null) throw new ArgumentNullException(nameof(inputKeyMaterial));
            using (var hmac = new HMACSHA256(salt))
            {
                return hmac.ComputeHash(inputKeyMaterial);
            }
        }

        /// <summary>
        /// Expands a pseudorandom key into <paramref name="length"/> bytes bound to <paramref name="info"/>. Each
        /// intermediate block is output key material, so it is zeroed once copied out.
        /// </summary>
        internal static byte[] Expand(byte[] pseudorandomKey, byte[] info, int length)
        {
            if (pseudorandomKey == null) throw new ArgumentNullException(nameof(pseudorandomKey));
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (length < 1 || length > 255 * HashSize) throw new ArgumentOutOfRangeException(nameof(length));
            var output = new byte[length];
            var block = Array.Empty<byte>();
            using (var hmac = new HMACSHA256(pseudorandomKey))
            {
                var produced = 0;
                for (var counter = 1; produced < length; counter++)
                {
                    var input = new byte[block.Length + info.Length + 1];
                    Buffer.BlockCopy(block, 0, input, 0, block.Length);
                    Buffer.BlockCopy(info, 0, input, block.Length, info.Length);
                    input[input.Length - 1] = (byte)counter;
                    Array.Clear(block, 0, block.Length);
                    block = hmac.ComputeHash(input);
                    Array.Clear(input, 0, input.Length);
                    var take = Math.Min(HashSize, length - produced);
                    Buffer.BlockCopy(block, 0, output, produced, take);
                    produced += take;
                }
            }

            Array.Clear(block, 0, block.Length);
            return output;
        }

        /// <summary>
        /// The Noise framework's HKDF with two outputs: RFC 5869 with the chaining key as salt and empty info.
        /// </summary>
        internal static void DeriveTwo(byte[] chainingKey, byte[] inputKeyMaterial, byte[] first, byte[] second)
        {
            if (first == null) throw new ArgumentNullException(nameof(first));
            if (second == null) throw new ArgumentNullException(nameof(second));
            if (first.Length != HashSize || second.Length != HashSize) throw new ArgumentException("Each output is 32 bytes.");
            var pseudorandomKey = Extract(chainingKey, inputKeyMaterial);
            var material = Expand(pseudorandomKey, Array.Empty<byte>(), 2 * HashSize);
            Buffer.BlockCopy(material, 0, first, 0, HashSize);
            Buffer.BlockCopy(material, HashSize, second, 0, HashSize);
            Array.Clear(pseudorandomKey, 0, pseudorandomKey.Length);
            Array.Clear(material, 0, material.Length);
        }
    }
}
