namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeGraphCanvas
    {
        internal TopiaForgeGraphCanvasBindingState CaptureBindingState()
        {
            return new TopiaForgeGraphCanvasBindingState(
                graph,
                viewport,
                selectedNodeId,
                pendingSourceNodeId,
                pendingSourcePortId);
        }

        internal void RestoreBindingState(TopiaForgeGraphCanvasBindingState state)
        {
            SetGraph(state.Graph);
            viewport = state.Viewport;
            selectedNodeId = state.SelectedNodeId;
            pendingSourceNodeId = state.PendingSourceNodeId;
            pendingSourcePortId = state.PendingSourcePortId;
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
            string? pendingSourcePortId)
        {
            Graph = graph;
            Viewport = viewport;
            SelectedNodeId = selectedNodeId;
            PendingSourceNodeId = pendingSourceNodeId;
            PendingSourcePortId = pendingSourcePortId;
        }

        public TopiaForge.Mods.UiGraphCanvas Graph { get; }
        public TopiaForge.Mods.UiGraphViewport Viewport { get; }
        public string? SelectedNodeId { get; }
        public string? PendingSourceNodeId { get; }
        public string? PendingSourcePortId { get; }
    }
}
