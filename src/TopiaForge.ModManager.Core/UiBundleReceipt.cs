using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Build observations tied to the SHA-256 of the exact authored asset bundle.</summary>
    internal sealed partial class UiBundleReceipt
    {
        internal const int MaxBytes = 512 * 1024;
        internal const int MaxTextures = 4;
        internal const long MaxDecodedBytes = 64L * 1024 * 1024;
        private readonly Dictionary<string, UiBundleSprite> sprites;
        private readonly Dictionary<string, UiBundleFont> fonts;

        private UiBundleReceipt(IReadOnlyList<UiBundleTexture> textures,
            Dictionary<string, UiBundleSprite> sprites, Dictionary<string, UiBundleFont> fonts)
        {
            Textures = textures;
            this.sprites = sprites;
            this.fonts = fonts;
        }

        internal IReadOnlyList<UiBundleTexture> Textures { get; }
        internal bool TryGetSprite(string assetName, out UiBundleSprite sprite) => sprites.TryGetValue(assetName, out sprite);
        internal bool TryGetFont(string assetName, out UiBundleFont font) => fonts.TryGetValue(assetName, out font);

        internal static string PathFor(string bundlePath)
        {
            using var sha = SHA256.Create();
            return "ui/receipts/" + UiThemeData.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(bundlePath))).ToLowerInvariant() + ".json";
        }
    }

    internal readonly struct UiBundleTexture
    {
        internal UiBundleTexture(int width, int height, int mipCount, string format)
        {
            Width = width;
            Height = height;
            MipCount = mipCount;
            Format = format;
            DecodedBytes = MeasureBytes(width, height, mipCount, format);
        }

        internal int Width { get; }
        internal int Height { get; }
        internal int MipCount { get; }
        internal string Format { get; }
        internal bool IsMask => string.Equals(Format, "Alpha8", StringComparison.Ordinal);
        internal long DecodedBytes { get; }

        private static long MeasureBytes(int width, int height, int mipCount, string format)
        {
            // Compressed formats count as decoded RGBA. Single-channel masks need one byte per pixel.
            var bytesPerPixel = format switch
            {
                "Alpha8" or "R8" => 1,
                "RGB24" => 3,
                "RGFloat" or "RGBAHalf" or "RGBA64" => 8,
                "RGBAFloat" => 16,
                _ => 4
            };
            long bytes = 0;
            for (var i = 0; i < mipCount; i++)
            {
                bytes += (long)width * height * bytesPerPixel;
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }

            return bytes;
        }
    }

    internal readonly struct UiBundleSprite
    {
        internal UiBundleSprite(int width, int height, int texture, string licenseSha256)
        {
            Width = width;
            Height = height;
            Texture = texture;
            LicenseSha256 = licenseSha256;
        }

        internal int Width { get; }
        internal int Height { get; }
        internal int Texture { get; }
        internal string LicenseSha256 { get; }
    }

    internal readonly struct UiBundleFont
    {
        internal UiBundleFont(string family, string style, float ascent, float descent,
            IReadOnlyList<int> textures, string licenseSha256)
        {
            Family = family;
            Style = style;
            Ascent = ascent;
            Descent = descent;
            Textures = textures;
            LicenseSha256 = licenseSha256;
        }

        internal string Family { get; }
        internal string Style { get; }
        internal float Ascent { get; }
        internal float Descent { get; }
        internal IReadOnlyList<int> Textures { get; }
        internal string LicenseSha256 { get; }
    }

    internal sealed class UiAdmittedBundle
    {
        private readonly string packageRoot;

        internal UiAdmittedBundle(string packageRoot, string bundlePath, string fullPath, string sha256, UiBundleReceipt receipt)
        {
            this.packageRoot = packageRoot;
            BundlePath = bundlePath;
            FullPath = fullPath;
            Sha256 = sha256;
            Receipt = receipt;
        }

        internal string FullPath { get; }
        internal string BundlePath { get; }
        internal string Sha256 { get; }
        internal UiBundleReceipt Receipt { get; }

        internal OperationResult<byte[]> ReadVerifiedBytes()
        {
            const string logical = "$.ui.bundlePath";
            try
            {
                if (!ManifestContentValidator.TryResolveOrdinaryFile(packageRoot, BundlePath, out var path, out _) ||
                    !string.Equals(path, FullPath, StringComparison.Ordinal))
                    return OperationResult<byte[]>.Failure(ModErrorCode.Io, logical + ": Could not read the admitted bundle.");
                var bytes = UiThemePackageFiles.ReadBounded(path, (int)UiThemePackageFiles.MaxBundleBytes, logical);
                using var sha = SHA256.Create();
                return string.Equals(UiThemeData.Hex(sha.ComputeHash(bytes)), Sha256, StringComparison.Ordinal)
                    ? OperationResult<byte[]>.Success(bytes)
                    : OperationResult<byte[]>.Failure(ModErrorCode.InvalidArgument, logical + ": The admitted bundle bytes changed.");
            }
            catch (UiThemeDataException error)
            {
                return OperationResult<byte[]>.Failure(error.Code, error.Message);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return OperationResult<byte[]>.Failure(ModErrorCode.Io, logical + ": Could not read the admitted bundle.");
            }
        }

        internal bool VerifyFile()
        {
            try
            {
                return ManifestContentValidator.TryResolveOrdinaryFile(packageRoot, BundlePath, out var path, out _) &&
                    string.Equals(path, FullPath, StringComparison.Ordinal) &&
                    string.Equals(UiThemeData.Hex(UiThemePackageFiles.HashFile(path, UiThemePackageFiles.MaxBundleBytes,
                        "$.ui.bundlePath", out _)), Sha256, StringComparison.Ordinal);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or UiThemeDataException)
            {
                return false;
            }
        }
    }
}
