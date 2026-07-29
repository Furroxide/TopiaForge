using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    internal static class GraphPage
    {
        public static void Build(TopiaForgeContainer parent)
        {
            parent.SectionHeader("BOUNDED GRAPH CANVAS");
            parent.Label(
                    "Drag nodes, drag the empty canvas to pan, use the wheel to zoom, click an output then a compatible input to connect, and click an edge to remove it.",
                    TopiaForgeTextStyle.Body)
                .Tone(TopiaForgeTone.Muted);

            var graph = parent.GraphCanvas(CreateSample(parent)).FixedHeight(440f);
            var actions = parent.Row(TopiaForgeGap.Sm);
            actions.Button("LOAD SAMPLE", () => graph.SetGraph(CreateSample(parent)), TopiaForgeButtonStyle.Outline);
            actions.Button("EMPTY STATE", () => graph.SetGraph(CreateEmpty(parent)), TopiaForgeButtonStyle.Ghost);
            actions.Button("RESET VIEW", () => graph.SetViewport(UiGraphViewport.Default), TopiaForgeButtonStyle.Ghost);

            parent.Label(
                    "States shown: selected neutral, success, warning, disabled danger, empty, typed ports, required ports, and semantic edges.",
                    TopiaForgeTextStyle.Caption)
                .Tone(TopiaForgeTone.Muted);
        }

        private static UiGraphCanvas CreateSample(TopiaForgeContainer parent)
        {
            var nodes = new[]
            {
                new UiGraphNode(
                    "trigger",
                    "event",
                    "F5 pressed",
                    new Vec2(-250f, 90f),
                    new[] { new UiGraphPort("flow", "Event", UiGraphPortDirection.Output) },
                    "Neutral / selected"),
                new UiGraphNode(
                    "spawn",
                    "action",
                    "Spawn robot",
                    new Vec2(20f, 90f),
                    new[]
                    {
                        new UiGraphPort("flow-in", "Run", UiGraphPortDirection.Input, required: true),
                        new UiGraphPort("robot", "Robot", UiGraphPortDirection.Output, "entity")
                    },
                    "Ready",
                    UiTone.Success),
                new UiGraphNode(
                    "personality",
                    "edit",
                    "Set personality",
                    new Vec2(290f, 90f),
                    new[]
                    {
                        new UiGraphPort("target", "Robot", UiGraphPortDirection.Input, "entity", required: true),
                        new UiGraphPort("result", "Changed", UiGraphPortDirection.Output)
                    },
                    "Missing preset",
                    UiTone.Warning),
                new UiGraphNode(
                    "publish",
                    "event",
                    "Publish scene",
                    new Vec2(290f, -130f),
                    new[] { new UiGraphPort("flow", "Run", UiGraphPortDirection.Input) },
                    "Unavailable offline",
                    UiTone.Danger,
                    enabled: false)
            };
            var edges = new[]
            {
                new UiGraphEdge("trigger-spawn", "trigger", "flow", "spawn", "flow-in", UiTone.Success),
                new UiGraphEdge("spawn-personality", "spawn", "robot", "personality", "target", UiTone.Warning),
                new UiGraphEdge("personality-publish", "personality", "result", "publish", "flow")
            };
            return new UiGraphCanvas(
                "gallery-graph",
                nodes,
                edges,
                selected => parent.Host.Toast(
                    selected == null ? "Graph selection cleared" : "Selected " + selected,
                    TopiaForgeTone.Neutral),
                selectedNodeId: "trigger",
                height: 440f,
                nodeMoved: move => parent.Host.Toast("Moved " + move.NodeId, TopiaForgeTone.Neutral),
                connectionRequested: _ => parent.Host.Toast("Connection requested", TopiaForgeTone.Success),
                connectionRemoved: edge => parent.Host.Toast("Remove " + edge, TopiaForgeTone.Warning));
        }

        private static UiGraphCanvas CreateEmpty(TopiaForgeContainer parent)
        {
            return new UiGraphCanvas(
                "gallery-graph-empty",
                System.Array.Empty<UiGraphNode>(),
                System.Array.Empty<UiGraphEdge>(),
                _ => parent.Host.Toast("Graph selection cleared"),
                height: 440f);
        }
    }
}
