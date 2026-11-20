using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Validates package bytes referenced by manifest metadata. Ordinary manifest validation intentionally does
    /// not require generated synchronized-content hashes because source manifests are valid before packing.
    /// Package installation, scanning, and release validation use this stricter boundary after packing.
    /// </summary>
    public static class ManifestContentValidator
    {
        public static IReadOnlyList<string> Validate(string packageRoot, ModManifest manifest)
        {
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                throw new ArgumentException("A package root is required.", nameof(packageRoot));
            }

            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            var errors = new List<string>();
            manifest.AdmittedUi = null;
            var synchronizedFiles = manifest.Multiplayer?.SynchronizedFiles ?? new List<string>();
            if (!ModManifest.IsSupportedSchemaVersion(manifest.SchemaVersion)) return errors;
            var requiredFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            var maps = (manifest.Contributions?.Worlds ?? new List<ModWorldDeclaration>())
                .Where(world => world.Content?.Kind == ModWorldContent.MapKind).ToList();
            if (maps.Count > 0 && (!VersionUtil.TryGetMinimum(manifest.SupportedLoaderVersionRange, out var minimum, out _)
                || minimum == null || !VersionUtil.IsAtLeast(minimum, "0.2.0")))
                errors.Add("TFPKG180: map world content needs TopiaForge 0.2.0 or newer. Raise supportedLoaderVersionRange.");
            foreach (var world in maps)
                if (world.Content!.File != null) requiredFiles[world.Content.File] = "map world file";
            var isSession = manifest.Multiplayer?.Mode == ModMultiplayerMetadata.SessionMode;
            if (isSession)
                foreach (var file in synchronizedFiles.Take(ModMultiplayerMetadata.MaxSynchronizedFiles + 1))
                    requiredFiles[file] = "multiplayer.synchronizedFiles entry";

            if (isSession && !synchronizedFiles.Contains(
                    ModMultiplayerMetadata.ContractLockFileName,
                    StringComparer.Ordinal))
            {
                errors.Add(
                    "Session packages must synchronize the canonical generated contract lock '" +
                    ModMultiplayerMetadata.ContractLockFileName + "'. Repack this mod with TopiaForge tooling.");
            }

            var hashes = manifest.Hashes ?? new Dictionary<string, string>();
            foreach (var required in requiredFiles)
            {
                var label = required.Value;
                if (!PortablePackagePath.TryValidate(required.Key, out var portablePath, out _, out _))
                {
                    // ManifestValidator owns the actionable path diagnostic.
                    continue;
                }

                if (!hashes.TryGetValue(portablePath, out var expectedDigest) ||
                    string.IsNullOrWhiteSpace(expectedDigest))
                {
                    errors.Add(
                        label + " '" + portablePath +
                        "' is missing its pack-time SHA-256 in hashes.");
                    continue;
                }

                if (!TryResolveOrdinaryFile(packageRoot, portablePath, out var fullPath, out var pathError))
                {
                    errors.Add(
                        label + " '" + portablePath +
                        "' is not an ordinary package file (" + pathError + ").");
                    continue;
                }

                string actualDigest;
                try
                {
                    actualDigest = ComputeSha256(fullPath);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    errors.Add(
                        label + " '" + portablePath +
                        "' could not be hashed (" + exception.Message + ").");
                    continue;
                }

                if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        label + " '" + portablePath +
                        "' does not match its pack-time SHA-256.");
                }
            }

            if (manifest.Ui != null)
            {
                var ui = ReadUiCatalogue(packageRoot, manifest);
                if (!ui.TryGetValue(out var catalogue)) errors.Add(ui.ErrorMessage);
                else if (errors.Count == 0) manifest.AdmittedUi = catalogue;
            }

            return errors;
        }

        /// <summary>Checks packaged UI definitions without inspecting the mod's managed assembly.</summary>
        public static IReadOnlyList<string> ValidateUiThemes(string packageRoot, ModManifest manifest)
        {
            if (manifest.Ui == null) return Array.Empty<string>();
            var result = ReadUiCatalogue(packageRoot, manifest);
            return result.Succeeded ? Array.Empty<string>() : new[] { result.ErrorMessage };
        }

        private static OperationResult<AdmittedUiCatalogue> ReadUiCatalogue(string packageRoot, ModManifest manifest)
        {
            if (!VersionUtil.TryGetMinimum(manifest.SupportedLoaderVersionRange, out var minimum, out _) ||
                minimum == null || !VersionUtil.IsAtLeast(minimum, UiKitRelease.K1))
                return OperationResult<AdmittedUiCatalogue>.Failure(
                    ModErrorCode.InvalidArgument, "ui needs TopiaForge " + UiKitRelease.K1 + " or newer. Raise supportedLoaderVersionRange.");

            var catalogue = UiThemeCatalogueParser.Parse(
                Encoding.UTF8.GetBytes(JsonUtil.Serialize(manifest.Ui!)), packaged: true);
            if (!catalogue.TryGetValue(out var declaration))
                return OperationResult<AdmittedUiCatalogue>.Failure(catalogue.ErrorCode, catalogue.ErrorMessage);

            var definitions = new Dictionary<string, UiThemeDescriptor>(StringComparer.Ordinal);
            var resourceHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long resourceBytes = 0;
            byte[]? HashResource(string path)
            {
                if (resourceHashes.TryGetValue(path, out var cached)) return cached;
                if (!TryResolveOrdinaryFile(packageRoot, path, out var file, out _)) return null;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = stream.Length;
                if (length > 32L * 1024 * 1024 || length > 64L * 1024 * 1024 - resourceBytes)
                    return null;
                using var sha = SHA256.Create();
                var digest = sha.ComputeHash(stream);
                resourceBytes += length;
                resourceHashes.Add(path, digest);
                return digest;
            }

            for (var index = 0; index < declaration.Themes.Count; index++)
            {
                var entry = declaration.Themes[index];
                try
                {
                    if (!TryResolveOrdinaryFile(packageRoot, entry.Definition, out var path, out _))
                        return UiDefinitionFailure(ModErrorCode.Io, index, "cannot read the declared package file.");

                    var bytes = ReadThemeDefinition(path);
                    if (bytes == null) return UiDefinitionFailure(ModErrorCode.RateLimited, index, "exceeds 64 KiB.");
                    if (!entry.Matches(bytes))
                        return UiDefinitionFailure(ModErrorCode.InvalidArgument, index, "does not match its packaged SHA-256.");

                    var parsed = UiThemePackParser.Parse(bytes, packaged: true, expectedId: entry.Id, fileHash: HashResource);
                    if (!parsed.TryGetValue(out var descriptor))
                        return UiDefinitionFailure(parsed.ErrorCode, index, parsed.ErrorMessage);
                    definitions.Add(entry.Id, descriptor);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    return UiDefinitionFailure(ModErrorCode.Io, index, "cannot read the declared package file.");
                }
            }

            return OperationResult<AdmittedUiCatalogue>.Success(new AdmittedUiCatalogue(declaration.Default, definitions));
        }

        private static byte[]? ReadThemeDefinition(string path)
        {
            const int Limit = 64 * 1024;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > Limit) return null;

            var bytes = new byte[checked((int)stream.Length)];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) throw new EndOfStreamException();
                length += read;
            }

            if (stream.ReadByte() != -1) throw new InvalidDataException("A UI theme definition changed while being read.");
            return bytes;
        }

        private static OperationResult<AdmittedUiCatalogue> UiDefinitionFailure(ModErrorCode code, int index, string detail) =>
            OperationResult<AdmittedUiCatalogue>.Failure(code, "ui.themes[" + index + "].definition " + detail);

        private static bool TryResolveOrdinaryFile(
            string packageRoot,
            string portablePath,
            out string fullPath,
            out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            try
            {
                fullPath = PathSafety.RequireOrdinaryPackageFile(packageRoot, portablePath);
                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is InvalidDataException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentException ||
                exception is InvalidOperationException ||
                exception is NotSupportedException)
            {
                error = exception.Message;
                return false;
            }
        }

        private static string ComputeSha256(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
