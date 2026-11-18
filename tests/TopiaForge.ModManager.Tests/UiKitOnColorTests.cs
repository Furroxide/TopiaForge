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
    /// accent a mod picks: a label at WCAG AA and a mark at 3:1, or 7:1 and 4.5:1 in high contrast. Each state keeps the
    /// kit's usual tint while the content still reads, and otherwise takes the mirror tint, away from the content, so the
    /// brand's fills keep their colors and a pressed fill still differs from a hovered one. Before, all of it was
    /// white: 2.61:1 on the brand orange under a filled button's label, a switch's thumb and a checkbox's check, 3.00:1 on
    /// the HUD's danger button and 2.34:1 under a HUD stepper's check, lower still in high contrast, which lightens the
    /// HUD's status fills. A slider's white handle read at 1.02:1 on Paper's surface, and an off switch's grey thumb at
    /// 2.78:1 on the HUD's tint.
    /// </summary>
    internal static class UiKitOnColorTests
    {
        // uGUI's default color block, also used by the slider handle.
        private const float DefaultHighlightedTint = 245f / 255f;
        private const float DefaultPressedTint = 200f / 255f;

        private static readonly TopiaForgeRgba Black = new TopiaForgeRgba(0f, 0f, 0f);

        public static void Run()
        {
            ContentReadsOnEveryFillInEveryState();
            WhiteStaysWhereItReadsAndDarkTakesOver();
            BrandFillsKeepTheirColors();
            StatesKeepTheUsualTintWhereItReads();
            OffSwitchThumbStaysFaint();
            PublicOnRolesReadOnTheirFills();
            AnyFillResolvesToContentThatReads();
            SliderHandleShowsOnItsTrack();
            WidgetsDrawTheResolvedColors();
            Console.WriteLine("UiKitOnColorTests passed.");
        }

        private static void ContentReadsOnEveryFillInEveryState()
        {
            Assert(TopiaForgeOnColors.ControlPressedTint == DefaultPressedTint && TopiaForgeOnColors.ControlHighlightedTint == DefaultHighlightedTint,
                "switches and checkboxes start from uGUI's default color block");
            Assert(Roles().Length == Enum.GetValues(typeof(TopiaForgeFillRole)).Length, "every fill role is tested in every state it is drawn in");
            foreach (var scheme in new[] { TopiaForgeScheme.Paper, TopiaForgeScheme.Hud })
            {
                foreach (var highContrast in new[] { false, true })
                {
                    foreach (var accent in Accents())
                    {
                        var colors = TopiaForgeSchemes.Resolve(scheme, accent, highContrast);
                        var all = TopiaForgeOnColors.ResolveAll(scheme, colors, highContrast);
                        var context = scheme + (highContrast ? " high contrast" : string.Empty) + (accent.HasValue ? " with accent " + accent.Value : string.Empty);
                        foreach (var (role, label, _) in Roles())
                        {
                            var pair = all[(int)role];
                            var single = TopiaForgeOnColors.Resolve(role, scheme, colors, highContrast);
                            Assert(pair.Fill == single.Fill && pair.Content == single.Content && pair.Hovered == single.Hovered &&
                                   pair.Selected == single.Selected && pair.Pressed == single.Pressed,
                                context + ": " + role + " resolves the same alone and with the rest");
                            Assert(pair.Fill.A == 1f && pair.Content.A == 1f, context + ": " + role + " draws opaque colors");
                            var minimum = highContrast ? (label ? 7f : 4.5f) : (label ? 4.5f : 3f);
                            foreach (var tint in new[] { 1f, pair.Hovered, pair.Selected, pair.Pressed })
                            {
                                foreach (var linear in new[] { false, true })
                                {
                                    var ratio = DrawnRatio(pair.Content, Tinted(TopiaForgeContrast.Quantize(pair.Fill), tint, linear));
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
            foreach (var (scheme, colors, ink) in new[]
            {
                (TopiaForgeScheme.Paper, paper, TopiaForgeRgba.Hex(0x2D3748)),
                (TopiaForgeScheme.Hud, hud, TopiaForgeRgba.Hex(0x10141B)),
            })
            {
                Assert(Content(TopiaForgeFillRole.FilledButton, scheme, colors, false) == ink, scheme + ": a filled button uses the scheme's exact ink on orange");
                Assert(Content(TopiaForgeFillRole.ControlOn, scheme, colors, false) == ink, scheme + ": a switch thumb and checkbox check use the scheme's exact ink on orange");
            }

            Assert(Content(TopiaForgeFillRole.DangerButton, TopiaForgeScheme.Hud, hud, false) == dark && Content(TopiaForgeFillRole.StepDone, TopiaForgeScheme.Hud, hud, false) == dark &&
                   Content(TopiaForgeFillRole.StepFailed, TopiaForgeScheme.Hud, hud, false) == dark, "the HUD's bright status fills take the dark content");
            Assert(Content(TopiaForgeFillRole.DangerButton, TopiaForgeScheme.Paper, paper, false) == white && Content(TopiaForgeFillRole.StepDone, TopiaForgeScheme.Paper, paper, false) == white &&
                   Content(TopiaForgeFillRole.StepFailed, TopiaForgeScheme.Paper, paper, false) == white, "white stays on Paper's darker status fills, where it reads");

            // In high contrast Paper's ink is darkened until it reads at 7:1 on the orange; the orange then lightens, never
            // darkens, in every state.
            var contrast = TopiaForgeSchemes.ResolvePaper(null, true);
            var highContrastFilled = TopiaForgeOnColors.Resolve(TopiaForgeFillRole.FilledButton, TopiaForgeScheme.Paper, contrast, true);
            Assert(TopiaForgeContrast.Luminance(highContrastFilled.Content) < TopiaForgeContrast.Luminance(TopiaForgePalette.Ink) &&
                   TopiaForgeContrast.Ratio(highContrastFilled.Content, highContrastFilled.Fill) >= 7f,
                "in high contrast the filled button's label is Paper's ink, darkened until it reads at 7:1 on the orange");
            Assert(highContrastFilled.Hovered >= 1f && highContrastFilled.Selected >= 1f && highContrastFilled.Pressed > 1f,
                "in high contrast the orange only lightens under its dark label");
        }

        private static void BrandFillsKeepTheirColors()
        {
            foreach (var scheme in new[] { TopiaForgeScheme.Paper, TopiaForgeScheme.Hud })
            {
                foreach (var highContrast in new[] { false, true })
                {
                    var colors = TopiaForgeSchemes.Resolve(scheme, null, highContrast);
                    var context = scheme + (highContrast ? " high contrast" : string.Empty);
                    Assert(Fill(TopiaForgeFillRole.FilledButton, scheme, colors, highContrast) == colors.Primary && Fill(TopiaForgeFillRole.ControlOn, scheme, colors, highContrast) == colors.Primary,
                        context + ": buttons, switches and checkboxes keep the brand orange");
                    Assert(Fill(TopiaForgeFillRole.SwitchOff, scheme, colors, highContrast) == colors.Tint, context + ": an off switch keeps the tint");
                    Assert(Fill(TopiaForgeFillRole.StepDone, scheme, colors, highContrast) == colors.Success && Fill(TopiaForgeFillRole.StepFailed, scheme, colors, highContrast) == colors.Danger,
                        context + ": a stepper keeps the status colors");
                }
            }

            Assert(Fill(TopiaForgeFillRole.DangerButton, TopiaForgeScheme.Hud, TopiaForgeSchemes.ResolveHud(null, false), false) == TopiaForgePalette.HudDanger, "the HUD's danger button keeps its red");

            // White reads at 4.94:1 on Paper's danger red at rest. Hovering darkens it instead of lightening it toward white,
            // so the button keeps the brand's red in every state.
            var paper = TopiaForgeSchemes.ResolvePaper(null, false);
            Assert(Fill(TopiaForgeFillRole.DangerButton, TopiaForgeScheme.Paper, paper, false) == TopiaForgePalette.Danger, "Paper's danger button keeps the brand's red");
            Assert(paper.Danger == TopiaForgePalette.Danger, "the danger role itself keeps the brand's red");
        }

        private static void StatesKeepTheUsualTintWhereItReads()
        {
            foreach (var scheme in new[] { TopiaForgeScheme.Paper, TopiaForgeScheme.Hud })
            {
                foreach (var highContrast in new[] { false, true })
                {
                    foreach (var accent in Accents())
                    {
                        var colors = TopiaForgeSchemes.Resolve(scheme, accent, highContrast);
                        var context = scheme + (highContrast ? " high contrast" : string.Empty) + (accent.HasValue ? " with accent " + accent.Value : string.Empty);
                        foreach (var (role, label, usual) in Roles())
                        {
                            var pair = TopiaForgeOnColors.Resolve(role, scheme, colors, highContrast);
                            var minimum = highContrast ? (label ? 7f : 4.5f) : (label ? 4.5f : 3f);
                            if (usual.Length == 0)
                            {
                                Assert(pair.Hovered == 1f && pair.Selected == 1f && pair.Pressed == 1f, context + ": " + role + " has no interaction states to tint");
                                continue;
                            }

                            var hoverMirrored = pair.Hovered != usual[0];
                            var states = new[] { ("hovered", pair.Hovered, usual[0]), ("selected", pair.Selected, usual[1]) };
                            foreach (var (name, tint, expected) in states)
                            {
                                Assert(tint == expected || tint == TopiaForgeOnColors.Mirror(expected), context + ": " + role + " " + name + " uses the usual tint or its mirror");
                                if (tint != expected)
                                {
                                    var followsHover = name == "selected" && hoverMirrored;
                                    Assert(followsHover || Lowest(pair.Content, pair.Fill, expected) < minimum,
                                        context + ": " + role + " " + name + " is mirrored only where the usual tint would not read, or to follow hover");
                                }
                            }

                            Assert((pair.Selected - 1f) * (pair.Hovered - 1f) >= 0f, context + ": " + role + " moves the same way selected as hovered");

                            Assert(Drawn(pair.Fill, pair.Pressed) != Drawn(pair.Fill, pair.Hovered),
                                context + ": " + role + " draws different bytes pressed and hovered, with or without the press's motion");
                            Assert(Lowest(pair.Content, pair.Fill, pair.Pressed) >= minimum, context + ": " + role + " reads while pressed");
                        }
                    }
                }
            }

            // Paper's orange lightens while pressed, away from its dark label, and its danger red darkens while hovered, away
            // from its white one; pressing then darkens the red further than hovering does.
            var paper = TopiaForgeSchemes.ResolvePaper(null, false);
            var filled = TopiaForgeOnColors.Resolve(TopiaForgeFillRole.FilledButton, TopiaForgeScheme.Paper, paper, false);
            Assert(filled.Hovered == TopiaForgeOnColors.ButtonHoveredTint && filled.Pressed == 1.12f, "Paper's filled button lightens by 6% when hovered and 12% when pressed");
            var danger = TopiaForgeOnColors.Resolve(TopiaForgeFillRole.DangerButton, TopiaForgeScheme.Paper, paper, false);
            Assert(danger.Hovered == TopiaForgeOnColors.ButtonPressedTint && danger.Pressed == 0.88f && danger.Selected == 0.96f,
                "Paper's danger button darkens by 6% when hovered, 12% when pressed and 4% when selected");
            Assert(Drawn(danger.Fill, danger.Pressed) != Drawn(danger.Fill, danger.Hovered), "Paper's danger button draws different bytes pressed and hovered");

            // The mirror of a kit tint is exactly the other kit tint on every runtime, so a one-ulp float difference can
            // never decide whether press goes further than hover.
            Assert(TopiaForgeOnColors.Mirror(TopiaForgeOnColors.ButtonHoveredTint) == TopiaForgeOnColors.ButtonPressedTint &&
                   TopiaForgeOnColors.Mirror(TopiaForgeOnColors.ButtonPressedTint) == TopiaForgeOnColors.ButtonHoveredTint &&
                   TopiaForgeOnColors.Mirror(TopiaForgeOnColors.ButtonSelectedTint) == 0.96f,
                "mirror tints are exact");
            Assert(TopiaForgeContrast.Ratio(TopiaForgePalette.White, paper.Danger.Scale(TopiaForgeOnColors.ButtonHoveredTint)) < 4.5f,
                "white read at only 4.47:1 on Paper's danger red lightened for hover, which is why hover darkens it instead");
        }

        private static void OffSwitchThumbStaysFaint()
        {
            foreach (var highContrast in new[] { false, true })
            {
                var paper = TopiaForgeSchemes.ResolvePaper(null, highContrast);
                var paperThumb = Content(TopiaForgeFillRole.SwitchOff, TopiaForgeScheme.Paper, paper, highContrast);
                Assert(Lum(paperThumb) <= Lum(paper.TextFaint) && Lum(paperThumb) > Lum(paper.Text), "Paper's off thumb stays a faint grey, darkened only as far as it must be to read");

                var hud = TopiaForgeSchemes.ResolveHud(null, highContrast);
                var hudThumb = Content(TopiaForgeFillRole.SwitchOff, TopiaForgeScheme.Hud, hud, highContrast);
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
                    Assert(colors.OnPrimary == Content(TopiaForgeFillRole.FilledButton, scheme, colors, highContrast), scheme + ": OnPrimary is a filled button's label");
                    Assert(colors.OnDanger == Content(TopiaForgeFillRole.DangerButton, scheme, colors, highContrast), scheme + ": OnDanger is a danger button's label");
                    Assert(TopiaForgeContrast.Ratio(colors.OnPrimary, colors.Primary) >= TopiaForgeOnColors.LabelRatio(highContrast), scheme + ": OnPrimary reads on Primary");
                    var danger = TopiaForgeOnColors.Resolve(TopiaForgeFillRole.DangerButton, scheme, colors, highContrast);
                    Assert(TopiaForgeContrast.Ratio(colors.OnDanger, danger.Fill) >= TopiaForgeOnColors.LabelRatio(highContrast), scheme + ": OnDanger reads on the danger button's fill");
                    Assert(highContrast || danger.Fill == colors.Danger, scheme + ": the danger button's fill is Danger itself outside high contrast");
                }
            }
        }

        private static void AnyFillResolvesToContentThatReads()
        {
            var tints = new[] { TopiaForgeOnColors.ButtonHoveredTint, TopiaForgeOnColors.ButtonSelectedTint, TopiaForgeOnColors.ButtonPressedTint, DefaultHighlightedTint, DefaultPressedTint };
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
                                var pair = TopiaForgeOnColors.OnFill(fill, preferred, minimum);
                                var context = preferred + " on " + fill + " at " + minimum.ToString(CultureInfo.InvariantCulture) + ":1";
                                var ratio = Lowest(pair.Content, pair.Fill, 1f);
                                Assert(ratio >= minimum, context + " resolves to content that reads (" + Format(ratio) + ")");
                                if (Lowest(preferred, fill, 1f) >= minimum)
                                {
                                    Assert(pair.Content == preferred && pair.Fill == fill, context + ": content that already reads is left as it is");
                                }

                                if (Lowest(TopiaForgePalette.White, fill, 1f) >= minimum + 0.01f || Lowest(Black, fill, 1f) >= minimum + 0.01f)
                                {
                                    Assert(pair.Fill == fill, context + ": the fill keeps its color while white or black reads on it");
                                }

                                foreach (var usual in tints)
                                {
                                    var tint = TopiaForgeOnColors.ReadableTint(pair.Fill, pair.Content, minimum, usual);
                                    var tinted = Lowest(pair.Content, pair.Fill, tint);
                                    var at = context + " at tint " + usual.ToString("0.###", CultureInfo.InvariantCulture);
                                    Assert(tinted >= minimum, at + " keeps reading (" + Format(tinted) + ")");
                                    // Within 0.01 of the threshold either answer is right, since rounding decides it.
                                    var usualRatio = Lowest(pair.Content, pair.Fill, usual);
                                    if (usualRatio >= minimum + 0.01f)
                                    {
                                        Assert(tint == usual, at + ": the usual tint is kept while the content reads on it");
                                    }
                                    else if (usualRatio < minimum - 0.01f)
                                    {
                                        // The pair reads at rest (asserted above), so the mirror, which moves away from the
                                        // content, always reads too: a state never falls back to rest here.
                                        Assert(tint == TopiaForgeOnColors.Mirror(usual), at + ": a state that would stop the content reading takes the mirror tint");
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
                    var context = "Paper" + (highContrast ? " high contrast" : string.Empty) + (accent.HasValue ? " with accent " + accent.Value : string.Empty);
                    Assert(handle.A == 1f, context + ": the slider handle is opaque, whatever alpha the accent has");
                    AssertHandleReads(handle, surfaces, highContrast, context);
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
                    var ratio = DrawnRatio(TopiaForgeContrast.Quantize(handle).Scale(tint), surface);
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
            Require(button, "theme.Fill(TopiaForgeFillRole.FilledButton)", "theme.Fill(TopiaForgeFillRole.DangerButton)",
                "button.colors = paint.HasValue ? paint.Value.Tinted(colors) : colors;", "SetContentColor(resolved.Content);", "fill.color = resolved.Fill;");
            var applyTheme = button.Substring(button.IndexOf("public void ApplyTheme(", StringComparison.Ordinal));
            applyTheme = applyTheme.Substring(0, applyTheme.IndexOf("private static Color Tint(", StringComparison.Ordinal));
            Assert(Regex.Matches(applyTheme, @"button\.colors\s*=").Count == 1, "a button writes its color block once per theme application, so one fade runs");
            Forbid(button, "Buttons draw the resolved label and state tints", @"theme\.OnPrimary", @"theme\.OnDanger", @"\b1\.06f", @"\b0\.94f", @"\b1\.04f");

            var toggle = Read("Widgets", "TopiaForgeToggle.cs");
            Require(toggle, "theme.Fill(value ? TopiaForgeFillRole.ControlOn : TopiaForgeFillRole.SwitchOff)", "toggle.colors = paint.Tinted(toggle.colors);",
                "on.Tinted(toggle.colors)", "thumb.color = paint.Content;", "check.color = on.Content;");
            Forbid(toggle, "A switch's thumb and a checkbox's check take the resolved colors and state tints", @"theme\.OnPrimary", @"theme\.TextFaint", @"PressedTint\(theme");

            var slider = Read("Widgets", "TopiaForgeSlider.cs");
            Require(slider, "handle.color = theme.SliderHandle;");
            Forbid(slider, "A slider's handle takes the resolved color, on uGUI's default color block", @"theme\.OnPrimary", @"\.colors\b");

            var stepper = Read("Widgets", "TopiaForgePhaseStepper.Layout.cs");
            Require(stepper, "theme.Fill(TopiaForgeFillRole.StepDone)", "theme.Fill(TopiaForgeFillRole.StepFailed)", "SetIcon(view, doneSprite!, done.Content);",
                "SetIcon(view, failedSprite!, failed.Content);");
            Forbid(stepper, "A stepper's marks take the resolved colors", @"theme\.OnDanger");

            var theme = Read("Theme", "TopiaForgeResolvedTheme.cs");
            Require(theme, "TopiaForgeOnColors.ResolveAll(scheme, colors, highContrast)", "TopiaForgeOnColors.SliderHandle(scheme, colors, HighContrast)",
                "PackFills(authoredScheme, HighContrast)", "block.pressedColor = Pressed;");
            var pack = Read("Theme", "TopiaForgeResolvedTheme.Packs.cs");
            Require(pack, "Button(primary, labelRatio)", "Button(danger, labelRatio)", "UiThemeColors.ResolveTints(pair, ratio",
                "StateTint(tints.Hovered)", "StateTint(tints.Selected)", "StateTint(tints.Pressed)");

            foreach (var file in Directory.EnumerateFiles(Path.Combine(kit, "Widgets"), "*.cs"))
            {
                Forbid(File.ReadAllText(file), Path.GetFileName(file) + " draws a resolved color, not white, on a fill", @"theme\.OnPrimary", @"theme\.OnDanger");
            }
        }

        // Each role, whether it is a label, and the usual hovered, selected and pressed tints of the control it sits on.
        private static (TopiaForgeFillRole Role, bool Label, float[] Usual)[] Roles()
        {
            var button = new[] { TopiaForgeOnColors.ButtonHoveredTint, TopiaForgeOnColors.ButtonSelectedTint, TopiaForgeOnColors.ButtonPressedTint };
            var control = new[] { DefaultHighlightedTint, DefaultHighlightedTint, DefaultPressedTint };
            var still = new float[0];
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

            foreach (var hex in new uint[] { 0xAAFF66, 0xFFE14D, 0x767676, 0xFF00FF, 0x0000FF, 0xFF0000, 0x008080, 0x0075AC })
            {
                yield return TopiaForgeRgba.Hex(hex);
            }

            // A mod can give SetAccent any alpha: a transparent accent and a translucent one.
            yield return TopiaForgeRgba.Hex(0x008080, 0f);
            yield return TopiaForgeRgba.Hex(0xFFE14D, 0.5f);
        }

        private static TopiaForgeRgba Content(TopiaForgeFillRole role, TopiaForgeScheme scheme, TopiaForgeSchemeColors colors, bool highContrast) =>
            TopiaForgeOnColors.Resolve(role, scheme, colors, highContrast).Content;

        private static TopiaForgeRgba Fill(TopiaForgeFillRole role, TopiaForgeScheme scheme, TopiaForgeSchemeColors colors, bool highContrast) =>
            TopiaForgeOnColors.Resolve(role, scheme, colors, highContrast).Fill;

        private static float Lum(TopiaForgeRgba color) => TopiaForgeContrast.Luminance(color);

        // The ratio at one tint, as computed and as drawn, measured here rather than through the code under test. uGUI stores
        // vertex colors in 8 bits a channel, so a tint above one saturates at white.
        private static float Lowest(TopiaForgeRgba content, TopiaForgeRgba fill, float tint)
        {
            var computed = TopiaForgeContrast.Ratio(content, Saturate(fill.Scale(tint)));
            var drawn = DrawnRatio(content, TopiaForgeContrast.Quantize(fill).Scale(tint));
            return Math.Min(computed, drawn);
        }

        private static TopiaForgeRgba Saturate(TopiaForgeRgba color) =>
            new TopiaForgeRgba(Math.Min(1f, color.R), Math.Min(1f, color.G), Math.Min(1f, color.B), color.A);

        // The fill's bytes as uGUI draws it in a gamma project: stored in 8 bits, multiplied by the tint, stored again.
        private static TopiaForgeRgba Drawn(TopiaForgeRgba fill, float tint) =>
            TopiaForgeContrast.Quantize(Saturate(TopiaForgeContrast.Quantize(fill).Scale(tint)));

        private static float DrawnRatio(TopiaForgeRgba content, TopiaForgeRgba fill) =>
            TopiaForgeContrast.Ratio(TopiaForgeContrast.Quantize(content), TopiaForgeContrast.Quantize(fill));

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
