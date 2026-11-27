using System;

namespace TopiaForge.Mods
{
    /// <summary>Identifies an image declared by a theme pack in the calling mod's package.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiImageRef : IEquatable<UiImageRef>
    {
        /// <summary>Creates an ordinal reference from the ids in the package catalogue and theme definition.</summary>
        public UiImageRef(string packId, string imageId)
        {
            if (!UiThemeIds.IsAuthorId(packId))
                throw new ArgumentException("The pack id must be a package-local lowercase identifier.", nameof(packId));
            if (!UiThemeIds.IsAuthorId(imageId))
                throw new ArgumentException("The image id must be a package-local lowercase identifier.", nameof(imageId));

            PackId = packId;
            ImageId = imageId;
        }

        /// <summary>Gets the package-local theme pack id.</summary>
        public string PackId { get; }

        /// <summary>Gets the image id within the declared theme pack.</summary>
        public string ImageId { get; }

        /// <inheritdoc/>
        public bool Equals(UiImageRef? other) =>
            other != null && StringComparer.Ordinal.Equals(PackId, other.PackId) &&
            StringComparer.Ordinal.Equals(ImageId, other.ImageId);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is UiImageRef other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(PackId), StringComparer.Ordinal.GetHashCode(ImageId));
    }
}
