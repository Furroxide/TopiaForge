using System;
using TopiaForge.Multiplayer.Transport.Crypto;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// This install's multiplayer identity: an X25519 static key the transport handshake proves, an Ed25519 key that
    /// signs this player's server descriptors, and a secret that derives the player keys this install gives others
    /// when it hosts. There are no accounts, so the identity is the key: rotating it starts a new identity, which
    /// also escapes any ban keyed by the old one. The secrets never leave the process except into the identity file.
    /// </summary>
    internal sealed class LocalIdentity : IDisposable
    {
        internal const string Schema = "topiaforge.identity/1";
        internal const int MaximumBytes = 4096;

        private readonly byte[] transportPrivateKey;
        private readonly byte[] signingSeed;
        private readonly byte[] hostSecret;
        private readonly byte[] transportPublicKey;
        private readonly byte[] signingPublicKey;
        private bool disposed;

        internal LocalIdentity(byte[] transportPrivateKey, byte[] signingSeed, byte[] hostSecret, long createdAt)
        {
            if (transportPrivateKey == null || transportPrivateKey.Length != X25519.KeySize) throw new ArgumentException("A transport key is 32 bytes.", nameof(transportPrivateKey));
            if (signingSeed == null || signingSeed.Length != Ed25519.SecretKeySize) throw new ArgumentException("A signing key is 32 bytes.", nameof(signingSeed));
            if (hostSecret == null || hostSecret.Length != PlayerKeys.SecretSize) throw new ArgumentException("A host secret is 32 bytes.", nameof(hostSecret));
            if (createdAt < 0 || createdAt > CanonicalJson.MaximumSafeInteger) throw new ArgumentOutOfRangeException(nameof(createdAt));
            this.transportPrivateKey = (byte[])transportPrivateKey.Clone();
            this.signingSeed = (byte[])signingSeed.Clone();
            this.hostSecret = (byte[])hostSecret.Clone();
            CreatedAt = createdAt;
            transportPublicKey = X25519.DerivePublicKey(this.transportPrivateKey);
            signingPublicKey = Ed25519.DerivePublicKey(this.signingSeed);
            ServerId = ServerDescriptor.ServerIdOf(signingPublicKey);
            Fingerprint = WireText.ToHex(transportPublicKey);
        }

        /// <summary>Gets the creation time in Unix seconds.</summary>
        internal long CreatedAt { get; }

        /// <summary>Gets a copy of the X25519 public key; the fingerprint is derived from the original.</summary>
        internal byte[] TransportPublicKey => (byte[])transportPublicKey.Clone();

        /// <summary>Gets a copy of the Ed25519 public key; the server id is derived from the original.</summary>
        internal byte[] SigningPublicKey => (byte[])signingPublicKey.Clone();

        /// <summary>Gets the server id this install hosts under: the SHA-256 of its signing key, in hexadecimal.</summary>
        internal string ServerId { get; }

        /// <summary>
        /// Gets the identity other hosts see: the transport public key in lowercase hexadecimal, as the session engine
        /// names a peer. Bans are keyed by it.
        /// </summary>
        internal string Fingerprint { get; }

        /// <summary>Gets a copy of the transport private key, for the endpoint that owns it from then on.</summary>
        internal byte[] CopyTransportPrivateKey()
        {
            ThrowIfDisposed();
            return (byte[])transportPrivateKey.Clone();
        }

        /// <summary>Gets a copy of the signing seed, for signing a descriptor; the caller wipes it.</summary>
        internal byte[] CopySigningSeed()
        {
            ThrowIfDisposed();
            return (byte[])signingSeed.Clone();
        }

        /// <summary>Derives the player key this host gives the identity holding <paramref name="clientTransportKey"/>.</summary>
        internal string PlayerKeyFor(byte[] clientTransportKey)
        {
            ThrowIfDisposed();
            return PlayerKeys.Derive(hostSecret, clientTransportKey);
        }

        internal static LocalIdentity Create(long createdAt)
        {
            var transport = X25519.GeneratePrivateKey();
            var signing = Ed25519.GenerateSecretKey();
            var secret = new byte[PlayerKeys.SecretSize];
            CryptoRandom.Fill(secret);
            try
            {
                return new LocalIdentity(transport, signing, secret, createdAt);
            }
            finally
            {
                Array.Clear(transport, 0, transport.Length);
                Array.Clear(signing, 0, signing.Length);
                Array.Clear(secret, 0, secret.Length);
            }
        }

        internal byte[] Serialize()
        {
            ThrowIfDisposed();
            return CanonicalJson.Serialize(new JsonObjectNode()
                .Add("schema", Schema)
                .Add("createdAt", CreatedAt)
                .Add("transportPrivateKey", WireText.ToBase64Url(transportPrivateKey))
                .Add("signingSeed", WireText.ToBase64Url(signingSeed))
                .Add("hostSecret", WireText.ToBase64Url(hostSecret)));
        }

        /// <summary>Reads an identity file in any layout; see the overload that says whether it was canonical.</summary>
        internal static bool TryParse(byte[] bytes, out LocalIdentity? identity, out string error) => TryParse(bytes, out identity, out _, out error);

        /// <summary>
        /// Reads an identity file in any layout another program may have left it in: white space, a byte-order mark and
        /// members in any order read as this game's canonical bytes do. Every check on the keys is the one this game's own
        /// bytes pass: exactly the version 1 members, the schema, each key 32 bytes in its one base64url spelling, and the
        /// creation time. <paramref name="canonical"/> says whether the bytes are exactly what this game writes. Neither
        /// <paramref name="error"/> nor anything else says anything about the keys: it names only what was wrong.
        /// </summary>
        internal static bool TryParse(byte[] bytes, out LocalIdentity? identity, out bool canonical, out string error)
        {
            identity = null;
            canonical = false;
            if (!CanonicalJson.TryParseAnyLayout(bytes, MaximumBytes, out var node, out error)) return false;
            byte[]? transport = null;
            byte[]? signing = null;
            byte[]? secret = null;
            try
            {
                if (!(node is JsonObjectNode value) || !value.HasExactly("schema", "createdAt", "transportPrivateKey", "signingSeed", "hostSecret"))
                {
                    throw new FormatException("An identity file has exactly the version 1 members.");
                }

                if (JsonRead.String(value, "schema") != Schema) throw new FormatException("The identity schema is not " + Schema + ".");
                if (!WireText.TryParseBase64Url(JsonRead.String(value, "transportPrivateKey"), 32, out transport) ||
                    !WireText.TryParseBase64Url(JsonRead.String(value, "signingSeed"), 32, out signing) ||
                    !WireText.TryParseBase64Url(JsonRead.String(value, "hostSecret"), 32, out secret))
                {
                    throw new FormatException("An identity key is malformed.");
                }

                var read = new LocalIdentity(transport, signing, secret, JsonRead.Integer(value, "createdAt"));
                canonical = IsWrittenAs(read, bytes);
                identity = read;
                error = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is FormatException || exception is ArgumentException)
            {
                error = exception.Message;
                return false;
            }
            finally
            {
                if (transport != null) Array.Clear(transport, 0, transport.Length);
                if (signing != null) Array.Clear(signing, 0, signing.Length);
                if (secret != null) Array.Clear(secret, 0, secret.Length);
            }
        }

        /// <summary>Whether <paramref name="bytes"/> are exactly what <paramref name="identity"/> writes; that copy is zeroed.</summary>
        private static bool IsWrittenAs(LocalIdentity identity, byte[] bytes)
        {
            var written = identity.Serialize();
            try
            {
                return WireText.FixedTimeEquals(written, bytes);
            }
            finally
            {
                Array.Clear(written, 0, written.Length);
            }
        }

        /// <summary>Zeroes the secrets. The public values stay readable.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Array.Clear(transportPrivateKey, 0, transportPrivateKey.Length);
            Array.Clear(signingSeed, 0, signingSeed.Length);
            Array.Clear(hostSecret, 0, hostSecret.Length);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(LocalIdentity));
        }
    }
}
