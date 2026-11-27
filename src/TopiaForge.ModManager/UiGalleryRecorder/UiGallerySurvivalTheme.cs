using System;
using System.IO;
using TopiaForge.Mods;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager
{
    /// <summary>The canonical Survival descriptor shared by the loader preview and the recorder.</summary>
    internal static class UiGallerySurvivalTheme
    {
        private const string Resource = "TopiaForge.UiGallery.survival.json";
        private static readonly Lazy<UiThemeDescriptor> Survival = new Lazy<UiThemeDescriptor>(Load);

        internal static UiThemeDescriptor Descriptor => Survival.Value;

        internal static bool Matches(UiThemeDescriptor? descriptor) =>
            descriptor?.Id == FoundationPages.SurvivalTheme.Id &&
            MatchesDigest(descriptor.Digest.Span);

        internal static bool MatchesDigest(ReadOnlySpan<byte> digest) => digest.SequenceEqual(Descriptor.Digest.Span);

        private static UiThemeDescriptor Load()
        {
            using var stream = typeof(UiGallerySurvivalTheme).Assembly.GetManifestResourceStream(Resource)
                ?? throw new InvalidOperationException("The embedded Gallery Survival definition is missing.");
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var parsed = UiThemePackParser.Parse(bytes.ToArray(), expectedId: FoundationPages.SurvivalTheme.Id);
            if (!parsed.TryGetValue(out var descriptor) || descriptor == null ||
                descriptor.Fonts.Count != 0 || descriptor.Images.Count != 0)
                throw new InvalidOperationException("The embedded Gallery Survival definition could not be loaded.");
            return descriptor;
        }
    }
}
