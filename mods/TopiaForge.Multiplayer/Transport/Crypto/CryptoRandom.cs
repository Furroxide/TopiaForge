using System;
using System.Security.Cryptography;

namespace TopiaForge.Multiplayer.Transport.Crypto
{
    /// <summary>The only source of key material: the platform's cryptographic random number generator.</summary>
    internal static class CryptoRandom
    {
        private static readonly RandomNumberGenerator Source = RandomNumberGenerator.Create();
        private static readonly object Gate = new object();

        /// <summary>Fills <paramref name="buffer"/> with cryptographically strong random bytes.</summary>
        internal static void Fill(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            // Mono's provider makes no thread-safety promise, and the main and network threads both draw keys.
            lock (Gate)
            {
                Source.GetBytes(buffer);
            }
        }
    }
}
