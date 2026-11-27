using System;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>How a path reaches the peer. It decides nothing in the transport today; diagnostics count by it.</summary>
    internal enum TransportPathKind
    {
        Direct = 0,
        Lan = 1,
        Relayed = 2
    }

    /// <summary>
    /// One network path as the I/O layer knows it: a UDP endpoint or a relay channel. The transport compares paths and
    /// binds cookies to their key, and nothing else. The key never leaves the transport and network layers: not to
    /// the engine, mods, logs or diagnostics.
    /// </summary>
    internal sealed class TransportPath : IEquatable<TransportPath>
    {
        internal const int MaximumKeyBytes = 64;

        private readonly byte[] key;
        private readonly int hash;

        internal TransportPath(byte[] key, TransportPathKind kind)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (key.Length == 0 || key.Length > MaximumKeyBytes) throw new ArgumentException("A path key is 1 to 64 bytes.", nameof(key));
            this.key = (byte[])key.Clone();
            Kind = kind;
            unchecked
            {
                var value = (int)2166136261;
                foreach (var item in this.key) value = (value ^ item) * 16777619;
                hash = value;
            }
        }

        internal TransportPathKind Kind { get; }

        /// <summary>Gets the key bytes. Only the cookie guard reads them.</summary>
        internal byte[] KeyBytes => key;

        public bool Equals(TransportPath? other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other == null || other.hash != hash || other.key.Length != key.Length) return false;
            for (var index = 0; index < key.Length; index++)
            {
                if (key[index] != other.key[index]) return false;
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is TransportPath other && Equals(other);

        public override int GetHashCode() => hash;

        /// <summary>Never shows the key: a path is not something to print.</summary>
        public override string ToString() => Kind + " path";
    }
}
