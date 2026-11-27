using System;

namespace TopiaForge.Mods
{
    /// <summary>Projects artwork without rebuilding unrelated trees or rebinding countdowns.</summary>
    internal static class UiImageProjection
    {
        internal static UiNode? Rewrite(UiNode? node, Func<UiImage, UiImage?> project) =>
            UiTreeProjection.Rewrite(node, candidate => candidate is UiImage image ? project(image) : candidate);

        internal static UiImage ForActionSlot(UiActionSlot slot, bool remote)
        {
            var size = UiActionSlotGeometry.IconSize;
            var image = new UiImage(slot.Image!, string.Empty, width: size, height: size, decorative: true);
            return remote ? image.WithWireMetadata(size, size, slot.PackDescriptorDigest.Span) : image;
        }

        internal static UiThemeImage? Find(UiThemeDescriptor pack, string id)
        {
            for (var index = 0; index < pack.Images.Count; index++)
                if (string.Equals(pack.Images[index].Id, id, StringComparison.Ordinal)) return pack.Images[index];
            return null;
        }

        internal static UiImageFrame ResolveFrame(UiImage image, UiThemeImage metadata)
        {
            if (metadata.PixelWidth is not int width || metadata.PixelHeight is not int height || metadata.IsMask is not bool mask)
                throw new ArgumentException("$.images: The image has incomplete admitted sprite metadata.");
            return UiImageFrame.Resolve(image, width, height, mask);
        }

        internal static UiLayoutRect FixedBox(UiLayoutRect allocated, float width, float height) =>
            new UiLayoutRect(allocated.X, allocated.Y, width, height);

    }
}
