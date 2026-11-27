using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        internal static void VerifyCompactCues()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            const string palette = """
                {"schema":1,"id":"compact-controls","paper":{"muted":"#404040","colors":{
                    "primary":{"fill":"#000000","on":"#FFFFFF"},"selected":{"fill":"#000000","on":"#FFFFFF"},
                    "sunken":{"fill":"#000000","on":"#FFFFFF"}}},
                 "hud":{"muted":"#404040","colors":{"primary":{"fill":"#010101","on":"#FFFFFF"},
                    "selected":{"fill":"#010101","on":"#FFFFFF"},"sunken":{"fill":"#010101","on":"#FFFFFF"}}}}
                """;
            Check(ui.AddThemeFixture(palette).Succeeded, "Admit black and near-black compact control pairs.");
            var toggleChanges = 0;
            var sliderChanges = 0;
            using var surface = Make(ui, new UiSurfaceRequest("compact-control-cues", "Compact control cues", "",
                width: 620f, height: 360f, content: new UiColumn(
                    new UiToggle("tracking", "Tracking", true, _ => toggleChanges++),
                    new UiSlider("range", "Range", 10f, 20f, 15f, _ => sliderChanges++),
                    new UiCheckbox("check", "Annotations", true, _ => { }),
                    new UiToggle("locked-tracking", "Tracking locked", true, _ => toggleChanges++, enabled: false),
                    new UiSlider("locked-range", "Range locked", 10f, 20f, 15f, _ => sliderChanges++, enabled: false),
                    new UiCheckbox("locked-check", "Annotations locked", true, _ => { }, enabled: false)))
                .WithTheme(new UiThemeRef("compact-controls")));
            ui.CompletePendingThemeRegistrations();

            foreach (var scale in new[] { 1f, 2f })
                foreach (var highContrast in new[] { false, true })
                    foreach (var reducedMotion in new[] { false, true })
                        foreach (var hud in new[] { false, true })
                        {
                            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast, scale, reducedMotion)).Succeeded,
                                "Apply the viewer's scale, contrast and motion preferences.");
                            var rest = Capture("Rest", hud);
                            var hover = Capture("Hovered", hud);
                            var focus = Capture("Focused", hud);
                            var selected = Capture("Selected", hud);
                            var pressed = Capture("Pressed", hud);
                            var both = Capture("SelectedPressed", hud);
                            foreach (var (id, body, mark) in new[] { ("tracking", "track", "thumb"), ("range", "track", "thumb"), ("check", "box", "check") })
                            {
                                Check(HasPart(both, id, "selection") && HasPart(both, id, "press"),
                                    "Each compact control, including the checkbox, shows both cues when the two active states collapse.");
                                var hit = Part(rest, id, "hit");
                                var track = Part(rest, id, body);
                                var label = Find(rest, id);
                                var bar = Part(both, id, "selection");
                                var outline = Part(both, id, "press");
                                var ring = Part(focus, id, "focus");
                                var stroke = scale * (highContrast ? 3f : 2f);
                                var minimum = highContrast ? 4.5f : 3f;
                                Check(track.FillColor == (hud ? "#010101" : "#000000") && Part(hover, id, body).FillColor == track.FillColor &&
                                    Part(both, id, body).FillColor == track.FillColor, "The compact control has collapsed quantized tints.");
                                Check(!HasPart(rest, id, "selection") && !HasPart(rest, id, "press") &&
                                    HasPart(selected, id, "selection") && !HasPart(selected, id, "press") &&
                                    !HasPart(pressed, id, "selection") && HasPart(pressed, id, "press"),
                                    "Compact controls show selection and press independently and clear both at rest.");
                                Check(bar is { Kind: "ControlSelectionBar", IsRendered: true, Clipped: false } && bar.Width == 3f * scale &&
                                    outline is { Kind: "ControlPressOutline", IsRendered: true, Clipped: false } && outline.BorderWidthPixels == stroke &&
                                    bar.ContrastRatio >= minimum && outline.ContrastRatio >= minimum &&
                                    bar.FillColor == label.FillColor && outline.FillColor == label.FillColor,
                                    "Both compact cues remain visible and contrast against the actual Surface backplate.");
                                Check(track.X == hit.X + 12f * scale &&
                                    outline.X + outline.Width <= track.X &&
                                    bar.X > outline.X + stroke && bar.X + bar.Width < outline.X + outline.Width - stroke,
                                    "The gutter reserves room for two separate cues without covering the track.");
                                Check(Geometry(hit) == Geometry(Part(both, id, "hit")) && Geometry(track) == Geometry(Part(both, id, body)) &&
                                    hit.Width >= 40f * scale && hit.Height >= 40f * scale && ring.X + (2f * scale + stroke) == hit.X &&
                                    ring.Width == hit.Width + 2f * (2f * scale + stroke) &&
                                    Geometry(Part(rest, id, mark)) == Geometry(Part(both, id, mark)),
                                    "The cues preserve the hit target, full focus ring and value mark.");
                            }
                            foreach (var (id, mark) in new[] { ("locked-tracking", "thumb"), ("locked-range", "thumb"), ("locked-check", "check") })
                            {
                                Check(!HasPart(both, id, "selection") && !HasPart(both, id, "press") &&
                                    Part(both, id, mark) is { Disabled: true } disabled && disabled.ContrastRatio >= (highContrast ? 7f : 4.5f),
                                    "Disabled controls keep their value mark at the disabled contrast threshold and show no input cues.");
                            }
                            Check(both.CheckAccessibility().Count == 0, "Combined compact cue captures pass the accessibility checks.");
                        }

            var on = Part(Capture("Rest", hud: false), "tracking", "thumb");
            Check(surface.ChangeToggle("tracking", false).Succeeded && toggleChanges == 1, "The switch still accepts its normal value callback.");
            var off = Part(Capture("SelectedPressed", hud: false), "tracking", "thumb");
            Check(on.Selected && !off.Selected && off.X < on.X && off.SemanticLabel == "Off" &&
                HasPart(Capture("SelectedPressed", hud: false), "tracking", "selection"),
                "The off thumb position and value stay independent from keyboard selection.");
            Check(surface.ChangeSlider("range", 10f).Succeeded, "The slider accepts its minimum.");
            var left = Part(Capture("Rest", hud: false), "range", "thumb");
            Check(surface.ChangeSlider("range", 20f).Succeeded, "The slider accepts its maximum.");
            var max = Capture("Rest", hud: false);
            var right = Part(max, "range", "thumb");
            var rail = Part(max, "range", "track");
            Check(left.X == rail.X && Math.Abs(right.X + right.Width - rail.X - rail.Width) < 0.001f &&
                !surface.ChangeSlider("range", 21f).Succeeded && !surface.ChangeSlider("locked-range", 14f).Succeeded &&
                sliderChanges == 2, "The gutter preserves both range endpoints and out-of-range refusal.");

            using var kit = Make(ui, new UiSurfaceRequest("compact-control-kit", "Ordinary controls", "",
                content: new UiColumn(new UiToggle("tracking", "Tracking", true, _ => { }),
                    new UiSlider("range", "Range", 10f, 20f, 15f, _ => { }))));
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences()).Succeeded, "Restore ordinary Paper accessibility.");
            var kitHover = CaptureForFixture(kit, Viewport, "Hovered", 0f, false);
            var kitPress = CaptureForFixture(kit, Viewport, "SelectedPressed", 0f, false);
            foreach (var id in new[] { "tracking", "range" })
            {
                Check(Part(kitHover, id, "track").FillColor != Part(kitPress, id, "track").FillColor &&
                    !HasPart(kitPress, id, "selection") && !HasPart(kitPress, id, "press"),
                    "Ordinary Paper switch and slider keep their readable state tints without extra cues.");
            }
            Console.WriteLine("Compact switch, slider and checkbox cues passed at both schemes, scales, contrasts and motion profiles.");

            UiTreeCapture Capture(string state, bool hud) => CaptureForFixture(surface, Viewport, state, 0f, hud);
            static UiCapturedNode Find(UiTreeCapture capture, string id) => capture.Nodes.Single(row => row.Id == id);
            static UiCapturedNode Part(UiTreeCapture capture, string id, string part) =>
                capture.Nodes.Single(row => row.Path == Find(capture, id).Path + "/" + part);
            static bool HasPart(UiTreeCapture capture, string id, string part) =>
                capture.Nodes.Any(row => row.Path == Find(capture, id).Path + "/" + part);
            static (float, float, float, float) Geometry(UiCapturedNode row) => (row.X, row.Y, row.Width, row.Height);
        }
    }
}
