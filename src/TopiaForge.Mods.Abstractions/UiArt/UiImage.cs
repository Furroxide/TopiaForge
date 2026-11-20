using System;

namespace TopiaForge.Mods
{
    /// <summary>Controls how an image preserves its aspect ratio within its bounds.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiImageFit
    {
        /// <summary>Shows the whole image, with unused space around it as needed.</summary>
        Contain = 0,
        /// <summary>Fills the image bounds with a centered crop.</summary>
        Cover = 1
    }

    /// <summary>Draws a verified image from the calling mod's package.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiImage : UiNode
    {
        /// <summary>Creates an image with a plain-text label and optional independently sized axes.</summary>
        /// <remarks>
        /// Each explicit axis must be between 1 and 1,024 reference units. An omitted axis uses the declared
        /// sprite's pixel dimension and is subject to the same limit. Contain and cover preserve the sprite's aspect ratio.
        /// The host verifies package ownership, image metadata, and mask format before rendering. A tint is allowed only for a mask.
        /// </remarks>
        public UiImage(UiImageRef image, string semanticLabel, float? width = null, float? height = null,
            UiImageFit fit = UiImageFit.Contain, UiTone? tint = null,
            bool decorative = false, string? id = null)
            : this(image, semanticLabel, width, height, fit, tint, decorative, id, default)
        {
        }

        internal UiImage(UiImageRef image, string semanticLabel, float width, float height, UiImageFit fit,
            UiTone? tint, bool decorative, string? id, ReadOnlySpan<byte> packDescriptorDigest)
            : this(image, semanticLabel, (float?)width, (float?)height, fit, tint, decorative, id, CopyDigest(packDescriptorDigest))
        {
        }

        private UiImage(UiImageRef image, string semanticLabel, float? width, float? height, UiImageFit fit,
            UiTone? tint, bool decorative, string? id, ReadOnlyMemory<byte> packDescriptorDigest)
            : base(id)
        {
            Image = image ?? throw new ArgumentNullException(nameof(image));
            UiArtValues.Label(semanticLabel, decorative, nameof(semanticLabel));
            UiArtValues.ImageDimension(width, nameof(width));
            UiArtValues.ImageDimension(height, nameof(height));
            if (!Enum.IsDefined(typeof(UiImageFit), fit))
                throw new ArgumentOutOfRangeException(nameof(fit));
            if (tint is UiTone tone && !Enum.IsDefined(typeof(UiTone), tone))
                throw new ArgumentOutOfRangeException(nameof(tint));

            SemanticLabel = semanticLabel;
            Width = width;
            Height = height;
            Fit = fit;
            Tint = tint;
            Decorative = decorative;
            PackDescriptorDigest = packDescriptorDigest;
        }

        /// <summary>Gets the package-local image reference.</summary>
        public UiImageRef Image { get; }
        /// <summary>Gets the authored semantic label, or an empty string for decorative art.</summary>
        public string SemanticLabel { get; }
        /// <summary>Gets the requested width, or null to use the verified sprite width.</summary>
        public float? Width { get; }
        /// <summary>Gets the requested height, or null to use the verified sprite height.</summary>
        public float? Height { get; }
        /// <summary>Gets the aspect-preserving fit within the allocated image bounds.</summary>
        public UiImageFit Fit { get; }
        /// <summary>Gets the mask tint, or null to preserve the image's own colors.</summary>
        public UiTone? Tint { get; }
        /// <summary>Gets whether the image is purely visual.</summary>
        public bool Decorative { get; }

        // Present on an outgoing normalized image or decoded presentation only.
        internal ReadOnlyMemory<byte> PackDescriptorDigest { get; }

        internal UiImage WithWireMetadata(float width, float height, ReadOnlySpan<byte> packDescriptorDigest) =>
            new UiImage(Image, SemanticLabel, width, height, Fit, Tint, Decorative, Id, packDescriptorDigest);

        private static ReadOnlyMemory<byte> CopyDigest(ReadOnlySpan<byte> packDescriptorDigest)
        {
            if (packDescriptorDigest.Length != 32)
                throw new ArgumentException("A remote image requires its package-local theme descriptor's SHA-256 digest.",
                    nameof(packDescriptorDigest));
            return packDescriptorDigest.ToArray();
        }
    }

    internal static class UiArtValues
    {
        internal static void Label(string text, bool decorative, string parameter)
        {
            if (text == null) throw new ArgumentNullException(parameter);
            if (decorative)
            {
                if (text.Length != 0)
                    throw new ArgumentException("Decorative art cannot also have a semantic label.", parameter);
            }
            else if (text.Length > 256 || string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("A plain-text label of 1 to 256 units is required.", parameter);
            }
        }

        internal static void ImageDimension(float? value, string parameter)
        {
            if (value is float size && !(size >= 1f && size <= 1024f))
                throw new ArgumentOutOfRangeException(parameter, "Image dimensions must be between 1 and 1,024 units.");
        }

        internal static void PaintRole(UiPaintRole role, string parameter)
        {
            if (!Enum.IsDefined(typeof(UiPaintRole), role))
                throw new ArgumentOutOfRangeException(parameter);
        }
    }
}
