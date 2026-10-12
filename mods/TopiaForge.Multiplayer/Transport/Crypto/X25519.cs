using System;
using Rfc7748 = TopiaForge.Multiplayer.ThirdParty.BouncyCastle.Math.EC.Rfc7748.X25519;

namespace TopiaForge.Multiplayer.Transport.Crypto
{
    /// <summary>X25519 Diffie-Hellman (RFC 7748), over the vendored BouncyCastle implementation.</summary>
    internal static class X25519
    {
        internal const int KeySize = 32;

        /// <summary>Creates a random private key. Its scalar is clamped whenever it is used, as RFC 7748 specifies.</summary>
        internal static byte[] GeneratePrivateKey()
        {
            var privateKey = new byte[KeySize];
            CryptoRandom.Fill(privateKey);
            return privateKey;
        }

        internal static byte[] DerivePublicKey(byte[] privateKey)
        {
            RequireKey(privateKey, nameof(privateKey));
            var publicKey = new byte[KeySize];
            Rfc7748.GeneratePublicKey(privateKey, 0, publicKey, 0);
            return publicKey;
        }

        /// <summary>
        /// Computes the shared secret into <paramref name="sharedSecret"/>. Returns false when the peer's key is a
        /// low-order point, which makes the secret all zeros (RFC 7748 section 6.1); the caller must then abort.
        /// </summary>
        internal static bool TryAgree(byte[] privateKey, byte[] peerPublicKey, byte[] sharedSecret)
        {
            RequireKey(privateKey, nameof(privateKey));
            RequireKey(peerPublicKey, nameof(peerPublicKey));
            RequireKey(sharedSecret, nameof(sharedSecret));
            return Rfc7748.CalculateAgreement(privateKey, 0, peerPublicKey, 0, sharedSecret, 0);
        }

        /// <summary>Multiplies <paramref name="u"/> by the clamped <paramref name="scalar"/>, with no zero check.</summary>
        internal static byte[] ScalarMultiply(byte[] scalar, byte[] u)
        {
            RequireKey(scalar, nameof(scalar));
            RequireKey(u, nameof(u));
            var result = new byte[KeySize];
            Rfc7748.ScalarMult(scalar, 0, u, 0, result, 0);
            return result;
        }

        private static void RequireKey(byte[] key, string name)
        {
            if (key == null) throw new ArgumentNullException(name);
            if (key.Length != KeySize) throw new ArgumentException("X25519 keys and points are 32 bytes.", name);
        }
    }
}
