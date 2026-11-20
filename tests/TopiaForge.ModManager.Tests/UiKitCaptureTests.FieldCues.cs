using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void FieldsAndRowsUseQuantizedCues()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            const string definition = """
                {"schema":1,"id":"dark-fields",
                 "paper":{"colors":{"sunken":{"fill":"#000000","on":"#FFFFFF"},"selected":{"fill":"#010101","on":"#FFFFFF"}}},
                 "hud":{"colors":{"sunken":{"fill":"#010101","on":"#FFFFFF"},"selected":{"fill":"#000000","on":"#FFFFFF"}}}}
                """;
            Check(ui.AddThemeFixture(definition).Succeeded, "The fields and selected list use valid extreme pairs.");
            using var surface = Make(ui, new UiSurfaceRequest("fields-with-cues", "Route controls", "", width: 640f, height: 440f,
                content: new UiColumn(new UiTextInput("signal", "Signal", "North", _ => { }),
                    new UiDropdown("station", "Station", new[] { new UiChoice("north", "North") }, "north", _ => { }),
                    new UiVirtualList("routes", new[] { new UiListItem("ridge", "Ridge", "West") }, _ => { },
                        selectedItemId: "ridge", visibleRows: 1)))
                .WithTheme(new UiThemeRef("dark-fields")));
            ui.CompletePendingThemeRegistrations();

            foreach (var high in new[] { false, true })
                foreach (var useHud in new[] { false, true })
                    foreach (var scale in new[] { 1f, 2f })
                    {
                        Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(high, scale, reducedMotion: true)).Succeeded,
                            "Apply the field cue viewer profile.");
                        var rest = CaptureForFixture(surface, Viewport, "Rest", 0f, useHud);
                        var both = CaptureForFixture(surface, Viewport, "SelectedPressed", 0f, useHud);
                        foreach (var id in new[] { "signal", "station", "ridge" })
                        {
                            var control = both.Nodes.Single(row => row.Id == id);
                            var prefix = control.Path;
                            var bars = both.Nodes.Where(row => row.Path == prefix + "/selection" && row.Kind == "ControlSelectionBar").ToArray();
                            var outlines = both.Nodes.Where(row => row.Path == prefix + "/press" && row.Kind == "ControlPressOutline").ToArray();
                            Check(bars.Length == 1 && outlines.Length == 1, "Rounded field and row states expose both marks for " + id + ".");
                            var bar = bars[0];
                            var outline = outlines[0];
                            var minimum = high ? 4.5f : 3f;
                            Check(bar.Width == 3f * scale && bar.ContrastRatio >= minimum && outline.ContrastRatio >= minimum &&
                                outline.X + outline.BorderWidthPixels < bar.X && outline.Y + outline.BorderWidthPixels < bar.Y,
                                "The two readable shapes remain separate within " + id + ".");
                            Check(!rest.Nodes.Any(row => row.Path == bar.Path || row.Path == outline.Path),
                                "Press and focus changes do not leave transient marks on a resting " + id + ".");
                        }
                        Check(both.CheckAccessibility().Count == 0, "The actual capture contains no inaccessible state marks.");
                    }
        }
    }
}
