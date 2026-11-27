using System;
using Rfc8032 = TopiaForge.Multiplayer.ThirdParty.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace TopiaForge.Multiplayer.Transport.Crypto
{
    /// <summary>Ed25519 signatures (RFC 8032, pure mode), over the vendored BouncyCastle implementation.</summary>
    internal static class Ed25519
    {
        internal const int SecretKeySize = 32;
        internal const int PublicKeySize = 32;
        internal const int SignatureSize = 64;

        internal static byte[] GenerateSecretKey()
        {
            var secretKey = new byte[SecretKeySize];
            CryptoRandom.Fill(secretKey);
            return secretKey;
        }

        internal static byte[] DerivePublicKey(byte[] secretKey)
        {
            RequireLength(secretKey, SecretKeySize, nameof(secretKey));
            var publicKey = new byte[PublicKeySize];
            Rfc8032.GeneratePublicKey(secretKey, 0, publicKey, 0);
            return publicKey;
        }

        internal static byte[] Sign(byte[] secretKey, byte[] message)
        {
            RequireLength(secretKey, SecretKeySize, nameof(secretKey));
            if (message == null) throw new ArgumentNullException(nameof(message));
            var signature = new byte[SignatureSize];
            Rfc8032.Sign(secretKey, 0, message, 0, message.Length, signature, 0);
            return signature;
        }

        /// <summary>Checks a signature. A key or signature of the wrong length simply fails to verify.</summary>
        internal static bool Verify(byte[] signature, byte[] publicKey, byte[] message)
        {
            if (signature == null) throw new ArgumentNullException(nameof(signature));
            if (publicKey == null) throw new ArgumentNullException(nameof(publicKey));
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (signature.Length != SignatureSize || publicKey.Length != PublicKeySize) return false;
            return Rfc8032.Verify(signature, 0, publicKey, 0, message, 0, message.Length);
        }

        private static void RequireLength(byte[] value, int length, string name)
        {
            if (value == null) throw new ArgumentNullException(name);
            if (value.Length != length) throw new ArgumentException("An Ed25519 key is " + length + " bytes.", name);
        }
    }
}
