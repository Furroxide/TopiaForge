using System;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeGraphCanvas
    {
        internal TopiaForgeGraphCanvasBindingState CaptureBindingState()
        {
            var positions = graph.Nodes.Count == 0 ? Array.Empty<Vector2>() : new Vector2[graph.Nodes.Count];
            for (var index = 0; index < positions.Length; index++) positions[index] = nodeViews[index].GraphPosition;
            return new TopiaForgeGraphCanvasBindingState(
                graph,
                viewport,
                selectedNodeId,
                pendingSourceNodeId,
                pendingSourcePortId,
                positions,
                enabledState);
        }

        internal void RestoreBindingState(TopiaForgeGraphCanvasBindingState state)
        {
            SetGraph(state.Graph);
            viewport = state.Viewport;
            selectedNodeId = state.SelectedNodeId;
            pendingSourceNodeId = state.PendingSourceNodeId;
            pendingSourcePortId = state.PendingSourcePortId;
            enabledState = state.Enabled;
            for (var index = 0; index < state.Positions.Length; index++) nodeViews[index].SetGraphPosition(state.Positions[index]);
            RenderGeometry();
            ApplyTheme(Theme);
        }
    }

    internal readonly struct TopiaForgeGraphCanvasBindingState
    {
        public TopiaForgeGraphCanvasBindingState(
            TopiaForge.Mods.UiGraphCanvas graph,
            TopiaForge.Mods.UiGraphViewport viewport,
            string? selectedNodeId,
            string? pendingSourceNodeId,
            string? pendingSourcePortId,
            Vector2[] positions,
            bool enabled)
        {
            Graph = graph;
            Viewport = viewport;
            SelectedNodeId = selectedNodeId;
            PendingSourceNodeId = pendingSourceNodeId;
            PendingSourcePortId = pendingSourcePortId;
            Positions = positions;
            Enabled = enabled;
        }

        public TopiaForge.Mods.UiGraphCanvas Graph { get; }
        public TopiaForge.Mods.UiGraphViewport Viewport { get; }
        public string? SelectedNodeId { get; }
        public string? PendingSourceNodeId { get; }
        public string? PendingSourcePortId { get; }
        public Vector2[] Positions { get; }
        public bool Enabled { get; }
    }
}
