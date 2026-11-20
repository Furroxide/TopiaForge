using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Core
{
    internal static class UiThemePackage
    {
        private const string ManifestPath = "topiaforge.mod.json";

        internal static OperationResult<AdmittedUiCatalogue> Read(string packageRoot, ModManifest manifest) =>
            Read(packageRoot, manifest, preparation: null);

        // Called by the packer in its disposable staging directory. All definitions and resources
        // must validate before any enriched definition or catalogue is written.
        internal static IReadOnlyList<string> Prepare(string packageRoot, ModManifest manifest)
        {
            if (manifest.Ui == null) return Array.Empty<string>();
            var catalogue = new ModUiCatalogue { Schema = 1, Default = manifest.Ui.Default };
            var preparation = new Preparation(catalogue);
            var result = Read(packageRoot, manifest, preparation);
            if (!result.Succeeded) return new[] { result.ErrorMessage };

            foreach (var definition in preparation.Definitions)
                File.WriteAllBytes(definition.Key, definition.Value);

            var manifestFile = Path.Combine(packageRoot, ManifestPath);
            var manifestJson = JsonUtil.ReadBoundedJsonObject(manifestFile);
            var replacement = new Dictionary<string, string> { ["ui"] = JsonUtil.Serialize(catalogue) };
            File.WriteAllText(manifestFile, JsonObjectMerge.Merge(manifestJson, replacement));
            return Array.Empty<string>();
        }

        private static OperationResult<AdmittedUiCatalogue> Read(string packageRoot, ModManifest manifest, Preparation? preparation)
        {
            if (!VersionUtil.TryGetMinimum(manifest.SupportedLoaderVersionRange, out var minimum, out _) ||
                minimum == null || !VersionUtil.IsAtLeast(minimum, UiKitRelease.K1))
                return OperationResult<AdmittedUiCatalogue>.Failure(
                    ModErrorCode.InvalidArgument, "ui needs TopiaForge " + UiKitRelease.K1 + " or newer. Raise supportedLoaderVersionRange.");

            var packaged = preparation == null;
            var catalogue = UiThemeCatalogueParser.Parse(
                Encoding.UTF8.GetBytes(JsonUtil.Serialize(manifest.Ui!)), packaged);
            if (!catalogue.TryGetValue(out var declaration))
                return OperationResult<AdmittedUiCatalogue>.Failure(catalogue.ErrorCode, catalogue.ErrorMessage);

            var definitions = new Dictionary<string, UiThemeDescriptor>(StringComparer.Ordinal);
            var files = new UiThemePackageFiles(packageRoot);
            using var sha = SHA256.Create();
            for (var index = 0; index < declaration.Themes.Count; index++)
            {
                var entry = declaration.Themes[index];
                try
                {
                    var path = files.ResolveDefinition(entry.Definition);
                    var bytes = UiThemePackageFiles.ReadBounded(path, UiThemeJson.MaximumBytes, "$");
                    if (packaged && !entry.Matches(bytes))
                        return Failure(ModErrorCode.InvalidArgument, index, "does not match its packaged SHA-256.");

                    var parsed = UiThemePackParser.Parse(bytes, packaged, entry.Id, files.HashBundle, files.ImageMetadata);
                    if (!parsed.TryGetValue(out var descriptor))
                        return Failure(parsed.ErrorCode, index, parsed.ErrorMessage);
                    files.CheckFonts(descriptor);
                    definitions.Add(entry.Id, descriptor);

                    if (preparation != null)
                    {
                        var final = Enrich(bytes, descriptor);
                        if (final.Length > UiThemeJson.MaximumBytes)
                            return Failure(ModErrorCode.RateLimited, index, "exceeds 64 KiB after completing image metadata.");
                        var digest = UiThemeData.Hex(sha.ComputeHash(final));
                        if (entry.Sha256 != null && !string.Equals(entry.Sha256, digest, StringComparison.Ordinal))
                            return Failure(ModErrorCode.InvalidArgument, index, "sha256 does not match the final definition bytes.");
                        preparation.Add(entry, path, ReferenceEquals(bytes, final) ? null : final, digest.ToLowerInvariant());
                    }
                }
                catch (UiThemeDataException error)
                {
                    return Failure(error.Code, index, error.Message);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return Failure(ModErrorCode.Io, index, "cannot read the declared package file.");
                }
            }

            return OperationResult<AdmittedUiCatalogue>.Success(new AdmittedUiCatalogue(declaration.Default, definitions, files.Bundles));
        }

        private static byte[] Enrich(byte[] bytes, UiThemeDescriptor descriptor)
        {
            if (descriptor.Images.Count == 0) return bytes;

            var json = Encoding.UTF8.GetString(bytes);
            var rawImages = JsonObjectMerge.ReadProperties(json).Single(property => property.Name == "images");
            var images = JsonObjectMerge.ReadArrayValues(rawImages.RawValue).ToArray();
            var changed = false;
            for (var index = 0; index < images.Length; index++)
            {
                var image = descriptor.Images[index];
                var fields = JsonObjectMerge.ReadProperties(images[index]);
                var facts = new Dictionary<string, string>(StringComparer.Ordinal);
                if (!fields.Any(property => property.Name == "pixelWidth"))
                    facts["pixelWidth"] = image.PixelWidth!.Value.ToString(CultureInfo.InvariantCulture);
                if (!fields.Any(property => property.Name == "pixelHeight"))
                    facts["pixelHeight"] = image.PixelHeight!.Value.ToString(CultureInfo.InvariantCulture);
                if (!fields.Any(property => property.Name == "isMask")) facts["isMask"] = image.IsMask!.Value ? "true" : "false";
                if (facts.Count == 0) continue;
                changed = true;
                images[index] = JsonObjectMerge.Merge(images[index], facts);
            }

            if (!changed) return bytes;
            var replacement = new Dictionary<string, string> { ["images"] = "[" + string.Join(",", images) + "]" };
            return Encoding.UTF8.GetBytes(JsonObjectMerge.Merge(json, replacement));
        }

        private static OperationResult<AdmittedUiCatalogue> Failure(ModErrorCode code, int index, string detail) =>
            OperationResult<AdmittedUiCatalogue>.Failure(code, "ui.themes[" + index + "].definition " + detail);

        private sealed class Preparation
        {
            private readonly ModUiCatalogue catalogue;

            internal Preparation(ModUiCatalogue catalogue) => this.catalogue = catalogue;
            internal Dictionary<string, byte[]> Definitions { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            internal void Add(UiThemeCatalogueEntry entry, string path, byte[]? replacement, string digest)
            {
                if (replacement != null) Definitions.Add(path, replacement);
                catalogue.Themes.Add(new ModUiCatalogueEntry { Id = entry.Id, Definition = entry.Definition, Sha256 = digest });
            }
        }
    }
}
