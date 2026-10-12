using System;

namespace TopiaForge.Multiplayer.Transport.Crypto
{
    /// <summary>
    /// A Noise CipherState for ChaCha20-Poly1305: a key and a 64-bit nonce. Ordered messages use the implicit nonce,
    /// which advances only on success; datagrams carry an explicit nonce that a <see cref="ReplayWindow"/> polices.
    /// Replacing the key or disposing the state zeroes the key it held, and a disposed state refuses all work rather
    /// than falling back to the keyless pass-through.
    /// </summary>
    internal sealed class CipherState : IDisposable
    {
        /// <summary>Gets the nonce Noise reserves for <see cref="Rekey"/>; no message is ever sealed with it.</summary>
        internal const ulong ReservedNonce = ulong.MaxValue;

        internal const int TagSize = ChaCha20Poly1305.TagSize;

        private readonly byte[] nonceBytes = new byte[ChaCha20Poly1305.NonceSize];
        private ChaCha20Poly1305? cipher;
        private bool disposed;

        internal bool HasKey => cipher != null;

        /// <summary>Gets the implicit nonce the next ordered message uses.</summary>
        internal ulong Nonce { get; private set; }

        /// <summary>
        /// Sets the key, or clears it when <paramref name="key"/> is null, and restarts the nonce at zero. The previous
        /// key is zeroed.
        /// </summary>
        internal void InitializeKey(byte[]? key)
        {
            ThrowIfDisposed();
            cipher?.Dispose();
            cipher = key == null ? null : new ChaCha20Poly1305(key);
            Nonce = 0;
        }

        /// <summary>Zeroes the key. The state can do no more work.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cipher?.Dispose();
            cipher = null;
        }

        /// <summary>Encrypts with the implicit nonce. Without a key the plaintext passes through, as Noise specifies.</summary>
        internal byte[] EncryptWithAd(byte[] associatedData, byte[] plaintext)
        {
            ThrowIfDisposed();
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (cipher == null) return (byte[])plaintext.Clone();
            var ciphertext = new byte[plaintext.Length + TagSize];
            Seal(Nonce, associatedData, plaintext, 0, plaintext.Length, ciphertext, 0);
            Nonce++;
            return ciphertext;
        }

        /// <summary>Decrypts with the implicit nonce, which advances only when the message authenticates.</summary>
        internal bool TryDecryptWithAd(byte[] associatedData, byte[] ciphertext, out byte[] plaintext)
        {
            ThrowIfDisposed();
            if (ciphertext == null) throw new ArgumentNullException(nameof(ciphertext));
            plaintext = Array.Empty<byte>();
            if (cipher == null)
            {
                plaintext = (byte[])ciphertext.Clone();
                return true;
            }

            if (ciphertext.Length < TagSize) return false;
            var output = new byte[ciphertext.Length - TagSize];
            if (!TryOpen(Nonce, associatedData, ciphertext, 0, ciphertext.Length, output, 0, out _)) return false;
            Nonce++;
            plaintext = output;
            return true;
        }

        /// <summary>Seals with an explicit nonce, for a datagram that carries its own counter.</summary>
        internal int Seal(ulong nonce, byte[] associatedData, byte[] plaintext, int offset, int count, byte[] output, int outputOffset)
        {
            var key = RequireKey();
            if (nonce == ReservedNonce) throw new InvalidOperationException("The cipher state has used every nonce; the session must rekey or end.");
            EncodeNonce(nonce);
            return key.Seal(nonceBytes, associatedData, plaintext, offset, count, output, outputOffset);
        }

        /// <summary>Opens a datagram sealed with an explicit nonce. The reserved nonce never authenticates.</summary>
        internal bool TryOpen(
            ulong nonce,
            byte[] associatedData,
            byte[] ciphertext,
            int offset,
            int count,
            byte[] output,
            int outputOffset,
            out int written)
        {
            var key = RequireKey();
            written = 0;
            if (nonce == ReservedNonce) return false;
            EncodeNonce(nonce);
            return key.TryOpen(nonceBytes, associatedData, ciphertext, offset, count, output, outputOffset, out written);
        }

        /// <summary>
        /// Noise REKEY: the new key is the first 32 bytes of 32 zero bytes sealed under the reserved nonce. The implicit
        /// nonce carries on unchanged.
        /// </summary>
        internal void Rekey()
        {
            var key = RequireKey();
            EncodeNonce(ReservedNonce);
            var sealedZeros = new byte[ChaCha20Poly1305.KeySize + TagSize];
            key.Seal(nonceBytes, Array.Empty<byte>(), new byte[ChaCha20Poly1305.KeySize], 0, ChaCha20Poly1305.KeySize, sealedZeros, 0);
            var next = new byte[ChaCha20Poly1305.KeySize];
            Buffer.BlockCopy(sealedZeros, 0, next, 0, next.Length);
            var nonce = Nonce;
            InitializeKey(next);
            Nonce = nonce;
            Array.Clear(next, 0, next.Length);
            Array.Clear(sealedZeros, 0, sealedZeros.Length);
        }

        private ChaCha20Poly1305 RequireKey()
        {
            ThrowIfDisposed();
            return cipher ?? throw new InvalidOperationException("The cipher state has no key yet.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(CipherState));
        }

        /// <summary>Writes the Noise ChaChaPoly nonce: four zero bytes, then the counter in little-endian order.</summary>
        private void EncodeNonce(ulong nonce)
        {
            nonceBytes[0] = 0;
            nonceBytes[1] = 0;
            nonceBytes[2] = 0;
            nonceBytes[3] = 0;
            for (var index = 0; index < 8; index++) nonceBytes[4 + index] = (byte)(nonce >> (8 * index));
        }
    }
}
