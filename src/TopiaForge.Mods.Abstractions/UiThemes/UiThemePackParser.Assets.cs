using System;
using System.Collections.Generic;
using System.IO;

namespace TopiaForge.Mods
{
    internal static partial class UiThemePackParser
    {
        private static UiThemeFont[] ParseFonts(UiThemeJsonObject pack, AssetInputs assets)
        {
            var values = pack.Array("fonts", 9);
            var fonts = new UiThemeFont[values.Count];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Count; index++)
            {
                var item = UiThemeJsonObject.From(values[index], UiThemeData.Entry(pack.At("fonts"), index));
                item.Allow("id", "bundlePath", "assetName", "licensePath");
                var id = ParseResourceId(item, ids);
                if (Array.IndexOf(KitFonts, id) >= 0)
                {
                    throw UiThemeDataException.Invalid(item.At("id"), "Font identifier is reserved for a kit face.");
                }

                var (bundle, asset, licence) = ParseResourceNames(item);
                fonts[index] = new UiThemeFont(id, bundle, asset, licence, assets.BundleDigest(bundle, item.At("bundlePath")));
            }

            return fonts;
        }

        private static UiThemeImage[] ParseImages(UiThemeJsonObject pack, AssetInputs assets)
        {
            var values = pack.Array("images", 64);
            var images = new UiThemeImage[values.Count];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < values.Count; index++)
            {
                var item = UiThemeJsonObject.From(values[index], UiThemeData.Entry(pack.At("images"), index));
                item.Allow("id", "bundlePath", "assetName", "licensePath", "pixelWidth", "pixelHeight", "isMask");
                var id = ParseResourceId(item, ids);
                var (bundle, asset, licence) = ParseResourceNames(item);
                var width = item.Integer("pixelWidth", 1, 2048);
                var height = item.Integer("pixelHeight", 1, 2048);
                var mask = item.Boolean("isMask");
                if (assets.Packaged && (width == null || height == null || mask == null))
                {
                    throw UiThemeDataException.Invalid(item.Path, "Packaged images require dimensions and a mask flag.");
                }

                var image = new UiThemeImage(id, bundle, asset, licence, assets.BundleDigest(bundle, item.At("bundlePath")), width, height, mask);
                images[index] = assets.Enrich(image, item.Path);
            }

            return images;
        }

        private static string ParseResourceId(UiThemeJsonObject item, HashSet<string> ids)
        {
            var id = UiThemeData.Id(item.RequiredString("id"), item.At("id"));
            return ids.Add(id) ? id : throw UiThemeDataException.Invalid(item.At("id"), "Resource identifier is already declared.");
        }

        private static (string Bundle, string Asset, string Licence) ParseResourceNames(UiThemeJsonObject item)
        {
            var bundle = UiThemeData.Path(item.RequiredString("bundlePath"), item.At("bundlePath"));
            var asset = item.RequiredString("assetName");
            if (asset.Length < 1 || asset.Length > 256)
            {
                throw UiThemeDataException.Invalid(item.At("assetName"), "Bundle entry names require 1–256 characters.");
            }

            return (bundle, asset, UiThemeData.Path(item.RequiredString("licensePath"), item.At("licensePath")));
        }

        private sealed class AssetInputs
        {
            private readonly Func<string, byte[]?>? fileHash;
            private readonly Func<UiThemeImage, UiThemeImageMetadata?>? imageMetadata;
            private readonly Dictionary<string, string> bundleDigests = new Dictionary<string, string>(StringComparer.Ordinal);

            internal AssetInputs(bool packaged, Func<string, byte[]?>? fileHash, Func<UiThemeImage, UiThemeImageMetadata?>? imageMetadata)
            {
                Packaged = packaged;
                this.fileHash = fileHash;
                this.imageMetadata = imageMetadata;
            }

            internal bool Packaged { get; }

            internal string? BundleDigest(string bundle, string path)
            {
                if (bundleDigests.TryGetValue(bundle, out var digest))
                {
                    return digest;
                }

                if (fileHash == null && !Packaged)
                {
                    return null;
                }

                byte[]? bytes;
                try
                {
                    bytes = fileHash?.Invoke(bundle);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw UiThemeDataException.Io(path, "Could not read the bundle digest.");
                }

                if (bytes is not { Length: 32 })
                {
                    throw UiThemeDataException.Invalid(path, "Expected the SHA-256 digest of the referenced bundle.");
                }

                digest = UiThemeData.Hex(bytes);
                bundleDigests.Add(bundle, digest);
                return digest;
            }

            internal UiThemeImage Enrich(UiThemeImage image, string path)
            {
                if (imageMetadata == null)
                {
                    return image;
                }

                UiThemeImageMetadata? receipt;
                try
                {
                    receipt = imageMetadata(image);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw UiThemeDataException.Io(path, "Could not read the image metadata.");
                }

                if (receipt is not UiThemeImageMetadata metadata || metadata.PixelWidth < 1 || metadata.PixelWidth > 2048
                    || metadata.PixelHeight < 1 || metadata.PixelHeight > 2048)
                {
                    throw UiThemeDataException.Invalid(path, "Expected valid image metadata from the referenced bundle.");
                }

                if ((image.PixelWidth is int width && width != metadata.PixelWidth)
                    || (image.PixelHeight is int height && height != metadata.PixelHeight)
                    || (image.IsMask is bool mask && mask != metadata.IsMask))
                {
                    throw UiThemeDataException.Invalid(path, "Declared image metadata differs from the referenced bundle.");
                }

                return image.WithMetadata(metadata);
            }
        }
    }
}
