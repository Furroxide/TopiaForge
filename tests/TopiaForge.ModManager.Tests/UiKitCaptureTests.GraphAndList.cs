using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static void GraphPortsHaveIndependentTargetsAndPaint()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            const string definition = """
                {"schema":1,"id":"graph-paint","typography":{"label":{"font":"mono","trackingEm":0.10,"case":"upper"}},
                 "paper":{"muted":"#FFFFFF","colors":{"surface":{"fill":"#FFFFFF","on":"#000000"},
                    "raised":{"fill":"#224455","on":"#FFFFFF"},"sunken":{"fill":"#0A1621","on":"#FFFFFF"},
                    "selected":{"fill":"#592553","on":"#FFFFFF"}}}}
                """;
            Check(ui.AddThemeFixture(definition).Succeeded, "The graph fixture declares distinct backing colors for all three roles.");
            var fixture = FoundationPages.CreateRequest(FoundationPage.InteractionStates, UiSurfaceKind.Window);
            var authored = (UiGraphCanvas)((UiColumn)fixture.Content!).Children[^1];
            var nodes = authored.Nodes.ToArray();
            var camp = nodes[0];
            nodes[0] = new UiGraphNode(camp.Id, camp.Type, camp.Title, camp.Position, camp.Ports.Concat(new[]
            {
                new UiGraphPort("in-back", "Back", UiGraphPortDirection.Input),
            }));
            var graph = new UiGraphCanvas(authored.Id!, nodes, authored.Edges, authored.SelectionChanged, authored.SelectedNodeId,
                height: 220f);
            using var surface = Make(ui, new UiSurfaceRequest("graph-paint", "Trail editor", "", width: 840, height: 320,
                content: graph).WithTheme(new UiThemeRef("graph-paint")));
            ui.CompletePendingThemeRegistrations();
            var rest = surface.Capture(Viewport);
            var ports = rest.Nodes.Where(n => n.Kind == "GraphPort").ToArray();
            var targets = rest.Nodes.Where(n => n.Kind == "HitTarget").ToArray();
            Check(ports.Length == 5 && targets.Length == 8 && targets.All(n => n.IsRendered && !n.Clipped &&
                n.Width >= 40f && n.Height >= 40f), "Each graph header and port exposes a separate stationary hit target.");
            for (var index = 0; index < targets.Length; index++)
                for (var other = index + 1; other < targets.Length; other++)
                    Check(!Overlaps(targets[index], targets[other]), "Graph fixture hit regions are disjoint.");

            Check(ports[0] is { Text: "RUN *", SemanticLabel: "Run *", FillColor: "#592553", TextHeightPixels: 18f } &&
                ports[3] is { FillColor: "#224455" } && ports[4] is { FillColor: "#0A1621", Disabled: true },
                "Graph port typography and labels use the selected, raised and disabled Sunken schemes.");
            var marks = rest.Nodes.Where(n => n.Kind == "GraphPortMark").ToArray();
            Check(marks.Length == ports.Length && marks.Zip(ports).All(pair =>
                pair.First.FillColor == pair.Second.FillColor && pair.First.InkColor == pair.Second.InkColor),
                "Port marks read against the same actual fill as the label.");
            foreach (var state in new[] { "Hovered", "Selected", "Pressed" })
            {
                var capture = CaptureForFixture(surface, Viewport, state, 0f, null);
                var port = capture.Nodes.First(n => n.Kind == "GraphPort");
                Check(port.FillColor != ports[0].FillColor && port.ContrastRatio >= 4.5f &&
                    capture.Nodes.First(n => n.Kind == "GraphPortMark").FillColor == port.FillColor,
                    "Each graph port interaction tint keeps its actual label and mark readable.");
            }

            foreach (var scale in new[] { 1f, 2f })
            {
                Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(true, scale)).Succeeded,
                    "Apply the viewer's high-contrast scale.");
                var focused = CaptureForFixture(surface, Viewport, "Focused", 0f, null);
                var ring = focused.Nodes.Single(n => n.Kind == "FocusRing");
                Check(ring.Path.Contains("/ports/", StringComparison.Ordinal) && ring.IsRendered && !ring.Clipped &&
                    ring.BorderWidthPixels == 3f * scale && ring.ContrastRatio >= 4.5f &&
                    focused.Nodes.Where(n => n.Kind == "HitTarget" && n.Path != ring.Path[..^6] + "/hit")
                        .All(n => !Overlaps(ring, n)), "Port focus paints inside its reserved clear space at both scales.");
                Check(focused.CheckAccessibility().Count == 0, "The focused graph passes captured accessibility checks.");
            }
        }

        private static void ListSelectionRevealsPaddedRows()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var items = new[] { new UiListItem("a", "A"), new UiListItem("b", "B"),
                new UiListItem("c", "C"), new UiListItem("d", "D") };
            var disabled = new UiVirtualList("list-disabled", items, _ => { }, selectedItemId: "b", visibleRows: 1, enabled: false);
            using var disabledSurface = Make(ui, new UiSurfaceRequest("list-disabled", "Route", "", content: disabled));
            Check(disabledSurface.Capture(Viewport).Nodes.Single(n => n.Kind == "ListItem") is { Id: "b", IsRendered: true },
                "A one-row disabled list reveals a later authored selection.");

            var events = new System.Collections.Generic.List<string>();
            var enabled = new UiVirtualList("list-live", items, events.Add, selectedItemId: "d", visibleRows: 2);
            using var surface = Make(ui, new UiSurfaceRequest("list-live", "Route", "", width: 350, height: 220,
                content: enabled));
            Check(VisibleRows(surface.Capture(Viewport)).SequenceEqual(new[] { "c", "d" }),
                "The viewport scrolls the least distance needed to reveal the later selected row.");
            Check(surface.SelectListItem("list-live", "c").Succeeded && VisibleRows(surface.Capture(Viewport)).SequenceEqual(new[] { "c", "d" }),
                "Selecting an already visible row keeps the same first visible row.");
            Check(surface.SelectListItem("list-live", "a").Succeeded && VisibleRows(surface.Capture(Viewport)).SequenceEqual(new[] { "a", "b" }) &&
                events.SequenceEqual(new[] { "c", "a" }), "A new selection above the viewport reveals it and still invokes the callback.");

            foreach (var scale in new[] { 1f, 2f })
            {
                Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(true, scale)).Succeeded, "Apply the high-contrast list scale.");
                foreach (var item in new[] { "a", "b" })
                {
                    Check(surface.SelectListItem("list-live", item).Succeeded, "Focus a row at the viewport boundary.");
                    var capture = CaptureForFixture(surface, Viewport, "Focused", 0f, null);
                    var list = capture.Nodes.Single(n => n.Kind == nameof(UiVirtualList));
                    var ring = capture.Nodes.Single(n => n.Kind == "FocusRing");
                    Check(ring.IsRendered && !ring.Clipped && ring.BorderWidthPixels == 3f * scale &&
                        ring.X > list.X && ring.Y > list.Y && ring.X + ring.Width < list.X + list.Width &&
                        ring.Y + ring.Height < list.Y + list.Height, "Both viewport edges reserve the full physical focus ring.");
                    Check(capture.CheckAccessibility().Count == 0, "Fully visible row focus passes capture checks.");
                }
            }

            Check(surface.SelectListItem("list-live", "a").Succeeded, "Return to the first row before applying the outer viewport clip.");
            var clipped = CaptureForFixture(surface, Viewport, "Focused", 3f, null);
            var clippedRing = clipped.Nodes.Single(n => n.Kind == "FocusRing");
            Check(clippedRing is { IsRendered: true, Clipped: true } &&
                clipped.CheckAccessibility().Any(issue => issue.Kind == UiAccessibilityIssueKind.ClippedContent &&
                    issue.Path == clippedRing.Path), "A captured focus ring clipped by a real viewport boundary fails accessibility.");

            static string?[] VisibleRows(UiTreeCapture capture) =>
                capture.Nodes.Where(n => n.Kind == "ListItem" && n.IsRendered).Select(n => n.Id).ToArray();
        }

        private static bool Overlaps(UiCapturedNode first, UiCapturedNode second) =>
            first.X < second.X + second.Width && first.X + first.Width > second.X &&
            first.Y < second.Y + second.Height && first.Y + first.Height > second.Y;
    }
}
