using System;
using System.Security.Cryptography;
using System.Text;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// Proves knowledge of a session password without sending it: <c>HMAC-SHA256(PBKDF2-HMAC-SHA256(password,
    /// hostTransportKey, 100,000, 32), handshakeHash)</c>. The salt is the host's transport key, so a proof made for
    /// one host is worthless at another. The handshake hash binds it to one transport session, so it cannot be
    /// replayed, and it travels inside that session's encryption. The password is the NFC form of what the player
    /// typed, as UTF-8; it is never stored or staged.
    /// </summary>
    internal static class PasswordProof
    {
        internal const int Iterations = 100_000;
        internal const int KeySize = 32;
        internal const int ProofSize = 32;
        internal const int MaximumPasswordLength = 64;

        /// <summary>Whether <paramref name="password"/> is acceptable: 1 to 64 characters with no control characters.</summary>
        internal static bool IsValidPassword(string? password)
        {
            if (string.IsNullOrEmpty(password) || password!.Length > MaximumPasswordLength) return false;
            foreach (var character in password)
            {
                if (char.IsControl(character)) return false;
            }

            return true;
        }

        /// <summary>
        /// Derives the proof key. It costs 100,000 HMAC rounds, so callers run it off the main thread; the host
        /// derives it once when it starts hosting.
        /// </summary>
        internal static byte[] DeriveKey(string password, byte[] hostTransportKey)
        {
            if (!IsValidPassword(password)) throw new ArgumentException("A password is 1 to 64 characters with no control characters.", nameof(password));
            if (hostTransportKey == null || hostTransportKey.Length != 32) throw new ArgumentException("The host transport key is 32 bytes.", nameof(hostTransportKey));
            var secret = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
            try
            {
                return Pbkdf2Sha256(secret, hostTransportKey, Iterations, KeySize);
            }
            finally
            {
                Array.Clear(secret, 0, secret.Length);
            }
        }

        internal static byte[] Prove(byte[] key, byte[] handshakeHash)
        {
            if (key == null || key.Length != KeySize) throw new ArgumentException("A proof key is 32 bytes.", nameof(key));
            if (handshakeHash == null || handshakeHash.Length != 32) throw new ArgumentException("A handshake hash is 32 bytes.", nameof(handshakeHash));
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(handshakeHash);
            }
        }

        internal static bool Verify(byte[] key, byte[] handshakeHash, byte[]? proof) =>
            proof != null && proof.Length == ProofSize && WireText.FixedTimeEquals(Prove(key, handshakeHash), proof);

        /// <summary>PBKDF2 (RFC 8018) with HMAC-SHA256, written out because Mono's class library may lack the SHA-256 form.</summary>
        internal static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations, int length)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));
            if (salt == null) throw new ArgumentNullException(nameof(salt));
            if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
            if (length < 1 || length > 1024) throw new ArgumentOutOfRangeException(nameof(length));
            var output = new byte[length];
            var block = new byte[salt.Length + 4];
            Buffer.BlockCopy(salt, 0, block, 0, salt.Length);
            using (var hmac = new HMACSHA256(password))
            {
                var produced = 0;
                for (var counter = 1; produced < length; counter++)
                {
                    block[salt.Length] = (byte)(counter >> 24);
                    block[salt.Length + 1] = (byte)(counter >> 16);
                    block[salt.Length + 2] = (byte)(counter >> 8);
                    block[salt.Length + 3] = (byte)counter;
                    var u = hmac.ComputeHash(block);
                    var t = (byte[])u.Clone();
                    for (var round = 1; round < iterations; round++)
                    {
                        var next = hmac.ComputeHash(u);
                        Array.Clear(u, 0, u.Length);
                        u = next;
                        for (var index = 0; index < t.Length; index++) t[index] ^= u[index];
                    }

                    var take = Math.Min(t.Length, length - produced);
                    Buffer.BlockCopy(t, 0, output, produced, take);
                    produced += take;
                    Array.Clear(u, 0, u.Length);
                    Array.Clear(t, 0, t.Length);
                }
            }

            return output;
        }
    }

    /// <summary>
    /// The player key a host gives each joining identity: the first 16 bytes of <c>HMAC-SHA256(hostSecret,
    /// "TopiaForge player key v1" || clientTransportKey)</c> in base64url. It is stable for one player at one host and
    /// unlinkable across hosts, because each host keeps its own secret.
    /// </summary>
    internal static class PlayerKeys
    {
        internal const int SecretSize = 32;
        private static readonly byte[] Label = Encoding.ASCII.GetBytes("TopiaForge player key v1");

        internal static string Derive(byte[] hostSecret, byte[] clientTransportKey)
        {
            if (hostSecret == null || hostSecret.Length != SecretSize) throw new ArgumentException("A host secret is 32 bytes.", nameof(hostSecret));
            if (clientTransportKey == null || clientTransportKey.Length != 32) throw new ArgumentException("A transport key is 32 bytes.", nameof(clientTransportKey));
            var message = new byte[Label.Length + clientTransportKey.Length];
            Buffer.BlockCopy(Label, 0, message, 0, Label.Length);
            Buffer.BlockCopy(clientTransportKey, 0, message, Label.Length, clientTransportKey.Length);
            byte[] mac;
            using (var hmac = new HMACSHA256(hostSecret))
            {
                mac = hmac.ComputeHash(message);
            }

            var key = new byte[16];
            Buffer.BlockCopy(mac, 0, key, 0, key.Length);
            Array.Clear(mac, 0, mac.Length);
            return WireText.ToBase64Url(key);
        }
    }
}
