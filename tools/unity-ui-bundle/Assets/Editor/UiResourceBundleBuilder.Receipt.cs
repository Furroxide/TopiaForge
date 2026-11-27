using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;

namespace TopiaForge
{
    public static partial class UiResourceBundleBuilder
    {
        private static Receipt InspectBundle(string bundleFile, string sha256, BoundSource[] sources)
        {
            var bundle = AssetBundle.LoadFromFile(bundleFile);
            if (bundle == null) throw new InvalidDataException("The built UI bundle could not be opened.");
            try
            {
                var addresses = new HashSet<string>(bundle.GetAllAssetNames(), StringComparer.Ordinal);
                var textureIds = new Dictionary<int, int>();
                var textures = new List<TextureObservation>();
                var sprites = new List<SpriteObservation>();
                var fonts = new List<FontObservation>();
                foreach (var source in sources)
                {
                    if (!addresses.Contains(source.Name))
                        throw new InvalidDataException("A recipe address differs from the exact built AssetBundle address.");
                    if (source.Font)
                        fonts.Add(ReadFont(bundle.LoadAsset<TMP_FontAsset>(source.Name)));
                    else
                    {
                        var matches = bundle.LoadAssetWithSubAssets<Sprite>(source.Name);
                        if (matches.Length != 1)
                            throw new InvalidDataException("An image address must resolve to exactly one Sprite.");
                        sprites.Add(ReadSprite(matches[0]));
                    }

                    SpriteObservation ReadSprite(Sprite sprite)
                    {
                        if (sprite == null || sprite.texture == null)
                            throw new InvalidDataException("An image must resolve as a Sprite with a texture.");
                        if (source.Mask && sprite.texture.format != TextureFormat.Alpha8)
                            throw new InvalidDataException("A declared mask must load with an Alpha8 backing texture.");
                        return new SpriteObservation
                        {
                            assetName = source.Name,
                            width = SpriteAxis(sprite.rect.width),
                            height = SpriteAxis(sprite.rect.height),
                            texture = ObserveTexture(sprite.texture),
                            sourceSha256 = source.SourceSha256,
                            license = source.License,
                            licenseSha256 = source.LicenseSha256,
                        };
                    }

                    FontObservation ReadFont(TMP_FontAsset font)
                    {
                        if (font == null || font.atlasPopulationMode != AtlasPopulationMode.Static ||
                            font.atlasTextures == null || font.atlasTextures.Length is < 1 or > 4)
                            throw new InvalidDataException("A font must resolve as a TMP asset with up to four static atlas textures.");
                        var face = font.faceInfo;
                        if (!(face.pointSize > 0 && face.lineHeight > 0f && face.lineHeight <= float.MaxValue &&
                            face.ascentLine <= float.MaxValue && face.descentLine >= -float.MaxValue &&
                            face.ascentLine > face.descentLine))
                            throw new InvalidDataException("A UI font has unusable metrics.");
                        return new FontObservation
                        {
                            assetName = source.Name,
                            family = face.familyName,
                            style = face.styleName,
                            ascent = face.ascentLine,
                            descent = face.descentLine,
                            textures = Array.ConvertAll(font.atlasTextures, ObserveTexture),
                            sourceSha256 = source.SourceSha256,
                            license = source.License,
                            licenseSha256 = source.LicenseSha256,
                        };
                    }
                }

                return new Receipt
                {
                    schema = 1,
                    bundleSha256 = sha256,
                    editorVersion = Application.unityVersion,
                    textures = textures.ToArray(),
                    sprites = sprites.ToArray(),
                    fonts = fonts.ToArray(),
                };

                int ObserveTexture(Texture2D texture)
                {
                    if (texture == null || texture.width is < 1 or > 4096 || texture.height is < 1 or > 4096)
                        throw new InvalidDataException("A backing texture must be between 1 and 4,096 pixels on both axes.");
                    var nativeId = texture.GetInstanceID();
                    if (textureIds.TryGetValue(nativeId, out var index)) return index;
                    if (textures.Count == 4) throw new InvalidDataException("A UI bundle may contain at most four backing textures.");
                    index = textures.Count;
                    textures.Add(new TextureObservation
                    {
                        width = texture.width,
                        height = texture.height,
                        mipCount = texture.mipmapCount,
                        format = texture.format.ToString()
                    });
                    textureIds.Add(nativeId, index);
                    return index;
                }
            }
            finally
            {
                bundle.Unload(true);
            }
        }

        private static int SpriteAxis(float value)
        {
            if (!(value >= 1f && value <= 2048f) || (int)value != value)
                throw new InvalidDataException("The native Sprite rectangle must have integer dimensions from 1 to 2,048.");
            return (int)value;
        }

        [Serializable] private sealed class Receipt
        {
            public int schema;
            public string bundleSha256;
            public string editorVersion;
            public TextureObservation[] textures;
            public SpriteObservation[] sprites;
            public FontObservation[] fonts;
        }

        [Serializable] private sealed class TextureObservation
        {
            public int width;
            public int height;
            public int mipCount;
            public string format;
        }

        [Serializable] private sealed class SpriteObservation
        {
            public string assetName;
            public int width;
            public int height;
            public int texture;
            public string sourceSha256;
            public string license;
            public string licenseSha256;
        }

        [Serializable] private sealed class FontObservation
        {
            public string assetName;
            public string family;
            public string style;
            public float ascent;
            public float descent;
            public int[] textures;
            public string sourceSha256;
            public string license;
            public string licenseSha256;
        }
    }
}
