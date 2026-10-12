using System;
using System.Text;
using TopiaForge.Multiplayer.Transport.Crypto;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// A server descriptor with the host's signature, as the descriptor endpoint and the control plane serve it: the
    /// canonical JSON object <c>{"descriptor":{...},"signature":"..."}</c>. The signature is Ed25519 over a fixed
    /// 32-byte context label followed by the descriptor's canonical bytes, so it cannot be replayed as a signature over
    /// anything else.
    /// </summary>
    internal sealed class SignedServerDescriptor
    {
        /// <summary>The largest signed descriptor anyone accepts, in bytes.</summary>
        internal const int MaximumBytes = 256 * 1024;

        private static readonly byte[] Context = Encoding.ASCII.GetBytes("TopiaForge server descriptor v1\n");

        private readonly byte[] signature;
        private readonly byte[] bytes;

        /// <summary>Takes ownership of <paramref name="signature"/> and <paramref name="bytes"/>; callers pass fresh arrays.</summary>
        private SignedServerDescriptor(ServerDescriptor descriptor, byte[] signature, byte[] bytes)
        {
            Descriptor = descriptor;
            this.signature = signature;
            this.bytes = bytes;
            Sha256 = WireText.ToHex(WireText.Sha256(bytes));
        }

        internal ServerDescriptor Descriptor { get; }

        /// <summary>Gets a copy of the signature.</summary>
        internal byte[] Signature => (byte[])signature.Clone();

        /// <summary>Gets a copy of the exact canonical bytes that are served and pinned.</summary>
        internal byte[] Bytes => (byte[])bytes.Clone();

        /// <summary>Gets the length of <see cref="Bytes"/> without copying them.</summary>
        internal int Length => bytes.Length;

        /// <summary>Gets the lowercase hexadecimal SHA-256 of <see cref="Bytes"/>, which a join plan pins.</summary>
        internal string Sha256 { get; }

        /// <summary>Signs <paramref name="descriptor"/> with the host's Ed25519 secret key, which must match its signing key.</summary>
        internal static SignedServerDescriptor Sign(ServerDescriptor descriptor, byte[] signingSecretKey)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (!WireText.FixedTimeEquals(Ed25519.DerivePublicKey(signingSecretKey), descriptor.SigningKey))
            {
                throw new ArgumentException("The secret key does not belong to the descriptor's signing key.", nameof(signingSecretKey));
            }

            var signature = Ed25519.Sign(signingSecretKey, SignedMessage(CanonicalJson.Serialize(descriptor.ToJson())));
            var bytes = CanonicalJson.Serialize(Envelope(descriptor, signature));
            if (bytes.Length > MaximumBytes) throw new ArgumentException("The signed descriptor is larger than 256 KiB.", nameof(descriptor));
            return new SignedServerDescriptor(descriptor, signature, bytes);
        }

        /// <summary>
        /// Checks a served descriptor: canonical JSON within the size bound, every member valid, the signature good for
        /// the signing key inside it, and, when <paramref name="expectedServerId"/> is given, that key's hash equal to
        /// it. Only a descriptor that passes may pin a transport key.
        /// </summary>
        internal static bool TryVerify(byte[] bytes, string? expectedServerId, out SignedServerDescriptor? verified, out string error)
        {
            verified = null;
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (!CanonicalJson.TryParse(bytes, MaximumBytes, out var node, out error)) return false;
            if (!(node is JsonObjectNode envelope) || !envelope.HasExactly("descriptor", "signature"))
            {
                error = "A signed descriptor has exactly a descriptor and a signature.";
                return false;
            }

            ServerDescriptor descriptor;
            byte[] signature;
            try
            {
                if (!WireText.TryParseBase64Url(JsonRead.String(envelope, "signature"), Ed25519.SignatureSize, out signature))
                {
                    error = "The signature is malformed.";
                    return false;
                }

                envelope.TryGet("descriptor", out var descriptorNode);
                descriptor = ServerDescriptor.FromJson(descriptorNode);
            }
            catch (FormatException exception)
            {
                error = exception.Message;
                return false;
            }

            if (expectedServerId != null && !string.Equals(descriptor.ServerId, expectedServerId, StringComparison.Ordinal))
            {
                error = "The descriptor belongs to another server.";
                return false;
            }

            if (!Ed25519.Verify(signature, descriptor.SigningKey, SignedMessage(CanonicalJson.Serialize(descriptor.ToJson()))))
            {
                error = "The descriptor signature does not verify.";
                return false;
            }

            verified = new SignedServerDescriptor(descriptor, signature, (byte[])bytes.Clone());
            error = string.Empty;
            return true;
        }

        private static JsonObjectNode Envelope(ServerDescriptor descriptor, byte[] signature) =>
            new JsonObjectNode().Add("descriptor", descriptor.ToJson()).Add("signature", WireText.ToBase64Url(signature));

        private static byte[] SignedMessage(byte[] canonicalDescriptor)
        {
            var message = new byte[Context.Length + canonicalDescriptor.Length];
            Buffer.BlockCopy(Context, 0, message, 0, Context.Length);
            Buffer.BlockCopy(canonicalDescriptor, 0, message, Context.Length, canonicalDescriptor.Length);
            return message;
        }
    }
}
