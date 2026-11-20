using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Resolves and bounds package data before any UI asset can reach a native decoder.</summary>
    internal sealed class UiThemePackageFiles
    {
        internal const long MaxBundleBytes = 32L * 1024 * 1024;
        private const long MaxPackageBundleBytes = 64L * 1024 * 1024;

        private readonly string root;
        private readonly Dictionary<string, (string Path, FileKind Kind)> paths =
            new Dictionary<string, (string, FileKind)>(StringComparer.Ordinal);
        private readonly Dictionary<string, UiAdmittedBundle> bundles = new Dictionary<string, UiAdmittedBundle>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> bundleHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> licenseHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        private long encodedBytes;
        private long decodedBytes;
        private int textures;

        internal UiThemePackageFiles(string root) => this.root = root;

        internal IReadOnlyDictionary<string, UiAdmittedBundle> Bundles => bundles;

        internal string ResolveDefinition(string path) => Resolve(path, FileKind.Definition, "$.ui.themes.definition");

        internal byte[] HashBundle(string path)
        {
            if (bundleHashes.TryGetValue(path, out var cached)) return cached;
            const string bundlePath = "$.ui.bundlePath";
            var fullPath = Resolve(path, FileKind.Bundle, bundlePath);
            var limit = Math.Min(MaxBundleBytes, MaxPackageBundleBytes - encodedBytes);
            var digest = HashFile(fullPath, limit, bundlePath, out var length);
            var sha256 = UiThemeData.Hex(digest);

            var receiptPath = Resolve(UiBundleReceipt.PathFor(path), FileKind.Receipt, "$.uiReceipt");
            var receipt = UiBundleReceipt.Parse(ReadBounded(receiptPath, UiBundleReceipt.MaxBytes, "$.uiReceipt"), sha256);
            textures += receipt.Textures.Count;
            if (textures > UiBundleReceipt.MaxTextures)
                throw UiThemeDataException.Limit("$.uiReceipt.textures", "A package may use at most four backing textures.");
            foreach (var texture in receipt.Textures) decodedBytes += texture.DecodedBytes;
            if (decodedBytes > UiBundleReceipt.MaxDecodedBytes)
                throw UiThemeDataException.Limit("$.uiReceipt.textures", "The decoded UI texture budget exceeds 64 MiB.");

            encodedBytes += length;
            bundleHashes.Add(path, digest);
            bundles.Add(path, new UiAdmittedBundle(root, path, fullPath, sha256, receipt));
            return digest;
        }

        internal UiThemeImageMetadata? ImageMetadata(UiThemeImage asset)
        {
            if (!bundles[asset.BundlePath].Receipt.TryGetSprite(asset.AssetName, out var sprite))
                return null;
            CheckLicense(asset.LicensePath, sprite.LicenseSha256);
            var texture = bundles[asset.BundlePath].Receipt.Textures[sprite.Texture];
            return new UiThemeImageMetadata(sprite.Width, sprite.Height, texture.IsMask);
        }

        internal void CheckFonts(UiThemeDescriptor descriptor)
        {
            foreach (var font in descriptor.Fonts)
            {
                if (!bundles[font.BundlePath].Receipt.TryGetFont(font.AssetName, out var native))
                    throw UiThemeDataException.Invalid("$.fonts", "Expected a static font asset in the referenced bundle receipt.");
                CheckLicense(font.LicensePath, native.LicenseSha256);
            }
        }

        private void CheckLicense(string path, string expected)
        {
            const string logical = "$.ui.licensePath";
            if (!licenseHashes.TryGetValue(path, out var digest))
            {
                var fullPath = Resolve(path, FileKind.License, logical);
                digest = UiThemeData.Hex(HashFile(fullPath, 1024L * 1024 * 1024, logical, out _));
                licenseHashes.Add(path, digest);
            }
            if (!string.Equals(digest, expected, StringComparison.Ordinal))
                throw UiThemeDataException.Invalid(logical, "The notice differs from the bundle receipt.");
        }

        private string Resolve(string path, FileKind kind, string logical)
        {
            if (!PortablePackagePath.TryValidate(path, out var portable, out var key, out _) ||
                key == "TOPIAFORGE.MOD.JSON" || key == "TOPIAFORGE.INSTALL.JSON" ||
                key == ".GITKEEP" || key.EndsWith("/.GITKEEP", StringComparison.Ordinal))
                throw UiThemeDataException.Invalid(logical, "Expected a distinct portable package file.");

            if (paths.TryGetValue(key, out var existing))
            {
                if (existing.Path != portable || existing.Kind != kind || kind == FileKind.Definition)
                    throw UiThemeDataException.Invalid(logical, "References collide at a package file.");
            }
            else paths.Add(key, (portable, kind));

            if (!ManifestContentValidator.TryResolveOrdinaryFile(root, portable, out var fullPath, out _))
                throw UiThemeDataException.Io(logical, "Cannot read the declared ordinary package file.");
            return fullPath;
        }

        internal static byte[] ReadBounded(string path, int maximumBytes, string logical)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes)
                throw UiThemeDataException.Limit(logical, "The file exceeds its size limit.");
            var bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            if (stream.ReadByte() != -1) throw new IOException("The file changed while it was read.");
            return bytes;
        }

        internal static byte[] HashFile(string path, long maximumBytes, string logical, out long length)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            length = stream.Length;
            if (length == 0) throw UiThemeDataException.Invalid(logical, "The file is empty.");
            if (length > maximumBytes) throw UiThemeDataException.Limit(logical, "The file exceeds its size limit.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            var remaining = length;
            while (remaining != 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            if (stream.ReadByte() != -1) throw new IOException("The file changed while it was read.");
            return hash.GetHashAndReset();
        }

        private enum FileKind { Definition, Bundle, Receipt, License }
    }
}
