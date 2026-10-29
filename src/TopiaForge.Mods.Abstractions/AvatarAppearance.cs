using System;

namespace TopiaForge.Mods
{
    /// <summary>The cosmetic slot for one avatar accessory; body parts and held gameplay items are excluded.</summary>
    [RequiresTopiaForge("0.2.0")]
    public enum AvatarAccessorySlot
    {
        /// <summary>A hat above the head.</summary>
        Hat,
        /// <summary>Glasses on the face.</summary>
        Glasses,
        /// <summary>Hair on the head.</summary>
        Hair,
        /// <summary>A beard on the face.</summary>
        Beard,
        /// <summary>A necklace.</summary>
        Necklace
    }

    /// <summary>Immutable cosmetic colours and accessory, independent of player identity, health and movement.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class AvatarAppearance : IEquatable<AvatarAppearance>
    {
        /// <summary>Creates opaque plastic and metal colours. Accessory is a catalog prefab name (at most 64 printable ASCII
        /// characters), null to retain the default, or empty to remove the selected slot. No asset paths or URLs are accepted.</summary>
        public AvatarAppearance(RgbaColor plastic, RgbaColor metal, string? accessory = null, AvatarAccessorySlot accessorySlot = AvatarAccessorySlot.Hat)
        {
            if (!ValidColor(plastic) || !ValidColor(metal)) throw new ArgumentOutOfRangeException(nameof(plastic), "Avatar colours must be finite, opaque and between zero and one.");
            if (!ValidAccessory(accessory)) throw new ArgumentException("Use a catalog accessory name of at most 64 printable characters, without path separators or colons.", nameof(accessory));
            if (accessorySlot < AvatarAccessorySlot.Hat || accessorySlot > AvatarAccessorySlot.Necklace) throw new ArgumentOutOfRangeException(nameof(accessorySlot));
            Plastic = plastic; Metal = metal; Accessory = accessory; AccessorySlot = accessorySlot;
        }
        /// <summary>Gets the plastic colour.</summary>
        public RgbaColor Plastic { get; }
        /// <summary>Gets the exterior metal colour.</summary>
        public RgbaColor Metal { get; }
        /// <summary>Gets the catalog accessory name, null for the default, or empty for none.</summary>
        public string? Accessory { get; }
        /// <summary>Gets the cosmetic accessory slot.</summary>
        public AvatarAccessorySlot AccessorySlot { get; }
        /// <inheritdoc/>
        public bool Equals(AvatarAppearance? other) => other != null && Plastic.Equals(other.Plastic) && Metal.Equals(other.Metal)
            && Accessory == other.Accessory && AccessorySlot == other.AccessorySlot;
        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is AvatarAppearance other && Equals(other);
        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Plastic, Metal, Accessory, AccessorySlot);
        internal static bool ValidColor(RgbaColor c) => Unit(c.Red) && Unit(c.Green) && Unit(c.Blue) && c.Alpha == 1f;
        private static bool Unit(float n) => !float.IsNaN(n) && n >= 0 && n <= 1;
        internal static bool ValidAccessory(string? value)
        {
            if (value == null) return true;
            if (value.Length > 64) return false;
            foreach (var c in value) if (c < ' ' || c > '~' || c == '/' || c == '\\' || c == ':') return false;
            return true;
        }
    }
}
