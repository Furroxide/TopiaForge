using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A chip's label reads on the chip in every tone of both schemes, with any accent a mod picks, translucent ones
    /// included, whatever is behind the chip and whichever way Unity blends: WCAG AA, or AAA in high contrast, checked on
    /// the colors as uGUI draws them, in 256 steps a channel. Before, a chip's label was its tone drawn on a wash of that
    /// tone: 1.8 to 3.2:1 in the status and brand tones on Paper, and 2.0 to 3.3:1 on the HUD's tint, and high contrast left
    /// Paper's as they were.
    /// </summary>
    internal static class UiKitChipContrastTests
    {
        public static void Run()
        {
            LabelsReadOnEveryChip();
            PaperChipsKeepTheBrandsWashAndRing();
            HudChipsAreSolidInTheirTone();
            Console.WriteLine("UiKitChipContrastTests passed.");
        }

        private static void LabelsReadOnEveryChip()
        {
            foreach (var scheme in new[] { TopiaForgeScheme.Paper, TopiaForgeScheme.Hud })
            {
                foreach (var highContrast in new[] { false, true })
                {
                    foreach (var accent in Accents())
                    {
                        var colors = TopiaForgeSchemes.Resolve(scheme, accent, highContrast);
                        var context = scheme + (highContrast ? " high contrast" : string.Empty) + (accent.HasValue ? " with accent " + accent.Value : string.Empty);
                        foreach (var (toneName, chip) in Chips(scheme, colors, highContrast))
                        {
                            var label = Drawn(chip.Label);
                            Assert(chip.Label.A == 1f, context + ": the " + toneName + " chip's label is opaque, so it reads as checked");
                            if (scheme == TopiaForgeScheme.Hud)
                            {
                                var ratio = TopiaForgeContrast.Ratio(label, Drawn(chip.Fill));
                                Assert(chip.Fill.A == 1f && chip.Ring == chip.Fill, context + ": the " + toneName + " chip is solid, so the world behind it cannot change it");
                                Assert(ratio >= Minimum(highContrast), context + ": the " + toneName + " chip's label reads on it (" + Format(ratio) + ")");
                                continue;
                            }

                            var fill = Drawn(chip.Fill);
                            foreach (var (surfaceName, surface) in PaperSurfaces(colors))
                            {
                                foreach (var linear in new[] { false, true })
                                {
                                    var ratio = TopiaForgeContrast.Ratio(label, Drawn(TopiaForgeContrast.Over(fill, fill.A, Drawn(surface), linear)));
                                    Assert(ratio >= Minimum(highContrast), context + ": the " + toneName + " chip's label reads on its wash over " + surfaceName +
                                        (linear ? " blended in linear light" : string.Empty) + " (" + Format(ratio) + ")");
                                }

                                var flat = chip.Flattened(Drawn(surface));
                                var flatRatio = TopiaForgeContrast.Ratio(label, Drawn(flat.Fill));
                                Assert(flat.Fill.A == 1f && flatRatio >= Minimum(highContrast),
                                    context + ": the " + toneName + " chip flattened onto " + surfaceName + ", as over a picture, still reads (" + Format(flatRatio) + ")");
                            }
                        }
                    }
                }
            }
        }

        private static void PaperChipsKeepTheBrandsWashAndRing()
        {
            var paper = TopiaForgeSchemes.ResolvePaper(null, false);
            var warning = TopiaForgeSchemes.Chip(TopiaForgeScheme.Paper, paper, paper.Warning, false);
            Assert(warning.Fill == Drawn(paper.Warning.WithAlpha(TopiaForgeSchemes.PaperChipTint)) && warning.Ring == paper.Warning && warning.Label != Drawn(paper.Warning),
                "a Paper chip keeps the brand's wash and ring, and only its label is darkened");
            Assert(TopiaForgeSchemes.NeutralChip(TopiaForgeScheme.Paper, paper, false).Label == Drawn(paper.TextMuted),
                "a neutral Paper chip keeps its muted label, which already reads");

            var translucent = TopiaForgeSchemes.ResolvePaper(TopiaForgeRgba.Hex(0x168E96, 0.5f), false);
            var translucentChip = TopiaForgeSchemes.Chip(TopiaForgeScheme.Paper, translucent, translucent.Accent, false);
            Assert(translucent.Accent.A == 0.5f && translucentChip.Label.A == 1f,
                "a translucent accent still gets an opaque label; at half opacity its label read at 2.2:1 once drawn");
            Assert(TopiaForgeSchemes.NeutralChip(TopiaForgeScheme.Paper, TopiaForgeSchemes.ResolvePaper(null, true), true).Label !=
                   TopiaForgeSchemes.ResolvePaper(null, true).TextMuted,
                "in high contrast the neutral chip's muted label, 5.8:1 on its wash, is darkened to 7:1");
        }

        private static void HudChipsAreSolidInTheirTone()
        {
            var hud = TopiaForgeSchemes.ResolveHud(null, false);
            var success = TopiaForgeSchemes.Chip(TopiaForgeScheme.Hud, hud, hud.Success, false);
            Assert(success.Fill == hud.Success && success.Label == TopiaForgeSchemes.HudChipDarkText, "a HUD chip is solid in its tone with a dark label");
            var neutral = TopiaForgeSchemes.NeutralChip(TopiaForgeScheme.Hud, hud, false);
            Assert(neutral.Fill == hud.Tint && neutral.Label == hud.Text, "a neutral HUD chip is the tint with the HUD's text on it");

            var grey = TopiaForgeSchemes.ResolveHud(TopiaForgeRgba.Hex(0x767676), false);
            var greyChip = TopiaForgeSchemes.Chip(TopiaForgeScheme.Hud, grey, grey.Accent, false);
            Assert(TopiaForgeContrast.Ratio(TopiaForgeSchemes.HudChipDarkText, grey.Accent) < 4.5f && TopiaForgeContrast.Ratio(grey.Text, grey.Accent) < 4.5f &&
                   greyChip.Label == TopiaForgeSchemes.HudChipDarkText && TopiaForgeContrast.Luminance(greyChip.Fill) > TopiaForgeContrast.Luminance(grey.Accent),
                "an accent neither label reads on is lightened until the dark label does");

            var blue = TopiaForgeSchemes.ResolveHud(TopiaForgeRgba.Hex(0x0000FF), true);
            var blueChip = TopiaForgeSchemes.Chip(TopiaForgeScheme.Hud, blue, blue.Accent, true);
            var blueRatio = TopiaForgeContrast.Ratio(Drawn(blueChip.Label), Drawn(blueChip.Fill));
            Assert(blueChip.Fill == Drawn(blueChip.Fill) && blueRatio >= 7f,
                "a fill is moved in drawn steps, so the high-contrast blue reads at 7:1 on screen, not just before rounding (" + Format(blueRatio) + ")");
        }

        private static IEnumerable<TopiaForgeRgba?> Accents()
        {
            yield return null;
            for (uint level = 0; level <= 0xFF; level += 0x11)
            {
                yield return TopiaForgeRgba.Hex(level * 0x010101);
            }

            foreach (var hex in new uint[] { 0x1E3A8A, 0xFFE14D, 0x767676, 0xFF00FF, 0x00FF00, 0x0000FF, 0xFF0000, 0x8A2BE2, 0x008080 })
            {
                yield return TopiaForgeRgba.Hex(hex);
            }

            // Accents whose Paper badge passes before rounding and reads below 4.5:1 once drawn, unless the wash and the label
            // are resolved as drawn.
            foreach (var hex in new uint[] { 0xD23C4B, 0x2D78FF, 0x007869, 0x697878, 0xD23C3C, 0x3CFFE1 })
            {
                yield return TopiaForgeRgba.Hex(hex);
            }

            // A mod's accent may carry its own opacity.
            yield return TopiaForgeRgba.Hex(0x168E96, 0.5f);
            yield return TopiaForgeRgba.Hex(0xFF00FF, 0.3f);
            yield return TopiaForgeRgba.Hex(0x20F6FE, 0.6f);
        }

        /// <summary>Gets a color as uGUI draws it, in 256 steps a channel.</summary>
        private static TopiaForgeRgba Drawn(TopiaForgeRgba color) => TopiaForgeContrast.Quantize(color);

        private static (string Name, TopiaForgeChipColors Chip)[] Chips(TopiaForgeScheme scheme, TopiaForgeSchemeColors colors, bool highContrast)
        {
            return new[]
            {
                ("neutral", TopiaForgeSchemes.NeutralChip(scheme, colors, highContrast)),
                ("muted", TopiaForgeSchemes.Chip(scheme, colors, colors.TextMuted, highContrast)),
                ("faint", TopiaForgeSchemes.Chip(scheme, colors, colors.TextFaint, highContrast)),
                ("primary", TopiaForgeSchemes.Chip(scheme, colors, colors.Primary, highContrast)),
                ("accent", TopiaForgeSchemes.Chip(scheme, colors, colors.Accent, highContrast)),
                ("success", TopiaForgeSchemes.Chip(scheme, colors, colors.Success, highContrast)),
                ("warning", TopiaForgeSchemes.Chip(scheme, colors, colors.Warning, highContrast)),
                ("danger", TopiaForgeSchemes.Chip(scheme, colors, colors.Danger, highContrast)),
            };
        }

        private static (string Name, TopiaForgeRgba Color)[] PaperSurfaces(TopiaForgeSchemeColors paper)
        {
            return new[]
            {
                ("surface", paper.Surface), ("raised surface", paper.SurfaceAlt), ("sunken paper", paper.SurfaceSunken), ("tint", paper.Tint),
                ("selection", paper.SelectedTint), ("a pressed row", paper.SurfaceSunken.Scale(TopiaForgeSchemes.PressedTint)),
            };
        }

        private static float Minimum(bool highContrast) => highContrast ? 7f : 4.5f;

        private static string Format(float ratio) => ratio.ToString("0.00", CultureInfo.InvariantCulture) + ":1";

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }
    }
}
