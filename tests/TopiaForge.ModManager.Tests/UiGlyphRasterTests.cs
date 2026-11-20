using System;
using System.Security.Cryptography;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using TopiaForge.TestSupport;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiGlyphRasterTests
    {
        private const int MinimumSize = 18;
        internal const byte InkThreshold = 153;

        // SHA-256 of the inspected 18px solid-ink masks in UiGlyph order.
        private static readonly string[] MinimumMasks =
        {
            "E9F69595269F272870C7B5DA7D18315088E9408AAF1E05C8DCB0C26C5BDA0721", // Check
            "B3161A694930E1E30F30F68489048B4962C733F623C7EB41CCFB568A2E067CF5", // Cross
            "D69D029AC85D167E4CBF76DD8EBDD2EE641D7E0A80547D89D9CB2B2729197167", // Down
            "6ADF2C59BF3358F83DADE3E1FB198C4A6BBC1F3CB8231142EBBB0D2D5B890B54", // Next
            "645F91354E73D5B325F7820FF4FCD8AFFDA6C35298E7D4B6813E18623E170316", // Search
            "E384D86E7F93CD77F0B018DDF02BC0BF614DB983390361D0B7D0CA825E8FCFC9", // Warning
            "76F4863A7956A6B3B346E0AB62D97D579F053ADB65E60A3D41C5CD50F4D09594", // Info
            "06F9AC1875B83ABDCFDBBA451B735C67B3B2664511FF8C9E940359A7E2DDB718", // Use
            "18BBE787FFE2CDDF2C56411F828494068C302723E0063798DEA11E7F49798F8A", // Microphone
            "96B137C16F228EAA0786AB7E08EB4041513A1B23B07C14FC0DCDBC1FC94C58F5", // MicrophoneOff
            "535AFB82B25E6BDF71A47613DEFA7141D2EAECA2FAF8DF48F3DC091E137C3913", // Talk
            "27F50541D161A571EB35CAA9407BAA079D63113BD0E52B5ABB02B6977CC6AEE8", // Vote
            "BEDDD35D733B91A4FC65BC92E9F8987C2CCB1F261E8EEC4A35012BD535389682", // Timer
            "BBFE595124D3E6FFC5ED437799CFF8D6A916CC368E5D241AEB515E60DE41B44E", // Lock
            "87D0B348E210BD095B2A91AFCA05A8D51E0C6CAB9069DC974801B693918F5BEA", // Map
            "E25EAC4ACD721865BE29D25DC0DE616A85D60B172D9DAB0D6A2911A0B9BFD5E9", // Notes
            "C0DCBF619882549D7E6D98831780AD122020F184844CD0E560DCB7C371BBBC6A", // Eye
            "DC92BA7A11785FB219AD64014D504C3E5F612525CEE89A4F68E4A9EB4620E690", // Bell
            "AFBB6CE96AD6C2AE13D1C3495CA334261F4D808DCC7B4451526202830D956C60", // Flag
            "6CB953FE8E914338AEBE2831DDEF1C15E414835B485BAD5AF8F24F8D930B96C3", // Star
            "24AB8A8AD1CF3DE3B17DC00EB021E40F833A32CD5C523B0EB6B69240248E9977", // Health
            "4432CFE8CA64D89341262C967C6F4D8CCD4E46EA1C0F91A5BD847771A98C3EEC", // Shield
            "DEC9213623ADEBD5928946BD6CA53D3E6DF513AAF66A5DF33FCEFF7C19F31DE9", // Energy
            "FF793768FB4CD213255C11789FA193D658BA766ED14CD35053D44BD832D5B1F7", // Settings
            "93B4EB738CE22453B2D8938F8C46595ECBC961A1358DA8AB50593C9839A63388", // Player
            "0FDD712A6CCD310F63AD0D53D9EE236C57417192B99CF290AC02E285B51A11D0", // Players
            "D519E8B938921E90DC1101DBC136E2ACDE32D1D4F3761D8D74EBC9073170D66D", // Host
            "9371C6DC7A3315C2C5416207B7DDA9C8FB5EBF6865A72A66905C3F2351AD5E07", // Signal
            "EBD09A866E0227DB05C1A9B348D0E5960C9B850E1AE681674B966663D00A66DD", // Volume
            "6DDA360560A1DB0B8868404E00CBB7BE3974DBF690509EF8EA54CE578ECBDAF8", // Leave
            "DCCD8127F814E5AF66EBCC4DE1394C63FF8F1A402425AFD7CDBDDCFB49A14676", // Play
        };

        internal static void Run()
        {
            CheckLegacyTiles();
            CheckMinimumSize();
            CheckWarmAllocation();
            Console.WriteLine("The 31 semantic glyphs and original icon tiles passed the raster checks.");
        }

        private static void CheckLegacyTiles()
        {
            var legacy = new[]
            {
                (UiGlyph.Check, TopiaForgeIcon.Check, 0),
                (UiGlyph.Cross, TopiaForgeIcon.Cross, 1),
                (UiGlyph.Down, TopiaForgeIcon.ChevronDown, 2),
                (UiGlyph.Next, TopiaForgeIcon.ChevronRight, 3),
                (UiGlyph.Search, TopiaForgeIcon.Magnifier, 4),
                (UiGlyph.Warning, TopiaForgeIcon.Warning, 6),
                (UiGlyph.Microphone, TopiaForgeIcon.Microphone, 7),
                (UiGlyph.MicrophoneOff, TopiaForgeIcon.MicrophoneOff, 8),
            };
            Check((int)TopiaForgeIcon.Grip == 5, "The original renderer Grip keeps its value.");
            for (var index = 0; index < legacy.Length; index++)
            {
                var (glyph, icon, value) = legacy[index];
                Check((int)icon == value && TopiaForgeGlyphRaster.SharedIcon(glyph) == icon,
                    "The matching semantic glyph uses its existing renderer tile.");
            }

            var aliases = 0;
            for (var index = 0; index < TopiaForgeGlyphRaster.Count; index++)
                if (TopiaForgeGlyphRaster.SharedIcon((UiGlyph)index).HasValue) aliases++;
            Check(aliases == legacy.Length, "All other semantic glyphs have their own geometry.");
        }

        private static void CheckMinimumSize()
        {
            var glyphs = Enum.GetValues<UiGlyph>();
            Check(glyphs.Length == 31 && glyphs.Length == TopiaForgeGlyphRaster.Count, "The complete approved set has a tile.");
            var masks = new byte[glyphs.Length][];
            for (var index = 0; index < glyphs.Length; index++)
            {
                var glyph = glyphs[index];
                var pixels = SampleAtMinimum(glyph);
                var ink = 0;
                for (var pixel = 0; pixel < pixels.Length; pixel++)
                {
                    pixels[pixel] = pixels[pixel] >= InkThreshold ? (byte)1 : (byte)0;
                    ink += pixels[pixel];
                }
                Check(ink >= 20 && ink <= 176, glyph + " has a bounded set of solid pixels at 18px.");
                Check(Convert.ToHexString(SHA256.HashData(pixels)) == MinimumMasks[index],
                    glyph + " matches its inspected 18px raster fixture.");
                for (var prior = 0; prior < index; prior++)
                {
                    var distance = 0;
                    for (var pixel = 0; pixel < pixels.Length; pixel++)
                        if (pixels[pixel] != masks[prior][pixel]) distance++;
                    Check(distance >= 24, glyph + " visibly differs from " + glyphs[prior] + " at 18px.");
                }
                masks[index] = pixels;
            }
        }

        internal static byte[] SampleAtMinimum(UiGlyph glyph)
        {
            const int tileSize = TopiaForgeGlyphRaster.TileSize;
            Span<byte> tile = stackalloc byte[tileSize * tileSize];
            for (var y = 0; y < tileSize; y++)
            {
                for (var x = 0; x < tileSize; x++)
                {
                    var alpha = TopiaForgeGlyphRaster.Coverage(glyph, x + 0.5f, y + 0.5f, tileSize);
                    Check(alpha >= 0f && alpha <= 1f, "The atlas receives finite alpha coverage.");
                    tile[(y * tileSize) + x] = (byte)Math.Round(alpha * 255f);
                }
            }

            var result = new byte[MinimumSize * MinimumSize];
            var ratio = (float)tileSize / MinimumSize;
            for (var y = 0; y < MinimumSize; y++)
            {
                for (var x = 0; x < MinimumSize; x++)
                {
                    // Downsample the actual atlas texels with the same bilinear footprint as the sprite.
                    var sourceX = ((x + 0.5f) * ratio) - 0.5f;
                    var sourceY = ((y + 0.5f) * ratio) - 0.5f;
                    var left = (int)sourceX;
                    var lower = (int)sourceY;
                    var floor = (lower * tileSize) + left;
                    var horizontal = sourceX - left;
                    var vertical = sourceY - lower;
                    var bottom = Lerp(tile[floor], tile[floor + 1], horizontal);
                    var top = Lerp(tile[floor + tileSize], tile[floor + tileSize + 1], horizontal);
                    result[(y * MinimumSize) + x] = (byte)Math.Round(Lerp(bottom, top, vertical));
                }
            }
            return result;
        }

        private static float Lerp(float low, float high, float fraction) => low + ((high - low) * fraction);

        private static void CheckWarmAllocation()
        {
            var index = 0;
            var coverage = 0f;
            var bytes = AllocationProbe.Measure("semantic glyph coverage", () =>
            {
                var glyph = (UiGlyph)(index % TopiaForgeGlyphRaster.Count);
                coverage += TopiaForgeGlyphRaster.Coverage(glyph, 12.5f, 12.5f, TopiaForgeGlyphRaster.TileSize);
                index++;
            }, frames: 4096, warmups: 512);
            Check(coverage > 0f && bytes == 0, "Warmed glyph coverage allocates zero managed bytes.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
