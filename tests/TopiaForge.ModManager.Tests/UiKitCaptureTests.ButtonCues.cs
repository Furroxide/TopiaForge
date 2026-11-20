using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        internal static void VerifyButtonCues()
        {
            ButtonCuesSurviveColorQuantization();
            NativeButtonCueTransitionsStayIndependent();
            FieldsAndRowsUseQuantizedCues();
            NativeNonButtonCuesUseActualTargets();
            VerifyCompactCues();
        }

        private static void ButtonCuesSurviveColorQuantization()
        {
            var ink = new UiThemeColor(16, 16, 16);
            Check(UiControlPaint.ButtonCues(ink, new UiThemeStateTints(1.06f, 0.94f, 1f)) == UiButtonCue.Press,
                "Matching rest still needs a press outline when hover has different paint.");
            Check(UiControlPaint.ButtonCues(ink, new UiThemeStateTints(1.06f, 1f, 1.05f)) ==
                (UiButtonCue.Selection | UiButtonCue.Press),
                "Matching the quantized hover also needs an outline when press differs from rest.");

            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            const string palette = """
                {"schema":1,"id":"dark-buttons",
                 "paper":{"colors":{"primary":{"fill":"#000000","on":"#FFFFFF"}}},
                 "hud":{"colors":{"primary":{"fill":"#010101","on":"#FFFFFF"}}}}
                """;
            Check(ui.AddThemeFixture(palette).Succeeded, "The exact black and near-black Primary pairs are admissible.");
            using var surface = Make(ui, new UiSurfaceRequest("cue-window", "Button cues", "", width: 560f, height: 380f,
                content: new UiColumn(new UiButton("go", "Confirm", () => { }, UiButtonStyle.Primary),
                    new UiButton("danger", "Close route", () => { }, UiButtonStyle.Danger),
                    new UiButton("disabled", "Hold", () => { }, UiButtonStyle.Primary, enabled: false)))
                .WithTheme(new UiThemeRef("dark-buttons")));
            ui.CompletePendingThemeRegistrations();

            foreach (var highContrast in new[] { false, true })
            foreach (var highScale in new[] { false, true })
            foreach (var hud in new[] { false, true })
            {
                var scale = highScale ? 2f : 1f;
                Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast, scale, reducedMotion: true)).Succeeded,
                    "Apply the viewer's contrast, scale and reduced motion.");
                var rest = Capture("Rest");
                var selected = Capture("Selected");
                var press = Capture("Pressed");
                var both = Capture("SelectedPressed");
                var fill = Find(rest, "go");
                Check(fill.FillColor == (hud ? "#010101" : "#000000"), "The fixture exercises an unchanged quantized fill.");
                var selectionMarks = Marks(selected, fill);
                var pressMarks = Marks(press, fill);
                Check(Marks(rest, fill).Length == 0 && selectionMarks.Length == 1 && selectionMarks[0].Kind == "ButtonSelectionBar" &&
                    pressMarks.Length == 1 && pressMarks[0].Kind == "ButtonPressOutline",
                    "Each quantized state adds only its own mark and clears it at rest.");
                var bar = Marks(both, fill).Single(row => row.Kind == "ButtonSelectionBar");
                var outline = Marks(both, fill).Single(row => row.Kind == "ButtonPressOutline");
                var required = highContrast ? 4.5f : 3f;
                Check(bar.Width == 3f * scale && bar.ContrastRatio >= required && outline.ContrastRatio >= required &&
                    outline.BorderWidthPixels == (highContrast ? 3f : 2f) * scale &&
                    outline.X + outline.BorderWidthPixels < bar.X && outline.Y + outline.BorderWidthPixels < bar.Y,
                    "Both shapes fit inside the button and stay separately readable under reduced motion.");
                var active = Find(both, "go");
                Check(active.Selected && active.FillColor == fill.FillColor && active.X == fill.X && active.Y == fill.Y &&
                    Marks(both, Find(both, "disabled")).Length == 0 &&
                    both.CheckAccessibility().Count == 0,
                    "Selection and press coexist, preserve geometry under reduced motion, and never mark a disabled control.");
                var restHit = rest.Nodes.Single(row => row.Path == fill.Path + "/hit");
                var activeHit = both.Nodes.Single(row => row.Path == fill.Path + "/hit");
                Check((activeHit.X, activeHit.Y, activeHit.Width, activeHit.Height) ==
                    (restHit.X, restHit.Y, restHit.Width, restHit.Height), "The hit target stays in the same layout slot.");
                if (!hud && !highContrast)
                    Check(Marks(both, Find(both, "danger")).Length == 0 &&
                        Find(Capture("Hovered"), "danger").FillColor != Find(press, "danger").FillColor,
                        "Ordinary Paper Danger keeps its distinct readable tints.");

                UiTreeCapture Capture(string state) => CaptureForFixture(surface, Viewport, state, 0f, hud);
            }

            static UiCapturedNode Find(UiTreeCapture capture, string id) => capture.Nodes.Single(row => row.Id == id);
            static UiCapturedNode[] Marks(UiTreeCapture capture, UiCapturedNode button) => capture.Nodes.Where(row =>
                row.Path.StartsWith(button.Path + "/", StringComparison.Ordinal) &&
                row.Kind is "ButtonSelectionBar" or "ButtonPressOutline").ToArray();
        }
    }
}
