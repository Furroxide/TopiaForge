using System;
using System.Collections.Generic;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// One procedurally generated runtime atlas holding every piece of widget chrome:
    /// 9-sliced rounded fills and border rings per brand radius, a circle, and the icon
    /// set. Everything samples a single texture so panels/buttons/badges batch. Sprites
    /// from the brand AssetBundle override these when present (PR2 wires that).
    /// </summary>
    public static class TopiaForgeSprites
    {
        private const int AtlasWidth = 1024;
        private const int Gutter = 2;
        private const int StraightMargin = 4;
        private const int CircleSize = 24;
        private const int IconSize = 24;

        private static readonly int[] Radii = { 6, 10, 14, 18, 26, 28 };

        private static Texture2D? atlas;
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private static Sprite? white;

        /// <summary>Plain white sprite for flat fills, flashes, vignettes, bars.</summary>
        public static Sprite White
        {
            get
            {
                if (white == null)
                {
                    white = Sprite.Create(
                        Texture2D.whiteTexture,
                        new Rect(0f, 0f, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height),
                        new Vector2(0.5f, 0.5f),
                        100f);
                    white.name = "TopiaForgeWhite";
                }

                return white;
            }
        }

        /// <summary>Creates and adds a fill child element.</summary>
        public static Sprite Fill(TopiaForgeRadius radius)
        {
            EnsureAtlas();
            return Cache["fill-" + (int)radius];
        }

        /// <summary>Creates and adds a ring child element.</summary>
        public static Sprite Ring(TopiaForgeRadius radius, float thickness)
        {
            EnsureAtlas();
            var key = thickness >= TopiaForgeTokens.BorderStrong ? "ring3-" : "ring2-";
            return Cache[key + (int)radius];
        }

        /// <summary>Creates and adds a circle child element.</summary>
        public static Sprite Circle()
        {
            EnsureAtlas();
            return Cache["circle"];
        }

        /// <summary>Creates a sprite for a semantic TopiaForge icon.</summary>
        public static Sprite Icon(TopiaForgeIcon icon)
        {
            EnsureAtlas();
            return Cache["icon-" + icon];
        }

        private static void EnsureAtlas()
        {
            if (atlas != null)
            {
                return;
            }

            var tiles = new List<(string Key, int Size, Vector4 Border, Func<float, float, int, float> Coverage)>();
            foreach (var radius in Radii)
            {
                var r = radius;
                var size = (r * 2) + (StraightMargin * 2);
                var border = r + StraightMargin - 1;
                var borders = new Vector4(border, border, border, border);
                tiles.Add(("fill-" + r, size, borders, (x, y, s) => TopiaForgeRoundedRectMath.FillCoverage(x, y, s, s, r)));
                tiles.Add(("ring2-" + r, size, borders, (x, y, s) => TopiaForgeRoundedRectMath.RingCoverage(x, y, s, s, r, TopiaForgeTokens.BorderStandard)));
                tiles.Add(("ring3-" + r, size, borders, (x, y, s) => TopiaForgeRoundedRectMath.RingCoverage(x, y, s, s, r, TopiaForgeTokens.BorderStrong)));
            }

            tiles.Add(("circle", CircleSize, Vector4.zero, (x, y, s) => TopiaForgeRoundedRectMath.CircleCoverage(x, y, s)));
            foreach (TopiaForgeIcon icon in Enum.GetValues(typeof(TopiaForgeIcon)))
            {
                var captured = icon;
                tiles.Add(("icon-" + captured, IconSize, Vector4.zero, (x, y, s) => TopiaForgeIconRaster.Coverage(captured, x, y, s)));
            }

            // Shelf packing: rows of tiles left-to-right on a fixed-width atlas.
            var positions = new (int X, int Y)[tiles.Count];
            var cursorX = Gutter;
            var cursorY = Gutter;
            var rowHeight = 0;
            for (var index = 0; index < tiles.Count; index++)
            {
                var size = tiles[index].Size;
                if (cursorX + size + Gutter > AtlasWidth)
                {
                    cursorX = Gutter;
                    cursorY += rowHeight + Gutter;
                    rowHeight = 0;
                }

                positions[index] = (cursorX, cursorY);
                cursorX += size + Gutter;
                rowHeight = Math.Max(rowHeight, size);
            }

            var atlasHeight = NextPowerOfTwo(cursorY + rowHeight + Gutter);
            var texture = new Texture2D(AtlasWidth, atlasHeight, TextureFormat.RGBA32, mipChain: false)
            {
                name = "TopiaForgeSpritesAtlas",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color32[AtlasWidth * atlasHeight];
            for (var index = 0; index < tiles.Count; index++)
            {
                var (_, size, _, coverage) = tiles[index];
                var (ox, oy) = positions[index];
                for (var y = 0; y < size; y++)
                {
                    var rowStart = ((oy + y) * AtlasWidth) + ox;
                    for (var x = 0; x < size; x++)
                    {
                        var alpha = coverage(x + 0.5f, y + 0.5f, size);
                        var value = (byte)Math.Round(Clamp01(alpha) * 255f);
                        pixels[rowStart + x] = new Color32(255, 255, 255, value);
                    }
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
            atlas = texture;

            for (var index = 0; index < tiles.Count; index++)
            {
                var (key, size, border, _) = tiles[index];
                var (ox, oy) = positions[index];
                var sprite = Sprite.Create(
                    texture,
                    new Rect(ox, oy, size, size),
                    new Vector2(0.5f, 0.5f),
                    100f,
                    0,
                    SpriteMeshType.FullRect,
                    border);
                sprite.name = "TopiaForge-" + key;
                sprite.hideFlags = HideFlags.HideAndDontSave;
                Cache[key] = sprite;
            }

            TopiaForgeLog.Info("Sprite atlas generated (" + AtlasWidth + "x" + atlasHeight + ", " + tiles.Count + " tiles).");
        }

        private static int NextPowerOfTwo(int value)
        {
            var result = 32;
            while (result < value)
            {
                result *= 2;
            }

            return result;
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }
    }
}
