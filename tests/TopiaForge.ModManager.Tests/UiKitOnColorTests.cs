using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// What the kit draws on a fill reads on that fill in both schemes, in every state uGUI tints the fill to, with any
    /// accent a mod picks: a label at WCAG AA and a mark at 3:1, or 7:1 and 4.5:1 in high contrast. Before, all of it was
    /// white: 2.61:1 on the brand orange under a filled button's label, a switch's thumb and a checkbox's check, 3.00:1 on
    /// the HUD's danger button and 2.34:1 under a HUD stepper's check, lower still in high contrast, which lightens the
    /// HUD's status fills. A slider's white handle read at 1.02:1 on Paper's surface, and an off switch's grey thumb at
    /// 2.78:1 on the HUD's tint.
    /// </summary>
    internal static class UiKitOnColorTests
    {
        // uGUI's default color block, which a switch's track, a checkbox's box and a slider's handle keep.
        private const float DefaultHighlightedTint = 245f / 255f;
        private const float DefaultPressedTint = 200f / 255f;

        private static readonly TopiaForgeRgba Black = new TopiaForgeRgba(0f, 0f, 0f);

        public static void Run()
        {
            ContentReadsOnEveryFillInEveryState();
            WhiteStaysWhereItReadsAndDarkTakesOver();
            BrandFillsKeepTheirColors();
            OffSwitchThumbStaysFaint();
            PublicOnRolesReadOnTheirFills();
            AnyFillResolvesToContentThatReads();
            SliderHandleShowsOnItsTrack();
            WidgetsDrawTheResolvedColors();
            Console.WriteLine("UiKitOnColorTests passed.");
        }

        private static void ContentReadsOnEveryFillInEveryState()
        {
            Assert(TopiaForgeOnColors.ControlPressedTint == DefaultPressedTint, "controls are resolved at uGUI's default pressed tint");
            Assert(Roles().Length == Enum.GetValues(typeof(TopiaForgeFillRole)).Length, "every fill role is tested in every state it is drawn in");
            foreach (var scheme in new[] { TopiaForgeScheme.Paper, TopiaForgeScheme.Hud })
            {
                foreach (var highContrast in new[] { false, true })
                {
                    foreach (var accent in Accents())
                    {
                        var colors = TopiaForgeSchemes.Resolve(scheme, accent, highContrast);
                        var all = TopiaForgeOnColors.ResolveAll(colors, highContrast);
                        var context = scheme + (highContrast ? " high contrast" : string.Empty) + (accent.HasValue ? " with accent " + accent.Value : string.Empty);
                        foreach (var (role, label, tints) in Roles())
                        {
                            var pair = all[(int)role];
                            var single = TopiaForgeOnColors.Resolve(role, colors, highContrast);
                            Assert(pair.Fill == single.Fill && pair.Content == single.Content, context + ": " + role + " resolves the same alone and with the rest");
                            Assert(pair.Fill.A == 1f && pair.Content.A == 1f, context + ": " + role + " draws opaque colors");
                            var minimum = label ? TopiaForgeOnColors.LabelRatio(highContrast) : TopiaForgeOnColors.MarkRatio(highContrast);
                            foreach (var tint in tints)
                            {
                                foreach (var linear in new[] { false, true })
                                {
                                    var ratio = TopiaForgeContrast.Ratio(pair.Content, Tinted(pair.Fill, tint, linear));
                                    Assert(ratio >= minimum, context + ": " + role + " reads on its fill at tint " + tint.ToString("0.###", CultureInfo.InvariantCulture) +
                                        (linear ? " in linear light" : string.Empty) + " (" + Format(ratio) + ")");
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void WhiteStaysWhereItReadsAndDarkTakesOver()
        {
            var white = TopiaForgePalette.White;
            var dark = TopiaForgeOnColors.DarkContent;
            var paper = TopiaForgeSchemes.ResolvePaper(null, false);
            var hud = TopiaForgeSchemes.ResolveHud(null, false);
            Assert(TopiaForgeContrast.Ratio(white, paper.Primary) < 3f && TopiaForgeContrast.Ratio(white, hud.Danger) < 4.5f &&
                   TopiaForgeContrast.Ratio(white, hud.Success) < 3f,
                "white read at 2.61:1 on the orange, short of a mark's 3:1, 3.00:1 on the HUD's danger button, short of a label's 4.5:1, and 2.34:1 under a check on the HUD's success");
            foreach (var (name, colors) in new[] { ("Paper", paper), ("HUD", hud) })
            {
                Assert(Content(TopiaForgeFillRole.FilledButton, colors, false) == dark, name + ": a filled button's label is dark on the brand orange");
                Assert(Content(TopiaForgeFillRole.ControlOn, colors, false) == dark, name + ": a switch's thumb and a checkbox's check are dark on the brand orange");
            }

            Assert(Content(TopiaForgeFillRole.DangerButton, hud, false) == dark && Content(TopiaForgeFillRole.StepDone, hud, false) == dark &&
                   Content(TopiaForgeFillRole.StepFailed, hud, false) == dark, "the HUD's bright status fills take the dark content");
            Assert(Content(TopiaForgeFillRole.DangerButton, paper, false) == white && Content(TopiaForgeFillRole.StepDone, paper, false) == white &&
                   Content(TopiaForgeFillRole.StepFailed, paper, false) == white, "white stays on Paper's darker status fills, where it reads");

            var contrast = TopiaForgeSchemes.ResolvePaper(null, true);
            Assert(TopiaForgeContrast.Luminance(Content(TopiaForgeFillRole.FilledButton, contrast, true)) < TopiaForgeContrast.Luminance(dark),
                "in high contrast the dark label is darkened until it reads at 7:1 on the orange while pressed");
        }

        private static void BrandFillsKeepTheirColors()
        {
            foreach (var scheme in new[] { TopiaForgeScheme.Paper, TopiaForgeScheme.Hud })
            {
                foreach (var highContrast in new[] { false, true })
                {
                    var colors = TopiaForgeSchemes.Resolve(scheme, null, highContrast);
                    var context = scheme + (highContrast ? " high contrast" : string.Empty);
                    Assert(Fill(TopiaForgeFillRole.FilledButton, colors, highContrast) == colors.Primary && Fill(TopiaForgeFillRole.ControlOn, colors, highContrast) == colors.Primary,
                        context + ": buttons, switches and checkboxes keep the brand orange");
                    Assert(Fill(TopiaForgeFillRole.SwitchOff, colors, highContrast) == colors.Tint, context + ": an off switch keeps the tint");
                    Assert(Fill(TopiaForgeFillRole.StepDone, colors, highContrast) == colors.Success && Fill(TopiaForgeFillRole.StepFailed, colors, highContrast) == colors.Danger,
                        context + ": a stepper keeps the status colors");
                }
            }

            Assert(Fill(TopiaForgeFillRole.DangerButton, TopiaForgeSchemes.ResolveHud(null, false), false) == TopiaForgePalette.HudDanger, "the HUD's danger button keeps its red");

            // White reads at 4.94:1 on Paper's danger red, but at only 4.47:1 while the button is hovered, and nothing dark
            // reads on it, so the button's red is one step darker.
            var paper = TopiaForgeSchemes.ResolvePaper(null, false);
            var hovered = paper.Danger.Scale(TopiaForgeOnColors.ButtonHoveredTint);
            Assert(TopiaForgeContrast.Ratio(TopiaForgePalette.White, hovered) < 4.5f && TopiaForgeContrast.Ratio(Black, paper.Danger.Scale(TopiaForgeOnColors.ButtonPressedTint)) < 4.5f,
                "neither white nor black reads on Paper's danger button in every state");
            Assert(Fill(TopiaForgeFillRole.DangerButton, paper, false) == paper.Danger.Scale(0.92f), "Paper's danger button is one step darker, as far as it must be");
            Assert(paper.Danger == TopiaForgePalette.Danger, "the danger role itself keeps the brand's red");
        }

        private static void OffSwitchThumbStaysFaint()
        {
            foreach (var highContrast in new[] { false, true })
            {
                var paper = TopiaForgeSchemes.ResolvePaper(null, highContrast);
                var paperThumb = Content(TopiaForgeFillRole.SwitchOff, paper, highContrast);
                Assert(Lum(paperThumb) < Lum(paper.TextFaint) && Lum(paperThumb) > Lum(paper.Text), "Paper's off thumb stays a faint grey, darkened until it reads while pressed");

                var hud = TopiaForgeSchemes.ResolveHud(null, highContrast);
                var hudThumb = Content(TopiaForgeFillRole.SwitchOff, hud, highContrast);
                Assert(Lum(hudThumb) > Lum(hud.TextFaint) && Lum(hudThumb) < Lum(hud.Text), "the HUD's off thumb stays a faint grey, lightened until it reads on the tint");
            }
        }

        private static void PublicOnRolesReadOnTheirFills()
        {
            foreach (var scheme in new[] { TopiaForgeScheme.Paper, TopiaForgeScheme.Hud })
            {
                foreach (var highContrast in new[] { false, true })
                {
                    var colors = TopiaForgeSchemes.Resolve(scheme, null, highContrast);
                    Assert(colors.OnPrimary == Content(TopiaForgeFillRole.FilledButton, colors, highContrast), scheme + ": OnPrimary is a filled button's label");
                    Assert(colors.OnStatus == Content(TopiaForgeFillRole.DangerButton, colors, highContrast), scheme + ": OnStatus is a danger button's label");
                    Assert(TopiaForgeContrast.Ratio(colors.OnPrimary, colors.Primary) >= TopiaForgeOnColors.LabelRatio(highContrast), scheme + ": OnPrimary reads on Primary");
                }
            }
        }

        private static void AnyFillResolvesToContentThatReads()
        {
            var ranges = new[] { (TopiaForgeOnColors.ButtonPressedTint, TopiaForgeOnColors.ButtonHoveredTint), (DefaultPressedTint, 1f), (1f, 1f) };
            var preferences = new[] { TopiaForgePalette.White, TopiaForgeOnColors.DarkContent, TopiaForgeRgba.Hex(0x808080), TopiaForgePalette.FaintText };
            for (uint red = 0; red <= 0xFF; red += 0x33)
            {
                for (uint green = 0; green <= 0xFF; green += 0x33)
                {
                    for (uint blue = 0; blue <= 0xFF; blue += 0x33)
                    {
                        var fill = TopiaForgeRgba.Hex((red << 16) | (green << 8) | blue);
                        foreach (var preferred in preferences)
                        {
                            foreach (var minimum in new[] { 3f, 4.5f, 7f })
                            {
                                foreach (var (darkest, lightest) in ranges)
                                {
                                    var pair = TopiaForgeOnColors.OnFill(fill, preferred, minimum, darkest, lightest);
                                    var context = preferred + " on " + fill + " at " + minimum.ToString(CultureInfo.InvariantCulture) + ":1";
                                    var ratio = Lowest(pair.Content, pair.Fill, darkest, lightest);
                                    Assert(ratio >= minimum, context + " resolves to content that reads (" + Format(ratio) + ")");
                                    if (Lowest(preferred, fill, darkest, lightest) >= minimum)
                                    {
                                        Assert(pair.Content == preferred && pair.Fill == fill, context + ": content that already reads is left as it is");
                                    }

                                    if (Lowest(TopiaForgePalette.White, fill, darkest, lightest) >= minimum + 0.01f || Lowest(Black, fill, darkest, lightest) >= minimum + 0.01f)
                                    {
                                        Assert(pair.Fill == fill, context + ": the fill keeps its color while white or black reads on it");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void SliderHandleShowsOnItsTrack()
        {
            var paper = TopiaForgeSchemes.ResolvePaper(null, false);
            Assert(TopiaForgeContrast.Ratio(TopiaForgePalette.White, paper.Surface) < 1.1f && TopiaForgeContrast.Ratio(TopiaForgePalette.White, paper.SurfaceSunken) < 1.2f,
                "a white handle read at 1.02:1 on Paper's surface and 1.13:1 on its track");
            Assert(TopiaForgeOnColors.SliderHandle(TopiaForgeScheme.Paper, paper, false) == TopiaForgePalette.AccentDark, "Paper's handle is the brand's teal, which already reads");
            foreach (var highContrast in new[] { false, true })
            {
                foreach (var accent in Accents())
                {
                    var colors = TopiaForgeSchemes.ResolvePaper(accent, highContrast);
                    var handle = TopiaForgeOnColors.SliderHandle(TopiaForgeScheme.Paper, colors, highContrast);
                    var surfaces = new[] { colors.Surface, colors.SurfaceAlt, colors.SurfaceSunken, colors.Tint, colors.SelectedTint, colors.SurfaceSunken.Scale(TopiaForgeSchemes.PressedTint) };
                    AssertHandleReads(handle, surfaces, highContrast, "Paper" + (highContrast ? " high contrast" : string.Empty) + (accent.HasValue ? " with accent " + accent.Value : string.Empty));
                }

                var hud = TopiaForgeSchemes.ResolveHud(null, highContrast);
                var hudHandle = TopiaForgeOnColors.SliderHandle(TopiaForgeScheme.Hud, hud, highContrast);
                Assert(hudHandle == TopiaForgePalette.White, "the HUD's handle stays white");
                var hudSurfaces = new[] { hud.Surface.WithAlpha(1f), hud.SurfaceAlt.WithAlpha(1f), hud.SurfaceSunken.WithAlpha(1f), hud.Tint.WithAlpha(1f), hud.SelectedTint.WithAlpha(1f) };
                AssertHandleReads(hudHandle, hudSurfaces, highContrast, "HUD" + (highContrast ? " high contrast" : string.Empty));
            }
        }

        private static void AssertHandleReads(TopiaForgeRgba handle, TopiaForgeRgba[] surfaces, bool highContrast, string context)
        {
            foreach (var surface in surfaces)
            {
                foreach (var tint in new[] { 1f, DefaultHighlightedTint, DefaultPressedTint })
                {
                    var ratio = TopiaForgeContrast.Ratio(handle.Scale(tint), surface);
                    Assert(ratio >= TopiaForgeOnColors.MarkRatio(highContrast), context + ": the slider handle reads on " + surface + " at tint " +
                        tint.ToString("0.###", CultureInfo.InvariantCulture) + " (" + Format(ratio) + ")");
                }
            }
        }

        private static void WidgetsDrawTheResolvedColors()
        {
            var kit = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            string Read(params string[] parts) => File.ReadAllText(Path.Combine(kit, Path.Combine(parts)));

            var button = Read("Widgets", "TopiaForgeButton.cs");
            Require(button, "TopiaForgeFillRole.FilledButton", "TopiaForgeFillRole.DangerButton", "Tint(TopiaForgeOnColors.ButtonHoveredTint)",
                "Tint(TopiaForgeOnColors.ButtonPressedTint)", "Tint(TopiaForgeOnColors.ButtonSelectedTint)");
            Forbid(button, "Buttons draw the resolved label and tint by the tints it is resolved at", @"theme\.OnPrimary", @"theme\.OnStatus", @"\b1\.06f", @"\b0\.94f", @"\b1\.04f");

            var toggle = Read("Widgets", "TopiaForgeToggle.cs");
            Require(toggle, "TopiaForgeFillRole.ControlOn", "TopiaForgeFillRole.SwitchOff");
            Forbid(toggle, "A switch's thumb and a checkbox's check take the resolved colors, on uGUI's default color block", @"theme\.OnPrimary", @"theme\.TextFaint", @"\.colors\b");

            var slider = Read("Widgets", "TopiaForgeSlider.cs");
            Require(slider, "handle.color = theme.SliderHandle;");
            Forbid(slider, "A slider's handle takes the resolved color, on uGUI's default color block", @"theme\.OnPrimary", @"\.colors\b");

            var stepper = Read("Widgets", "TopiaForgePhaseStepper.Layout.cs");
            Require(stepper, "TopiaForgeFillRole.StepDone", "TopiaForgeFillRole.StepFailed");
            Forbid(stepper, "A stepper's marks take the resolved colors", @"theme\.OnStatus");

            var theme = Read("Theme", "TopiaForgeResolvedTheme.cs");
            Require(theme, "TopiaForgeOnColors.ResolveAll(colors, HighContrast)", "TopiaForgeOnColors.SliderHandle(scheme, colors, HighContrast)");

            foreach (var file in Directory.EnumerateFiles(Path.Combine(kit, "Widgets"), "*.cs"))
            {
                Forbid(File.ReadAllText(file), Path.GetFileName(file) + " draws a resolved color, not white, on a fill", @"theme\.OnPrimary", @"theme\.OnStatus");
            }
        }

        private static (TopiaForgeFillRole Role, bool Label, float[] Tints)[] Roles()
        {
            var button = new[] { 1f, TopiaForgeOnColors.ButtonHoveredTint, TopiaForgeOnColors.ButtonSelectedTint, TopiaForgeOnColors.ButtonPressedTint };
            var control = new[] { 1f, DefaultHighlightedTint, DefaultPressedTint };
            var still = new[] { 1f };
            return new[]
            {
                (TopiaForgeFillRole.FilledButton, true, button), (TopiaForgeFillRole.DangerButton, true, button),
                (TopiaForgeFillRole.ControlOn, false, control), (TopiaForgeFillRole.SwitchOff, false, control),
                (TopiaForgeFillRole.StepDone, false, still), (TopiaForgeFillRole.StepFailed, false, still),
            };
        }

        private static IEnumerable<TopiaForgeRgba?> Accents()
        {
            yield return null;
            for (uint level = 0; level <= 0xFF; level += 0x33)
            {
                yield return TopiaForgeRgba.Hex(level * 0x010101);
            }

            foreach (var hex in new uint[] { 0xAAFF66, 0xFFE14D, 0x767676, 0xFF00FF, 0x0000FF, 0xFF0000, 0x008080 })
            {
                yield return TopiaForgeRgba.Hex(hex);
            }
        }

        private static TopiaForgeRgba Content(TopiaForgeFillRole role, TopiaForgeSchemeColors colors, bool highContrast) =>
            TopiaForgeOnColors.Resolve(role, colors, highContrast).Content;

        private static TopiaForgeRgba Fill(TopiaForgeFillRole role, TopiaForgeSchemeColors colors, bool highContrast) =>
            TopiaForgeOnColors.Resolve(role, colors, highContrast).Fill;

        private static float Lum(TopiaForgeRgba color) => TopiaForgeContrast.Luminance(color);

        // The lowest ratio over a fill's states, measured here rather than through the code under test.
        private static float Lowest(TopiaForgeRgba content, TopiaForgeRgba fill, float darkest, float lightest)
        {
            var lowest = float.MaxValue;
            foreach (var tint in new[] { darkest, lightest, 1f, (darkest + lightest) / 2f })
            {
                lowest = Math.Min(lowest, TopiaForgeContrast.Ratio(content, fill.Scale(tint)));
            }

            return lowest;
        }

        // uGUI multiplies the stored values by a tint in a gamma project, and linear light in a linear one.
        private static TopiaForgeRgba Tinted(TopiaForgeRgba fill, float tint, bool linear)
        {
            return linear
                ? new TopiaForgeRgba(Encode(Decode(fill.R) * tint), Encode(Decode(fill.G) * tint), Encode(Decode(fill.B) * tint), fill.A)
                : fill.Scale(tint);
        }

        private static float Decode(float channel) =>
            channel <= 0.04045f ? channel / 12.92f : (float)Math.Pow((channel + 0.055) / 1.055, 2.4);

        private static float Encode(float channel)
        {
            channel = Math.Min(1f, Math.Max(0f, channel));
            return channel <= 0.0031308f ? channel * 12.92f : (float)((1.055 * Math.Pow(channel, 1.0 / 2.4)) - 0.055);
        }

        private static void Require(string source, params string[] expected)
        {
            foreach (var text in expected)
            {
                Assert(source.Contains(text, StringComparison.Ordinal), "the kit draws the resolved colors with " + text);
            }
        }

        private static void Forbid(string source, string message, params string[] patterns)
        {
            foreach (var pattern in patterns)
            {
                Assert(!Regex.IsMatch(source, pattern), message + " (found " + pattern + ")");
            }
        }

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
