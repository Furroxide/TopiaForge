using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

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
            var synchronizedFiles = manifest.Multiplayer?.SynchronizedFiles ?? new List<string>();
            if (!ModManifest.IsSupportedSchemaVersion(manifest.SchemaVersion) ||
                !string.Equals(
                    manifest.Multiplayer?.Mode,
                    ModMultiplayerMetadata.SessionMode,
                    StringComparison.Ordinal))
            {
                return errors;
            }

            if (!synchronizedFiles.Contains(
                    ModMultiplayerMetadata.ContractLockFileName,
                    StringComparer.Ordinal))
            {
                errors.Add(
                    "Session packages must synchronize the canonical generated contract lock '" +
                    ModMultiplayerMetadata.ContractLockFileName + "'. Repack this mod with TopiaForge tooling.");
            }

            var hashes = manifest.Hashes ?? new Dictionary<string, string>();
            foreach (var rawPath in synchronizedFiles.Take(ModMultiplayerMetadata.MaxSynchronizedFiles + 1))
            {
                if (!PortablePackagePath.TryValidate(rawPath, out var portablePath, out _, out _))
                {
                    // ManifestValidator owns the actionable path diagnostic.
                    continue;
                }

                if (!hashes.TryGetValue(portablePath, out var expectedDigest) ||
                    string.IsNullOrWhiteSpace(expectedDigest))
                {
                    errors.Add(
                        "multiplayer.synchronizedFiles entry '" + portablePath +
                        "' is missing its pack-time SHA-256 in hashes.");
                    continue;
                }

                if (!TryResolveOrdinaryFile(packageRoot, portablePath, out var fullPath, out var pathError))
                {
                    errors.Add(
                        "multiplayer.synchronizedFiles entry '" + portablePath +
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
                        "multiplayer.synchronizedFiles entry '" + portablePath +
                        "' could not be hashed (" + exception.Message + ").");
                    continue;
                }

                if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "multiplayer.synchronizedFiles entry '" + portablePath +
                        "' does not match its pack-time SHA-256.");
                }
            }

            return errors;
        }

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
                var root = Path.GetFullPath(packageRoot);
                var rootAttributes = File.GetAttributes(root);
                if ((rootAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) !=
                    FileAttributes.Directory)
                {
                    error = "package root is not an ordinary directory";
                    return false;
                }

                var current = root;
                var segments = portablePath.Split('/');
                for (var index = 0; index < segments.Length; index++)
                {
                    current = PathSafety.CombineRelativeChild(current, segments[index]);
                    var attributes = File.GetAttributes(current);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        error = "path contains a link or special entry";
                        return false;
                    }

                    var isLast = index == segments.Length - 1;
                    if (isLast == ((attributes & FileAttributes.Directory) != 0))
                    {
                        error = isLast ? "path names a directory" : "parent path is not a directory";
                        return false;
                    }
                }

                fullPath = current;
                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
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
