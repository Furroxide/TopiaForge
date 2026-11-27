using System;
using System.Security.Cryptography;
using TopiaForge.Multiplayer.Transport.Crypto;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// The network a link's packets came from, as a host's password limits count it: an IPv4 address, or the /64 an
    /// IPv6 address belongs to, never the port. It is a keyed hash that only the network layer can compute, under a secret
    /// each network stack draws for itself, so it names no address: whoever holds one can tell only whether two links
    /// came from the same network of the same host. It is never shown, logged or handed to mods.
    /// </summary>
    internal readonly struct SourceNetwork : IEquatable<SourceNetwork>
    {
        private readonly ulong high;
        private readonly ulong low;

        internal SourceNetwork(ulong high, ulong low)
        {
            this.high = high;
            this.low = low;
        }

        public bool Equals(SourceNetwork other) => high == other.high && low == other.low;

        public override bool Equals(object? obj) => obj is SourceNetwork other && Equals(other);

        public override int GetHashCode() => unchecked((int)high ^ (int)(high >> 32) ^ ((int)low * 397));

        /// <summary>Never shows the value: a network is not something to print.</summary>
        public override string ToString() => "source network";
    }

    /// <summary>Hashes the network bytes a path names into <see cref="SourceNetwork"/> values, under a secret of its own.</summary>
    internal sealed class SourceNetworks
    {
        private readonly byte[] secret = new byte[32];

        internal SourceNetworks()
        {
            CryptoRandom.Fill(secret);
        }

        internal SourceNetwork Of(byte[] network)
        {
            if (network == null) throw new ArgumentNullException(nameof(network));
            byte[] mac;
            using (var hmac = new HMACSHA256(secret))
            {
                mac = hmac.ComputeHash(network);
            }

            var value = new SourceNetwork(BitConverter.ToUInt64(mac, 0), BitConverter.ToUInt64(mac, 8));
            Array.Clear(mac, 0, mac.Length);
            return value;
        }

        /// <summary>Zeroes the secret once the stack that owns it is gone.</summary>
        internal void Wipe() => Array.Clear(secret, 0, secret.Length);
    }
}
