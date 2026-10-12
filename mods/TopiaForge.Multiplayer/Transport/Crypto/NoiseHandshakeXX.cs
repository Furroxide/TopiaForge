using System;
using System.Security.Cryptography;
using System.Text;

namespace TopiaForge.Multiplayer.Transport.Crypto
{
    /// <summary>
    /// One side of a Noise_XX_25519_ChaChaPoly_SHA256 handshake:
    /// <code>
    /// -> e
    /// &lt;- e, ee, s, es
    /// -> s, se
    /// </code>
    /// Both statics travel encrypted, so each side learns the other's authenticated static key. After the third
    /// message <see cref="Split"/> yields the transport cipher states. A message that fails to authenticate ends the
    /// handshake for good.
    /// </summary>
    internal sealed class NoiseHandshakeXX : IDisposable
    {
        internal const string ProtocolName = "Noise_XX_25519_ChaChaPoly_SHA256";

        /// <summary>Gets the largest handshake message Noise allows.</summary>
        internal const int MaximumMessageSize = 65535;

        private const int KeySize = X25519.KeySize;
        private const int EncryptedKeySize = KeySize + CipherState.TagSize;

        private readonly SymmetricState symmetric;
        private readonly byte[] staticPrivate;
        private readonly byte[] staticPublic;
        private readonly Func<byte[]> ephemeralSource;
        private byte[]? ephemeralPrivate;
        private byte[]? ephemeralPublic;
        private byte[]? remoteEphemeral;
        private byte[]? remoteStatic;
        private int messagesDone;
        private bool failed;
        private bool split;

        /// <summary>Starts a handshake as initiator or responder with this install's static key and a shared prologue.</summary>
        internal NoiseHandshakeXX(bool initiator, byte[] staticPrivateKey, byte[] prologue)
            : this(initiator, staticPrivateKey, prologue, X25519.GeneratePrivateKey)
        {
        }

        /// <summary>Starts a handshake whose ephemeral key comes from <paramref name="ephemeralSource"/>; tests use fixed keys.</summary>
        internal NoiseHandshakeXX(bool initiator, byte[] staticPrivateKey, byte[] prologue, Func<byte[]> ephemeralSource)
        {
            if (prologue == null) throw new ArgumentNullException(nameof(prologue));
            IsInitiator = initiator;
            staticPrivate = (byte[])(staticPrivateKey ?? throw new ArgumentNullException(nameof(staticPrivateKey))).Clone();
            staticPublic = X25519.DerivePublicKey(staticPrivate);
            this.ephemeralSource = ephemeralSource ?? throw new ArgumentNullException(nameof(ephemeralSource));
            symmetric = new SymmetricState();
            symmetric.MixHash(prologue);
        }

        internal bool IsInitiator { get; }

        /// <summary>Gets whether all three messages have been written and read.</summary>
        internal bool IsComplete => messagesDone == 3 && !failed;

        /// <summary>Gets whether it is this side's turn to write.</summary>
        internal bool IsMyTurn => !failed && messagesDone < 3 && (messagesDone % 2 == 0) == IsInitiator;

        /// <summary>Gets the peer's authenticated static key, known once its encrypted static has been read.</summary>
        internal byte[] RemoteStaticKey =>
            remoteStatic != null ? (byte[])remoteStatic.Clone() : throw new InvalidOperationException("The peer's static key is not known yet.");

        /// <summary>Gets the handshake hash, which binds the whole transcript and serves as a channel binding.</summary>
        internal byte[] HandshakeHash =>
            IsComplete ? (byte[])symmetric.Hash.Clone() : throw new InvalidOperationException("The handshake is not complete.");

        /// <summary>Writes this side's next message, carrying <paramref name="payload"/>.</summary>
        internal byte[] WriteMessage(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (!IsMyTurn) throw new InvalidOperationException("It is not this side's turn to write a handshake message.");
            var overhead = messagesDone switch
            {
                0 => KeySize,
                1 => KeySize + EncryptedKeySize + CipherState.TagSize,
                _ => EncryptedKeySize + CipherState.TagSize
            };
            if (payload.Length > MaximumMessageSize - overhead)
            {
                throw new ArgumentException("The payload makes the handshake message too large.", nameof(payload));
            }

            byte[] message;
            switch (messagesDone)
            {
                case 0:
                    CreateEphemeral();
                    symmetric.MixHash(ephemeralPublic!);
                    message = Concatenate(ephemeralPublic!, symmetric.EncryptAndHash(payload));
                    break;
                case 1:
                    CreateEphemeral();
                    symmetric.MixHash(ephemeralPublic!);
                    MixAgreement(ephemeralPrivate!, remoteEphemeral!);
                    var encryptedStatic = symmetric.EncryptAndHash(staticPublic);
                    MixAgreement(staticPrivate, remoteEphemeral!);
                    message = Concatenate(ephemeralPublic!, encryptedStatic, symmetric.EncryptAndHash(payload));
                    break;
                default:
                    var encryptedInitiatorStatic = symmetric.EncryptAndHash(staticPublic);
                    MixAgreement(staticPrivate, remoteEphemeral!);
                    message = Concatenate(encryptedInitiatorStatic, symmetric.EncryptAndHash(payload));
                    break;
            }

            messagesDone++;
            return message;
        }

        /// <summary>
        /// Reads the peer's next message. Returns false, ending the handshake, when it is malformed or does not
        /// authenticate.
        /// </summary>
        internal bool TryReadMessage(byte[] message, out byte[] payload)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (failed || messagesDone >= 3 || IsMyTurn) throw new InvalidOperationException("It is not this side's turn to read a handshake message.");
            payload = Array.Empty<byte>();
            if (message.Length > MaximumMessageSize || !TryRead(message, out payload))
            {
                failed = true;
                payload = Array.Empty<byte>();
                return false;
            }

            messagesDone++;
            return true;
        }

        /// <summary>Derives the transport cipher states once, after the third message.</summary>
        internal TransportKeys Split()
        {
            if (!IsComplete) throw new InvalidOperationException("The handshake is not complete.");
            if (split) throw new InvalidOperationException("The transport keys were already derived.");
            split = true;
            var first = new byte[Hkdf.HashSize];
            var second = new byte[Hkdf.HashSize];
            symmetric.Split(first, second);
            var initiatorToResponder = new CipherState();
            var responderToInitiator = new CipherState();
            initiatorToResponder.InitializeKey(first);
            responderToInitiator.InitializeKey(second);
            Array.Clear(first, 0, first.Length);
            Array.Clear(second, 0, second.Length);
            return IsInitiator
                ? new TransportKeys(initiatorToResponder, responderToInitiator)
                : new TransportKeys(responderToInitiator, initiatorToResponder);
        }

        public void Dispose()
        {
            Array.Clear(staticPrivate, 0, staticPrivate.Length);
            if (ephemeralPrivate != null) Array.Clear(ephemeralPrivate, 0, ephemeralPrivate.Length);
            symmetric.Dispose();
        }

        private bool TryRead(byte[] message, out byte[] payload)
        {
            payload = Array.Empty<byte>();
            switch (messagesDone)
            {
                case 0:
                    if (message.Length < KeySize) return false;
                    remoteEphemeral = Slice(message, 0, KeySize);
                    symmetric.MixHash(remoteEphemeral);
                    return symmetric.TryDecryptAndHash(Slice(message, KeySize, message.Length - KeySize), out payload);
                case 1:
                    if (message.Length < KeySize + EncryptedKeySize + CipherState.TagSize) return false;
                    remoteEphemeral = Slice(message, 0, KeySize);
                    symmetric.MixHash(remoteEphemeral);
                    if (!TryMixAgreement(ephemeralPrivate!, remoteEphemeral)) return false;
                    if (!symmetric.TryDecryptAndHash(Slice(message, KeySize, EncryptedKeySize), out var responderStatic)) return false;
                    remoteStatic = responderStatic;
                    if (!TryMixAgreement(ephemeralPrivate!, remoteStatic)) return false;
                    var offset = KeySize + EncryptedKeySize;
                    return symmetric.TryDecryptAndHash(Slice(message, offset, message.Length - offset), out payload);
                default:
                    if (message.Length < EncryptedKeySize + CipherState.TagSize) return false;
                    if (!symmetric.TryDecryptAndHash(Slice(message, 0, EncryptedKeySize), out var initiatorStatic)) return false;
                    remoteStatic = initiatorStatic;
                    if (!TryMixAgreement(ephemeralPrivate!, remoteStatic)) return false;
                    return symmetric.TryDecryptAndHash(Slice(message, EncryptedKeySize, message.Length - EncryptedKeySize), out payload);
            }
        }

        private void CreateEphemeral()
        {
            var key = ephemeralSource();
            if (key == null || key.Length != KeySize) throw new InvalidOperationException("The ephemeral key source returned an invalid key.");
            ephemeralPrivate = (byte[])key.Clone();
            ephemeralPublic = X25519.DerivePublicKey(ephemeralPrivate);
        }

        /// <summary>Mixes a Diffie-Hellman result into the chaining key; a low-order peer key fails the handshake.</summary>
        private void MixAgreement(byte[] privateKey, byte[] publicKey)
        {
            if (!TryMixAgreement(privateKey, publicKey))
            {
                failed = true;
                throw new InvalidOperationException("The peer's key is a low-order point.");
            }
        }

        private bool TryMixAgreement(byte[] privateKey, byte[] publicKey)
        {
            var shared = new byte[KeySize];
            try
            {
                if (!X25519.TryAgree(privateKey, publicKey, shared)) return false;
                symmetric.MixKey(shared);
                return true;
            }
            finally
            {
                Array.Clear(shared, 0, shared.Length);
            }
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, offset, slice, 0, count);
            return slice;
        }

        private static byte[] Concatenate(params byte[][] parts)
        {
            var length = 0;
            foreach (var part in parts) length += part.Length;
            var result = new byte[length];
            var offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }

        /// <summary>The Noise SymmetricState: chaining key, transcript hash and the handshake's cipher state.</summary>
        private sealed class SymmetricState : IDisposable
        {
            private readonly SHA256 sha256 = SHA256.Create();
            private readonly CipherState cipher = new CipherState();
            private byte[] chainingKey;

            internal SymmetricState()
            {
                var name = Encoding.ASCII.GetBytes(ProtocolName);
                Hash = new byte[Hkdf.HashSize];
                if (name.Length <= Hkdf.HashSize) Buffer.BlockCopy(name, 0, Hash, 0, name.Length);
                else Hash = sha256.ComputeHash(name);
                chainingKey = (byte[])Hash.Clone();
            }

            internal byte[] Hash { get; private set; }

            internal void MixKey(byte[] inputKeyMaterial)
            {
                var nextChainingKey = new byte[Hkdf.HashSize];
                var key = new byte[Hkdf.HashSize];
                Hkdf.DeriveTwo(chainingKey, inputKeyMaterial, nextChainingKey, key);
                Array.Clear(chainingKey, 0, chainingKey.Length);
                chainingKey = nextChainingKey;
                cipher.InitializeKey(key);
                Array.Clear(key, 0, key.Length);
            }

            internal void MixHash(byte[] data) => Hash = sha256.ComputeHash(Concatenate(Hash, data));

            internal byte[] EncryptAndHash(byte[] plaintext)
            {
                var ciphertext = cipher.EncryptWithAd(Hash, plaintext);
                MixHash(ciphertext);
                return ciphertext;
            }

            internal bool TryDecryptAndHash(byte[] ciphertext, out byte[] plaintext)
            {
                if (!cipher.TryDecryptWithAd(Hash, ciphertext, out plaintext)) return false;
                MixHash(ciphertext);
                return true;
            }

            internal void Split(byte[] first, byte[] second) => Hkdf.DeriveTwo(chainingKey, Array.Empty<byte>(), first, second);

            public void Dispose()
            {
                Array.Clear(chainingKey, 0, chainingKey.Length);
                sha256.Dispose();
            }
        }
    }

    /// <summary>The transport cipher states a completed handshake yields for one side.</summary>
    internal sealed class TransportKeys
    {
        internal TransportKeys(CipherState send, CipherState receive)
        {
            Send = send;
            Receive = receive;
        }

        internal CipherState Send { get; }

        internal CipherState Receive { get; }
    }
}
