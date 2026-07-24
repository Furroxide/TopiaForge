using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        private static void TestGraphUiComposition()
        {
            using var context = new FakeModContext();
            Assert(UiGraphCanvas.MaximumNodes == 512 && UiGraphCanvas.MaximumEdges == 1024 &&
                   UiGraphCanvas.MaximumPortsPerNode == 32,
                "graph resource ceilings match the bounded creator-tool contract");
            string? selected = null;
            UiGraphNodeMove moved = default;
            UiGraphConnectionRequest connected = default;
            string? removed = null;
            var changedViewport = UiGraphViewport.Default;

            var source = new UiGraphNode(
                "catalog",
                "source",
                "Robot catalog",
                new Vec2(-180f, 40f),
                new[] { new UiGraphPort("robot", "Robot", UiGraphPortDirection.Output, "robot") },
                "Ready",
                UiTone.Success);
            var spawn = new UiGraphNode(
                "spawn",
                "action",
                "Spawn robot",
                new Vec2(100f, 40f),
                new[]
                {
                    new UiGraphPort("robot", "Robot", UiGraphPortDirection.Input, "robot", required: true),
                    new UiGraphPort("result", "Spawned", UiGraphPortDirection.Output, "entity")
                },
                "Waiting for placement",
                UiTone.Warning);
            var inspect = new UiGraphNode(
                "inspect",
                "tool",
                "Edit entity",
                new Vec2(360f, -80f),
                new[] { new UiGraphPort("target", "Target", UiGraphPortDirection.Input, "entity") },
                "Disabled state",
                UiTone.Danger,
                enabled: false);
            var edges = new[]
            {
                new UiGraphEdge("catalog-spawn", "catalog", "robot", "spawn", "robot", UiTone.Success),
                new UiGraphEdge("spawn-inspect", "spawn", "result", "inspect", "target", UiTone.Warning)
            };
            var graph = new UiGraphCanvas(
                "creator-graph",
                new[] { source, spawn, inspect },
                edges,
                value => selected = value,
                selectedNodeId: "spawn",
                viewport: new UiGraphViewport(new Vec2(12f, -8f), 1.1f),
                height: 520f,
                nodeMoved: value => moved = value,
                connectionRequested: value => connected = value,
                connectionRemoved: value => removed = value,
                viewportChanged: value => changedViewport = value);
            var composition = new UiSplitPane(
                graph,
                new UiColumn(
                    new UiText("Inspector", UiTextStyle.Heading),
                    new UiText("Select a graph node to edit it."),
                    new UiButton("apply-graph", "Apply", () => { })),
                UiSplitOrientation.Horizontal,
                0.7f);
            var creation = context.Ui.CreateSurface(new UiSurfaceRequest(
                "creator-workbench",
                "Creator Workbench",
                "Build and test an event graph.",
                UiSurfaceKind.FullscreenTool,
                content: composition));
            Assert(creation.TryGetValue(out var created) && created is FakeUiSurface,
                "fullscreen safe surfaces are captured by the testing kit");
            var surface = (FakeUiSurface)created!;
            Assert(surface.Request.Kind == UiSurfaceKind.FullscreenTool && surface.TryFindNode("creator-graph", out _),
                "split panes participate in graph lookup and composition validation");
            Assert(surface.TryGetSelectedGraphNode("creator-graph", out var initialSelection) && initialSelection == "spawn" &&
                   surface.TryGetGraphViewport("creator-graph", out var initialViewport) && initialViewport.Zoom == 1.1f,
                "the fake retains initial graph selection and viewport state");

            Assert(surface.SelectGraphNode("creator-graph", "catalog").Succeeded && selected == "catalog" &&
                   surface.TryGetSelectedGraphNode("creator-graph", out var capturedSelection) && capturedSelection == "catalog",
                "graph selection uses stable node ids");
            Assert(surface.MoveGraphNode("creator-graph", "spawn", new Vec2(140f, 60f)).Succeeded &&
                   moved.NodeId == "spawn" && moved.Position == new Vec2(140f, 60f),
                "graph node moves deliver bounded engine-free coordinates");
            Assert(surface.ConnectGraphPorts("creator-graph", "catalog", "robot", "spawn", "robot").Succeeded &&
                   connected.SourceNodeId == "catalog" && connected.TargetNodeId == "spawn",
                "graph connection requests enforce port direction and data compatibility");
            Assert(surface.RemoveGraphConnection("creator-graph", "spawn-inspect").Succeeded && removed == "spawn-inspect",
                "graph edge removal delivers the stable edge id");
            var nextViewport = new UiGraphViewport(new Vec2(80f, -32f), 1.5f);
            Assert(surface.ChangeGraphViewport("creator-graph", nextViewport).Succeeded &&
                   changedViewport == nextViewport &&
                   surface.TryGetGraphViewport("creator-graph", out var capturedViewport) && capturedViewport == nextViewport,
                "graph viewport callbacks preserve bounded pan and zoom state");
            Assert(surface.MoveGraphNode("creator-graph", "inspect", Vec2.Zero).ErrorCode == ModErrorCode.InvalidState &&
                   surface.ConnectGraphPorts("creator-graph", "spawn", "result", "catalog", "robot").ErrorCode ==
                       ModErrorCode.InvalidArgument,
                "fake graph editing rejects disabled nodes and incompatible connection directions");

            var dismissalCalls = 0;
            var dismissal = (IUiSurfaceDismissalSource)surface;
            dismissal.Dismissed += () => throw new InvalidOperationException("expected dismissal failure");
            dismissal.Dismissed += () => dismissalCalls++;
            surface.Hide();
            surface.Hide();
            Assert(dismissalCalls == 1 && surface.CallbackErrors.Count == 1,
                "optional dismissal notifications fire once per visible transition and isolate subscribers");
            surface.Show();
            surface.Hide();
            Assert(dismissalCalls == 2, "a shown fullscreen surface can report a later dismissal");

            AssertThrows<ArgumentException>(() => new UiGraphCanvas(
                    "bad-edge",
                    new[] { source, spawn },
                    new[] { new UiGraphEdge("wrong", "spawn", "robot", "catalog", "robot") },
                    _ => { }),
                "graph validation rejects edges that do not run from output to input");
            AssertThrows<ArgumentOutOfRangeException>(() => new UiGraphViewport(Vec2.Zero, 3f),
                "graph viewport zoom is bounded before rendering");
            AssertThrows<ArgumentOutOfRangeException>(() => new UiSplitPane(graph, new UiText("Inspector"), primaryFraction: 0.95f),
                "split-pane proportions retain usable space for both panes");
            AssertThrows<ArgumentException>(() => new UiSurfaceRequest(
                    "graph-hud",
                    "Graph HUD",
                    string.Empty,
                    UiSurfaceKind.Hud,
                    content: graph),
                "interactive graph canvases cannot be placed in presentation-only HUD surfaces");

            TestKeyedGraphRetentionPolicy();
        }

        private static void TestKeyedGraphRetentionPolicy()
        {
            var retainedGraph = new object();
            var staleGraph = new object();
            var previous = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["retained"] = retainedGraph,
                ["stale"] = staleGraph,
            };
            var retention = new KeyedRetentionMap<object>(previous);

            Assert(retention.TryClaimPrevious("retained", out var existing) &&
                   ReferenceEquals(existing, retainedGraph),
                "keyed retention finds the prior widget by stable graph id");
            retention.SetClaimed("retained", retainedGraph);

            Assert(!retention.TryClaimPrevious("created", out _),
                "a new graph id does not consume an unrelated retained widget");
            var createdGraph = new object();
            retention.SetClaimed("created", createdGraph);

            var staleCount = 0;
            var staleId = string.Empty;
            foreach (var id in retention.StaleKeys)
            {
                staleCount++;
                staleId = id;
            }

            Assert(retention.Next.Count == 2 &&
                   ReferenceEquals(retention.Next["retained"], retainedGraph) &&
                   ReferenceEquals(retention.Next["created"], createdGraph) &&
                   staleCount == 1 && staleId == "stale" && previous.Count == 2,
                "the next retained snapshot keeps reused/new widgets and leaves stale cleanup to commit");
            AssertThrows<InvalidOperationException>(() => retention.TryClaimPrevious("retained", out _),
                "one declarative refresh cannot claim the same graph id twice");
            AssertThrows<InvalidOperationException>(() => retention.SetClaimed("unclaimed", new object()),
                "a widget cannot enter the retained snapshot without a keyed claim");
        }
    }
}
