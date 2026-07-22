using System;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// Internal routing key for provider data. The public contract id is deliberately
    /// left unchanged; the owner component exists only inside the provider.
    /// </summary>
    internal readonly struct LoopbackOwnerScopedId : IEquatable<LoopbackOwnerScopedId>
    {
        internal LoopbackOwnerScopedId(string ownerModId, string publicId)
        {
            OwnerModId = ownerModId ?? throw new ArgumentNullException(nameof(ownerModId));
            PublicId = publicId ?? throw new ArgumentNullException(nameof(publicId));
        }

        internal string OwnerModId { get; }

        internal string PublicId { get; }

        public bool Equals(LoopbackOwnerScopedId other) =>
            string.Equals(OwnerModId, other.OwnerModId, StringComparison.Ordinal) &&
            string.Equals(PublicId, other.PublicId, StringComparison.Ordinal);

        public override bool Equals(object? obj) =>
            obj is LoopbackOwnerScopedId other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                return (StringComparer.Ordinal.GetHashCode(OwnerModId) * 397) ^
                       StringComparer.Ordinal.GetHashCode(PublicId);
            }
        }
    }
}
