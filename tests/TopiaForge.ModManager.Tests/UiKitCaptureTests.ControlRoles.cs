using System;
using System.Linq;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void NativeControlsUseTheirResolvedRoles()
        {
            const string definition = """
                {"schema":1,"id":"control-roles","typography":{"label":{"font":"mono","trackingEm":0.10,"case":"upper"}},
                 "paper":{"colors":{"surface":{"fill":"#FFFFFF","on":"#000000"},
                    "sunken":{"fill":"#000000","on":"#FFFFFF"},"selected":{"fill":"#3E2525","on":"#FFFFFF"}}},
                 "hud":{"muted":"#FFFFFF","colors":{"sunken":{"fill":"#FFFFFF","on":"#000000"}}}}
                """;
            var descriptor = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(definition)).Value!;
            var label = descriptor.GetText(UiTextStyle.Label);
            var metrics = TopiaForgeTypographyMetrics.Resolve(label, 0.75f, 90f, 91.8f, -27f, 1f);
            var drawnPixels = metrics.PointSize / 90f * (91.8f + 27f) * 0.75f;
            Check(label.Font == "mono" && metrics.Uppercase && metrics.CharacterSpacing == 10f &&
                Math.Abs(drawnPixels - 18f) < 0.0001f, "An authored control label resolves Mono, uppercase and tracking at 0.75 UI scale.");

            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            Check(ui.AddThemeFixture(definition).Succeeded, "Admit custom light and dark Sunken pairs.");
            var rows = new[] { new UiListItem("ridge", "Ridge", "West"), new UiListItem("valley", "Valley", "South") };
            var view = new UiColumn(
                new UiButton("type", "Safety check", () => { }),
                new UiVirtualList("route-list", rows, _ => { }, selectedItemId: "ridge", visibleRows: 2),
                new UiVirtualList("closed-list", new[] { new UiListItem("closed", "Closed") }, _ => { },
                    selectedItemId: "closed", visibleRows: 1, enabled: false),
                new UiSlider("range", "Signal", 0f, 10f, 6f, _ => { }),
                new UiSlider("locked-range", "Locked", 0f, 10f, 6f, _ => { }, enabled: false),
                new UiGraphCanvas("graph", new[] { new UiGraphNode("camp", "Station", "Camp", new Vec2(0f, 0f),
                    Array.Empty<UiGraphPort>(), enabled: false) }, Array.Empty<UiGraphEdge>(), _ => { },
                    selectedNodeId: "camp", height: 160f));
            using var surface = Make(ui, new UiSurfaceRequest("control-roles", "Route", "", width: 680, height: 520,
                content: view).WithTheme(new UiThemeRef("control-roles")));
            ui.CompletePendingThemeRegistrations();
            var paper = surface.Capture(Viewport);
            Check(paper.Nodes.Single(n => n.Id == "type") is { Text: "SAFETY CHECK", SemanticLabel: "Safety check" },
                "Styled labels keep their original accessible text.");
            var listHits = paper.Nodes.Where(n => n.Kind == "HitTarget" && (n.Path.Contains(":ridge/") || n.Path.Contains(":valley/")))
                .OrderBy(n => n.Y).ToArray();
            Check(listHits.Length == 2 && listHits.All(n => n.Height == 40f) && listHits[1].Y - listHits[0].Y == 52f,
                "Two virtual rows expose 40-unit hit targets with room for both focus rings.");
            Check(paper.Nodes.Single(n => n.Kind == "ListItem" && n.Id == "valley") is
            { FillColor: "#000000", InkColor: "#FFFFFF" } &&
                paper.Nodes.Single(n => n.Kind == "ListItem" && n.Id == "closed") is
                { FillColor: "#000000", ContrastRatio: >= 4.5f } &&
                paper.Nodes.Single(n => n.Kind == "GraphNode" && n.Id == "camp") is
                { FillColor: "#000000", ContrastRatio: >= 4.5f },
                "Rows and disabled selected graph nodes resolve ink against their actual Sunken fill.");

            var hud = CaptureForFixture(surface, Viewport, "Hovered", 0f, true);
            var thumbs = hud.Nodes.Where(n => n.Kind == "SliderThumb").ToArray();
            Check(thumbs.Length == 2 && thumbs[0].ContrastRatio >= 3f &&
                thumbs[1] is { FillColor: "#FFFFFF", ContrastRatio: >= 3f },
                "Both slider thumbs remain visible over a valid white HUD track, including when disabled.");
        }
    }
}
