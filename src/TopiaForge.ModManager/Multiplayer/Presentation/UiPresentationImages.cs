using System;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Projects package-local image metadata into an immutable, bounded presentation tree.</summary>
    internal static class UiPresentationImages
    {
        internal static OperationResult<bool> Prepare(UiNode? content, UiThemeScope? scope, UiThemeDescriptor? theme,
            out UiNode? presentation)
        {
            presentation = null;
            try
            {
                // Check depth and node count before walking either the styles or the image references.
                UiComposition.Validate(content);
                var styles = UiPresentationTheme.StylesValid(content, theme);
                if (!styles.Succeeded) return styles;
                if (content == null) return UiPresentationTheme.Allowed;
                presentation = UiImageProjection.Rewrite(content, image => NormalizeImage(image, scope));
                return presentation == null ? ImageMissing : UiPresentationTheme.Allowed;
            }
            catch (ArgumentException error)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, error.Message);
            }
        }

        private static readonly OperationResult<bool> ImageMissing = OperationResult<bool>.Failure(ModErrorCode.NotFound,
            "$.images: This package does not declare the requested UI image.");

        private static UiImage? NormalizeImage(UiImage image, UiThemeScope? scope)
        {
            if (scope == null || !scope.TryDescribe(image.Image.PackId, out var pack) || pack == null ||
                UiImageProjection.Find(pack, image.Image.ImageId) is not { } metadata)
                return null;
            var frame = UiImageProjection.ResolveFrame(image, metadata);
            return image.Width == frame.Width && image.Height == frame.Height &&
                image.PackDescriptorDigest.Span.SequenceEqual(pack.Digest.Span) ? image
                : image.WithWireMetadata(frame.Width, frame.Height, pack.Digest.Span);
        }

        internal static bool ViewerAdmits(UiNode? node, UiRemoteThemeSelection selection)
        {
            var catalogue = selection.Package?.Manifest.AdmittedUi;
            return catalogue == null || Admits(node, catalogue);
        }

        private static bool Admits(UiNode? node, AdmittedUiCatalogue catalogue)
        {
            if (node == null) return true;
            if (node is UiImage image)
            {
                // Unknown or different descriptors have no local art authority. Keep the transmitted box for fallback.
                if (!catalogue.TryGet(image.Image.PackId, out var pack) ||
                    !image.PackDescriptorDigest.Span.SequenceEqual(pack.Digest.Span)) return true;
                var metadata = UiImageProjection.Find(pack, image.Image.ImageId);
                if (metadata == null) return false;
                try { _ = UiImageProjection.ResolveFrame(image, metadata); }
                catch (ArgumentException) { return false; }
                return true;
            }

            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++)
                if (!Admits(children[index], catalogue)) return false;
            return true;
        }

    }
}
