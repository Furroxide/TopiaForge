using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "AdvanceGraphConnectionLabel")]
        private static extern bool AdvanceGraphConnectionLabel(FakeUiSurface surface, UiCaptureViewport viewport);

        private static void GraphConnectionLabelsRevealBothEndpoints()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            const string shared = "Common maintenance distribution switch in the eastern network";
            const string output = "Distribution handoff output";
            var removed = new List<string>();
            foreach (var scale in new[] { 1f, 2f })
            {
                Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: scale)).Succeeded, "Set the compact graph scale.");
                using var surface = Make(ui, new UiSurfaceRequest("compact-labels", "Connections", "", width: 240, height: 272,
                    content: ConnectionLabelGraph(shared, output, "Alpha station", "Beta station", "Input", removed.Add)));
                Check(OpenGraphConnections(surface, Viewport), "Open the measured 240 by 272 connection list.");
                var first = surface.Capture(Viewport);
                var rows = first.Nodes.Where(node => node.Kind == "GraphConnectionRow").ToArray();
                Check(rows.Select(row => row.Id).SequenceEqual(new[] { "alpha", "beta" }) &&
                    rows[0].Text.Contains("Alpha station", StringComparison.Ordinal) &&
                    rows[1].Text.Contains("Beta station", StringComparison.Ordinal),
                    "Compact rows with a common long source visibly identify their different targets.");
                Check(rows[0].Text != rows[0].SemanticLabel && rows[0].IsRendered && !rows[0].Clipped &&
                    rows[0].SemanticLabel == shared + " / " + output + " to Alpha station / Input",
                    "The captured preview shows actual display text and retains the full edge name.");
                Check(first.Nodes.Single(node => node.Kind == "GraphConnectionRemove").SemanticLabel ==
                    "Remove connection from " + rows[0].SemanticLabel, "The Remove action keeps the full selected edge name.");
                CheckDisjoint(first, 40f * scale);
                Check(AdvanceGraphConnectionLabel(surface, Viewport), "Activate the selected row to reveal its next page.");
                var next = surface.Capture(Viewport);
                Check(next.Nodes.Single(node => node.Kind == "GraphConnectionRow" && node.Selected).Text != rows[0].Text,
                    "The next page displays additional endpoint text.");
                CheckDisjoint(next, 40f * scale);
                Check(MoveGraphConnection(surface, 1), "Move Down to the next authored edge after revealing text.");
                Check(surface.Capture(Viewport).Nodes.Single(node => node.Kind == "GraphConnectionRow" && node.Selected).Text == rows[1].Text,
                    "A newly selected edge opens to its own first page.");
                Check(RemoveGraphConnectionSelection(surface).Succeeded && removed.Last() == "beta",
                    "The compact Remove action selects the edge shown by its full label.");
            }

            var sourceName = new string('W', 255) + "A";
            var sourcePort = new string('M', 255) + "B";
            var targetName = new string('X', 255) + "C";
            var targetPort = new string('Y', 255) + "D";
            using var maximal = Make(ui, new UiSurfaceRequest("max-labels", "Connections", "", width: 240, height: 272,
                content: ConnectionLabelGraph(sourceName, sourcePort, targetName, "Other station", targetPort, removed.Add)));
            Check(OpenGraphConnections(maximal, Viewport), "An edge with four maximum-length fields still has an accessible list.");
            var firstPage = maximal.Capture(Viewport).Nodes.Single(node => node.Kind == "GraphConnectionRow" && node.Selected);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            for (; index < 128; index++)
            {
                var capture = maximal.Capture(Viewport);
                var selected = capture.Nodes.Single(node => node.Kind == "GraphConnectionRow" && node.Selected);
                Check(selected.SemanticLabel == firstPage.SemanticLabel && selected.IsRendered && !selected.Clipped,
                    "Every maximal page remains readable with the same edge identity.");
                CheckDisjoint(capture, 80f);
                if (index != 0 && selected.Text == firstPage.Text && seen.Any(value => value.Contains("D", StringComparison.Ordinal)))
                    break;
                seen.Add(selected.Text);
                Check(AdvanceGraphConnectionLabel(maximal, Viewport), "Each maximal edge page can advance.");
            }
            foreach (var suffix in new[] { "A", "B", "C", "D" })
                Check(seen.Any(page => page.Contains(suffix, StringComparison.Ordinal)), "The full endpoint suffix " + suffix + " is reachable.");
            Check(index < 128, "The maximal endpoint reveal wraps back to the first page.");

            const string scalar = "\U0001F9CA";
            var wideCluster = string.Join("\u200D", Enumerable.Repeat(scalar, 75));
            var labels = new UiGraphConnectionLabels(wideCluster, "Station / Input");
            var pages = UiGraphConnectionLabelPages.Measure(labels, labels.Full, new UiLayoutRect(0, 0, 108, 40),
                text => (text.Split('\n').Max(line => line.Length) * 6d, text.Split('\n').Length * 18d));
            Check(pages.Count > 1 && Enumerable.Range(0, pages.Count).All(page =>
                pages.Display(page).Split('\n').All(UiPlainText.IsSingleLine)) &&
                Enumerable.Range(0, pages.Count).Count(page => pages.Display(page).Contains(scalar, StringComparison.Ordinal)) > 1,
                "If the font cannot fit a joined glyph sequence, the pager reveals its scalars without splitting a surrogate pair.");
        }

        private static UiGraphCanvas ConnectionLabelGraph(string source, string output, string alpha, string beta, string input,
            Action<string> remove)
        {
            return new UiGraphCanvas("connection-labels", new[]
            {
                new UiGraphNode("source", "switch", source, new Vec2(-75, 0),
                    new[] { new UiGraphPort("out", output, UiGraphPortDirection.Output) }),
                new UiGraphNode("target-a", "station", alpha, new Vec2(75, 0),
                    new[] { new UiGraphPort("in", input, UiGraphPortDirection.Input) }),
                new UiGraphNode("target-b", "station", beta, new Vec2(75, 100),
                    new[] { new UiGraphPort("in", input, UiGraphPortDirection.Input) }),
            }, new[]
            {
                new UiGraphEdge("alpha", "source", "out", "target-a", "in"),
                new UiGraphEdge("beta", "source", "out", "target-b", "in"),
            }, _ => { }, height: 176f, connectionRemoved: remove);
        }
    }
}
