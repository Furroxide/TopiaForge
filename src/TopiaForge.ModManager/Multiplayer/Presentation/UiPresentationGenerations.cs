using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The retained surface identities of one authenticated receive route. Each route owns this ledger and clears it
    /// with its stream. Baseline admission reads the old high-water marks until the full replacement has validated.
    /// </summary>
    internal sealed class UiPresentationGenerations
    {
        private readonly Dictionary<PackageIdentity, ulong> high = new Dictionary<PackageIdentity, ulong>();
        private readonly Dictionary<ulong, Identity> active = new Dictionary<ulong, Identity>();

        internal bool Allows(ulong token, UiWireEnvelope envelope)
        {
            var proposed = new Identity(envelope);
            if (active.TryGetValue(token, out var current) && !current.Package.Equals(proposed.Package)) return false;
            if (!high.TryGetValue(proposed.Package, out var mark) || proposed.Generation > mark) return true;
            foreach (var open in active.Values)
                if (open.Generation == proposed.Generation && open.Package.Equals(proposed.Package)) return true;
            return false;
        }

        internal void ClearActive() => active.Clear();

        internal void Commit(ulong token, UiWireEnvelope? envelope)
        {
            if (envelope == null)
            {
                active.Remove(token);
                return;
            }

            var value = new Identity(envelope);
            active[token] = value;
            if (!high.TryGetValue(value.Package, out var mark) || value.Generation > mark) high[value.Package] = value.Generation;
        }

        internal void Reset()
        {
            active.Clear();
            high.Clear();
        }

        private readonly struct Identity
        {
            internal Identity(UiWireEnvelope envelope)
            {
                Package = new PackageIdentity(envelope.OwnerPackageId, envelope.PackageDigest.Span);
                Generation = envelope.Generation;
            }

            internal PackageIdentity Package { get; }
            internal ulong Generation { get; }
        }

        private readonly struct PackageIdentity : IEquatable<PackageIdentity>
        {
            private readonly string id;
            private readonly ulong a, b, c, d;

            internal PackageIdentity(string id, ReadOnlySpan<byte> digest)
            {
                this.id = id;
                a = BinaryPrimitives.ReadUInt64LittleEndian(digest);
                b = BinaryPrimitives.ReadUInt64LittleEndian(digest.Slice(8));
                c = BinaryPrimitives.ReadUInt64LittleEndian(digest.Slice(16));
                d = BinaryPrimitives.ReadUInt64LittleEndian(digest.Slice(24));
            }

            public bool Equals(PackageIdentity other) => a == other.a && b == other.b && c == other.c && d == other.d
                && string.Equals(id, other.id, StringComparison.Ordinal);
            public override bool Equals(object? obj) => obj is PackageIdentity other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(id, a, b, c, d);
        }
    }
}
