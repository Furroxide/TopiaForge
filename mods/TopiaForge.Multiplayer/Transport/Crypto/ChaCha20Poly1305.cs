using System;
using TopiaForge.Multiplayer.ThirdParty.BouncyCastle.Crypto;
using TopiaForge.Multiplayer.ThirdParty.BouncyCastle.Crypto.Parameters;
using Rfc8439 = TopiaForge.Multiplayer.ThirdParty.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace TopiaForge.Multiplayer.Transport.Crypto
{
    /// <summary>
    /// ChaCha20-Poly1305 (RFC 8439) under one key, over the vendored BouncyCastle implementation. An instance is not
    /// thread-safe: each direction of a connection owns its own.
    /// </summary>
    internal sealed class ChaCha20Poly1305
    {
        internal const int KeySize = 32;
        internal const int NonceSize = 12;
        internal const int TagSize = 16;

        private readonly Rfc8439 cipher = new Rfc8439();
        private readonly KeyParameter key;

        internal ChaCha20Poly1305(byte[] key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length != KeySize) throw new ArgumentException("A ChaCha20-Poly1305 key is 32 bytes.", nameof(key));
            this.key = new KeyParameter(key);
        }

        /// <summary>
        /// Encrypts <paramref name="count"/> bytes and appends the 16-byte tag, returning the bytes written. A nonce
        /// must never repeat under one key; the underlying implementation refuses an immediate repeat.
        /// </summary>
        internal int Seal(
            byte[] nonce,
            byte[] associatedData,
            byte[] plaintext,
            int offset,
            int count,
            byte[] output,
            int outputOffset)
        {
            RequireArguments(nonce, associatedData, plaintext, offset, count, output, outputOffset, count + TagSize);
            cipher.Init(true, new ParametersWithIV(key, nonce));
            if (associatedData.Length > 0) cipher.ProcessAadBytes(associatedData, 0, associatedData.Length);
            var written = cipher.ProcessBytes(plaintext, offset, count, output, outputOffset);
            return written + cipher.DoFinal(output, outputOffset + written);
        }

        /// <summary>
        /// Verifies and decrypts <paramref name="count"/> bytes of ciphertext and tag. On a bad tag it returns false and
        /// clears whatever plaintext the cipher had already written.
        /// </summary>
        internal bool TryOpen(
            byte[] nonce,
            byte[] associatedData,
            byte[] ciphertext,
            int offset,
            int count,
            byte[] output,
            int outputOffset,
            out int written)
        {
            written = 0;
            if (count < TagSize) return false;
            RequireArguments(nonce, associatedData, ciphertext, offset, count, output, outputOffset, count - TagSize);
            cipher.Init(false, new ParametersWithIV(key, nonce));
            if (associatedData.Length > 0) cipher.ProcessAadBytes(associatedData, 0, associatedData.Length);
            try
            {
                var produced = cipher.ProcessBytes(ciphertext, offset, count, output, outputOffset);
                written = produced + cipher.DoFinal(output, outputOffset + produced);
                return true;
            }
            catch (InvalidCipherTextException)
            {
                Array.Clear(output, outputOffset, count - TagSize);
                return false;
            }
        }

        private static void RequireArguments(
            byte[] nonce,
            byte[] associatedData,
            byte[] input,
            int offset,
            int count,
            byte[] output,
            int outputOffset,
            int outputCount)
        {
            if (nonce == null) throw new ArgumentNullException(nameof(nonce));
            if (nonce.Length != NonceSize) throw new ArgumentException("A ChaCha20-Poly1305 nonce is 12 bytes.", nameof(nonce));
            if (associatedData == null) throw new ArgumentNullException(nameof(associatedData));
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (offset < 0 || count < 0 || offset > input.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (outputOffset < 0 || outputOffset > output.Length - outputCount)
            {
                throw new ArgumentException("The output buffer is too small.", nameof(output));
            }
        }
    }
}
