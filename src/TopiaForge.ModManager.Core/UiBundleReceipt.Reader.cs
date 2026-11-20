using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Core
{
    internal sealed partial class UiBundleReceipt
    {
        internal static UiBundleReceipt Parse(ReadOnlySpan<byte> bytes, string bundleSha256)
        {
            const string path = "$.uiReceipt";
            var root = UiThemeJson.Read(bytes, path, MaxBytes, maximumItems: 512);
            root.Allow("schema", "bundleSha256", "editorVersion", "textures", "sprites", "fonts");
            root.RequiredInteger("schema", 1, 1);
            var expected = Sha256(root, "bundleSha256");
            if (!string.Equals(expected, bundleSha256, StringComparison.Ordinal))
                throw UiThemeDataException.Invalid(root.At("bundleSha256"), "Receipt does not match the bundle bytes.");
            ReadText(root, "editorVersion", 64);

            var textures = ReadTextures(root);
            var sprites = ReadSprites(root, textures);
            var fonts = ReadFonts(root, textures.Length);
            foreach (var sprite in sprites)
            {
                if (fonts.ContainsKey(sprite.Key))
                    throw UiThemeDataException.Invalid(path, "An asset cannot be both a Sprite and a font.");
            }

            return new UiBundleReceipt(Array.AsReadOnly(textures), sprites, fonts);
        }

        private static UiBundleTexture[] ReadTextures(UiThemeJsonObject root)
        {
            var records = root.Array("textures", MaxTextures, required: true);
            var textures = new UiBundleTexture[records.Count];
            for (var index = 0; index < textures.Length; index++)
            {
                var item = UiThemeJsonObject.From(records[index], UiThemeData.Entry(root.At("textures"), index));
                item.Allow("width", "height", "mipCount", "format");
                var width = item.RequiredInteger("width", 1, 4096);
                var height = item.RequiredInteger("height", 1, 4096);
                var maxMips = 1;
                for (var dimension = Math.Max(width, height); dimension > 1; dimension /= 2) maxMips++;
                var mips = item.RequiredInteger("mipCount", 1, maxMips);
                var format = ReadText(item, "format", 64);
                foreach (var character in format)
                    if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                        throw UiThemeDataException.Invalid(item.At("format"), "Expected a native texture format name.");

                if (!(format[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
                    throw UiThemeDataException.Invalid(item.At("format"), "Expected a native texture format name.");
                textures[index] = new UiBundleTexture(width, height, mips, format);
            }
            return textures;
        }

        private static Dictionary<string, UiBundleSprite> ReadSprites(UiThemeJsonObject root, UiBundleTexture[] textures)
        {
            var records = root.Array("sprites", 512, required: true);
            var sprites = new Dictionary<string, UiBundleSprite>(StringComparer.Ordinal);
            for (var index = 0; index < records.Count; index++)
            {
                var item = UiThemeJsonObject.From(records[index], UiThemeData.Entry(root.At("sprites"), index));
                item.Allow("assetName", "width", "height", "texture", "sourceSha256", "license", "licenseSha256");
                var assetName = ReadText(item, "assetName", 256);
                var texture = item.RequiredInteger("texture", 0, textures.Length - 1);
                var width = item.RequiredInteger("width", 1, Math.Min(2048, textures[texture].Width));
                var height = item.RequiredInteger("height", 1, Math.Min(2048, textures[texture].Height));
                var notice = ReadProvenance(item);
                if (!sprites.TryAdd(assetName, new UiBundleSprite(width, height, texture, notice)))
                    throw UiThemeDataException.Invalid(item.At("assetName"), "Sprite entry is already recorded.");
            }
            return sprites;
        }

        private static Dictionary<string, UiBundleFont> ReadFonts(UiThemeJsonObject root, int textureCount)
        {
            var records = root.Array("fonts", 72, required: true);
            var fonts = new Dictionary<string, UiBundleFont>(StringComparer.Ordinal);
            for (var index = 0; index < records.Count; index++)
            {
                var item = UiThemeJsonObject.From(records[index], UiThemeData.Entry(root.At("fonts"), index));
                item.Allow("assetName", "family", "style", "ascent", "descent", "textures", "sourceSha256", "license", "licenseSha256");
                var name = ReadText(item, "assetName", 256);
                var family = ReadText(item, "family", 256);
                var style = ReadText(item, "style", 256, allowEmpty: true);
                var ascent = item.Number("ascent", -float.MaxValue, float.MaxValue);
                var descent = item.Number("descent", -float.MaxValue, float.MaxValue);
                if (!ascent.HasValue || !descent.HasValue || !(ascent.Value > descent.Value))
                    throw UiThemeDataException.Invalid(item.Path, "Expected usable ascent and descent metrics.");

                var pages = item.Array("textures", MaxTextures, required: true);
                if (pages.Count == 0) throw UiThemeDataException.Invalid(item.At("textures"), "Expected a static font atlas.");
                var indices = new int[pages.Count];
                for (var page = 0; page < pages.Count; page++)
                {
                    if (pages[page] is not UiThemeJsonNumber { IsInteger: true } value ||
                        value.Integer < 0 || value.Integer >= textureCount ||
                        Array.IndexOf(indices, value.Integer, 0, page) >= 0)
                        throw UiThemeDataException.Invalid(item.At("textures"), "Expected distinct valid texture identities.");
                    indices[page] = value.Integer;
                }

                var notice = ReadProvenance(item);
                if (!fonts.TryAdd(name, new UiBundleFont(family, style, ascent.Value, descent.Value, Array.AsReadOnly(indices), notice)))
                    throw UiThemeDataException.Invalid(item.At("assetName"), "Font entry is already recorded.");
            }
            return fonts;
        }

        private static string ReadProvenance(UiThemeJsonObject item)
        {
            Sha256(item, "sourceSha256");
            ReadText(item, "license", 128);
            return Sha256(item, "licenseSha256");
        }

        private static string Sha256(UiThemeJsonObject item, string name) =>
            UiThemeData.Sha256(item.RequiredString(name), item.At(name));

        private static string ReadText(UiThemeJsonObject item, string name, int maxLength, bool allowEmpty = false)
        {
            var text = item.RequiredString(name);
            if ((!allowEmpty && text.Length == 0) || text.Length > maxLength)
                throw UiThemeDataException.Invalid(item.At(name), "Text is outside the permitted length.");
            foreach (var character in text)
                if (char.IsControl(character))
                    throw UiThemeDataException.Invalid(item.At(name), "Control characters are not permitted.");
            return text;
        }
    }
}
