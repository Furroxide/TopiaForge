namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiSurface
    {
        private UiGraphConnections? graphConnections;
        private string? focusedConnectionGraphId;
        internal UiGraphConnections? GraphConnections => graphConnections;

        internal UiGraphCanvas? FirstGraphWithConnections =>
            WireBounded || !IsVisible || Request.Kind == UiSurfaceKind.Hud ? null : FirstRemovableGraph(Content);

        internal UiGraphCanvas? GraphForConnections =>
            focusedConnectionGraphId == null ? FirstGraphWithConnections :
            UiLayoutTree.TryFind(Content, focusedConnectionGraphId, out var node, out var disabled) &&
            node is UiGraphCanvas graph && !disabled ? graph : null;

        internal bool OpenGraphConnections(UiCaptureViewport viewport)
        {
            if (WireBounded || !IsVisible || Request.Kind == UiSurfaceKind.Hud) return false;
            var graph = GraphForConnections;
            if (graph == null || !UiGraphConnections.IsActionable(graph)) return false;
            var rows = FakeUiTreeCapture.MeasureGraphConnections(owner, this, viewport).Rows;
            if (rows == 0) return false;
            graphConnections ??= new UiGraphConnections();
            graphConnections.Bind(graph);
            graphConnections.Resize(rows);
            return true;
        }

        internal void CloseGraphConnections() => graphConnections?.Bind(null);

        internal bool MoveGraphConnection(int step) => graphConnections?.Move(step) == true;

        internal OperationResult<bool> RemoveGraphConnectionSelection() =>
            graphConnections?.Graph is { } graph && graphConnections.Selected is { } edge && IsVisible
                ? RemoveGraphConnection(graph.Id!, edge.Id)
                : OperationResult<bool>.Failure(ModErrorCode.InvalidState, "No graph connection is selected.");

        private void RefreshGraphConnections()
        {
            if (focusedConnectionGraphId != null && !UiLayoutTree.TryFind(Content, focusedConnectionGraphId, out _, out _))
                focusedConnectionGraphId = null;
            var previous = graphConnections?.Graph;
            if (previous == null) return;
            graphConnections!.Bind(UiLayoutTree.TryFind(Content, previous.Id!, out var current, out var disabled) &&
                current is UiGraphCanvas graph && !disabled && UiGraphConnections.IsActionable(graph) ? graph : null);
        }

        private static UiGraphCanvas? FirstRemovableGraph(UiNode? node)
        {
            if (node == null || UiLayoutTree.IsDisabled(node)) return null;
            if (node is UiGraphCanvas graph) return UiGraphConnections.IsActionable(graph) ? graph : null;
            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++)
                if (FirstRemovableGraph(children[index]) is { } candidate) return candidate;
            return null;
        }
    }
}
