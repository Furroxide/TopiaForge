using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OpenGraphConnections")]
        private static extern bool OpenGraphConnections(FakeUiSurface surface, UiCaptureViewport viewport);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CloseGraphConnections")]
        private static extern void CloseGraphConnections(FakeUiSurface surface);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "MoveGraphConnection")]
        private static extern bool MoveGraphConnection(FakeUiSurface surface, int step);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "RemoveGraphConnectionSelection")]
        private static extern OperationResult<bool> RemoveGraphConnectionSelection(FakeUiSurface surface);

        private static void GraphConnectionsPaintIndependentCues()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            const string palette = """
                {"schema":1,"id":"graph-cues","paper":{"colors":{
                  "surface":{"fill":"#000000","on":"#FFFFFF"},"raised":{"fill":"#000000","on":"#FFFFFF"},
                  "selected":{"fill":"#000000","on":"#FFFFFF"}}}}
                """;
            Check(ui.AddThemeFixture(palette).Succeeded, "Admit collapsed graph connection colors.");
            var request = FoundationPages.CreateRequest(FoundationPage.GraphConnections, UiSurfaceKind.Window, new UiThemeRef("graph-cues"));
            using var surface = Make(ui, request);
            ui.CompletePendingThemeRegistrations();
            foreach (var scale in new[] { 1f, 2f })
            {
                Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: scale)).Succeeded, "Set the viewer scale.");
                CloseGraphConnections(surface);
                Check(surface.SelectGraphNode("relay-map", "west-high").Succeeded, "Focus a graph before pressing Connections.");
                var launcher = CaptureForFixture(surface, Viewport, "Pressed", 0, false);
                Check(launcher.Nodes[0].ThemeId == "graph-cues" && HasMark(launcher, "/graph-connections/press"),
                    "The closed launcher has a visible pressed outline in the admitted collapsed palette. " +
                    string.Join(", ", launcher.Nodes.Where(node => node.Path.StartsWith("/graph-connections", StringComparison.Ordinal))
                        .Select(node => $"{node.Path} fill {node.FillColor} visible {node.IsRendered} clipped {node.Clipped} disabled {node.Disabled}")));
                Check(OpenGraphConnections(surface, Viewport), "Open the graph list.");
                var rest = CaptureForFixture(surface, Viewport, "Rest", 0, false);
                var active = rest.Nodes.Single(node => node.Kind == "GraphConnectionRow" && node.Selected);
                Check(HasMark(rest, active.Path + "/selection") && HasMark(rest, "/graph-connections/selection"),
                    "The open action and selected row each retain their semantic cue at rest.");
                var remove = CaptureForFixture(surface, Viewport, "Pressed", 0, false);
                Check(HasMark(remove, "/0/remove/press") && HasMark(remove, active.Path + "/selection") &&
                    !HasMark(remove, active.Path + "/press"), "Remove has independent press paint while the selected row remains marked.");
                var selectedPressed = CaptureForFixture(surface, Viewport, "SelectedPressed", 0, false);
                Check(HasMark(selectedPressed, active.Path + "/selection") && HasMark(selectedPressed, active.Path + "/press") &&
                    selectedPressed.CheckAccessibility().Count == 0, "A selected pressed row has both readable, non-overlapping marks.");
            }

            static bool HasMark(UiTreeCapture tree, string path) => tree.Nodes.Any(node => node.Path == path && node.IsRendered && !node.Clipped);
        }

        private static void GraphConnectionsAreLocalPooledAndLabelled()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var events = new List<string>();
            var request = FoundationPages.CreateRequest(FoundationPage.GraphConnections, UiSurfaceKind.Window, report: events.Add);
            using var surface = Make(ui, request);
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(true, 2f, true)).Succeeded,
                "The local graph fixture takes viewer high contrast and scale 2.0.");
            var canvas = surface.Capture(Viewport);
            Check(canvas.Nodes.Count(node => node.Kind == "GraphConnectionsAction") == 1 &&
                canvas.Nodes.Count(node => node.Kind == nameof(UiGraphCanvas)) == 2,
                "Two graph canvases get one owning-surface Connections launcher.");
            Check(surface.SelectGraphNode("relay-map", "west-high").Succeeded && OpenGraphConnections(surface, Viewport),
                "The action opens the focused graph.");
            var first = CaptureForFixture(surface, Viewport, "Focused", 0, null);
            Check(first.Nodes.Where(node => node.Kind == "GraphConnectionRow").Select(node => node.Id)
                .SequenceEqual(new[] { "lower-relay", "upper-relay" }), "Crossing edges appear once in authored order.");
            Check(first.Nodes.Single(node => node.Kind == "GraphConnectionRemove").SemanticLabel ==
                "Remove connection from West ridge / Send to East relay / Run",
                "The distinct Remove target names both endpoints of the selected edge.");
            CheckDisjoint(first, 80f);

            Check(MoveGraphConnection(surface, 1), "Arrow navigation selects the next authored edge.");
            Check(RemoveGraphConnectionSelection(surface).Succeeded &&
                events.Last() == "Remove requested for upper-relay.",
                "The measured list delivers the selected removal through the existing graph callback.");
            CloseGraphConnections(surface);
            Check(surface.SelectGraphNode("station-map", "station").Succeeded && OpenGraphConnections(surface, Viewport),
                "The same launcher follows focus into the other graph.");
            var second = surface.Capture(Viewport);
            Check(second.Nodes.Single(node => node.Kind == "GraphConnectionsList").Id == "station-map" &&
                second.Nodes.Single(node => node.Kind == "GraphConnectionRow").Id == "lookout-relay",
                "A surface has one active list with rows from just its focused graph.");
            CheckDisjoint(second, 80f);

            const int maximum = UiGraphCanvas.MaximumEdges;
            var edges = Enumerable.Range(0, maximum).Select(index =>
                new UiGraphEdge("edge-" + (maximum - index), "station", "send", "lookout", "run")).ToList();
            var station = (UiGraphCanvas)((UiColumn)request.Content!).Children[3];
            using var bounded = Make(ui, new UiSurfaceRequest("large-connection-list", "Map", "",
                UiSurfaceKind.FullscreenTool, content: Graph()));
            var large = new UiCaptureViewport(2560, 1440);
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(true, 1f, true)).Succeeded &&
                OpenGraphConnections(bounded, large), "The list opens over the full-screen tool with 1,024 edges.");
            Check(bounded.Capture(large).Nodes.Count(node => node.Kind == "GraphConnectionRow") == 12,
                "The visible pool has the twelve-row ceiling even in a tall viewport.");
            for (var index = 1; index < maximum; index++)
                Check(MoveGraphConnection(bounded, 1), "Every authored edge is reachable with keyboard navigation.");
            var last = bounded.Capture(large);
            Check(last.Nodes.Count(node => node.Kind == "GraphConnectionRow") == 12 &&
                last.Nodes.Single(node => node.Kind == "GraphConnectionRow" && node.Selected).Id == "edge-1",
                "The row pool reveals the authored tail and preserves its selected identity.");
            Check(RemoveGraphConnectionSelection(bounded).Succeeded && bounded.SetContent(Graph()).Succeeded,
                "An accepted replacement from the existing removal callback shrinks the authored list.");
            var remaining = bounded.Capture(large);
            Check(remaining.Nodes.Single(node => node.Kind == "GraphConnectionRow" && node.Selected).Id == "edge-2",
                "Removing the last row moves selection to the nearest remaining edge.");
            CheckDisjoint(remaining, 40f * UiHudViewport.ScreenScale(2560, 1440));

            using var compact = Make(ui, new UiSurfaceRequest("compact-map", "Map", "", width: 240, height: 272,
                content: Graph()));
            Check(OpenGraphConnections(compact, Viewport), "A compact window measures a row and a separate Remove side by side.");
            var tiny = compact.Capture(Viewport);
            Check(tiny.Nodes.Single(node => node.Kind == "GraphConnectionRemove") is
                { Text: "Remove", SemanticLabel: "Remove connection from Station / Send to Lookout / Run" },
                "Compact paint retains the full semantic Remove label.");
            CheckDisjoint(tiny, 40f);

            var model = new UiGraphConnections();
            var authored = Graph();
            model.Bind(authored);
            model.Resize(12);
            var allocated = TopiaForge.TestSupport.AllocationProbe.Measure("settled graph connections", () =>
            {
                if (model.Bind(authored) || model.Resize(12) || model.Move(-1))
                    throw new InvalidOperationException("The settled graph selection changed.");
            }, 4096, 128);
            Check(allocated == 0, "The same graph selection and measured rows allocate 0 B at rest.");

            UiGraphCanvas Graph() => new UiGraphCanvas("large-graph", station.Nodes, edges, _ => { },
                height: 176f, connectionRemoved: id => edges.RemoveAll(edge => edge.Id == id));
        }

        private static void CheckDisjoint(UiTreeCapture capture, float minimum)
        {
            var targets = capture.Nodes.Where(node => node.Kind == "HitTarget").ToArray();
            Check(targets.All(node =>
                node.IsRendered && !node.Clipped && node.Width >= minimum - 0.001f && node.Height >= minimum - 0.001f),
                "Connections rows and Remove are fully visible with the reference target floor.");
            for (var index = 0; index < targets.Length; index++)
            for (var later = index + 1; later < targets.Length; later++)
                Check(!Overlaps(targets[index], targets[later]), "Connections uses disjoint measurable action boxes.");
            Check(capture.CheckAccessibility().Count == 0, "The Connections capture passes its accessibility checks.");
        }

    }
}
