namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private const float GraphLabelPadding = 24f;

        internal static UiGraphConnectionsViewport MeasureGraphConnections(FakeUiService service, FakeUiSurface surface,
            UiCaptureViewport viewport)
        {
            var capture = new FakeUiTreeCapture(service, surface, viewport, UiCaptureControlState.Rest, null);
            return capture.ConnectionsAperture(capture.SurfaceBounds());
        }

        private UiGraphConnectionsViewport ConnectionsAperture(UiCaptureRect bounds) =>
            new UiGraphConnectionsViewport(bounds.Width / layoutScale,
                bounds.Height / layoutScale - UiGraphConnectionsViewport.RowSlot);

        private bool PaintGraphConnections(UiCaptureRect bounds, UiCaptureRect clip, UiThemeColor? background)
        {
            if (surface.FirstGraphWithConnections == null) return false;
            var scale = layoutScale;
            var label = UiGraphConnections.LauncherLabel;
            var textWidth = MeasureText(label, UiTextStyle.Label, float.PositiveInfinity, wrap: false).Width;
            var width = (float)UiGraphConnectionsViewport.LauncherWidth(bounds.Width / scale, textWidth / scale + GraphLabelPadding);
            if (width <= 0f) return false;

            var model = surface.GraphConnections;
            var geometry = ConnectionsAperture(bounds);
            var open = model?.Graph != null && geometry.Rows > 0;
            var inset = UiGraphConnections.Gutter;
            var launcher = new UiCaptureRect(bounds.Right - (48f + width - inset) * scale, bounds.Y + inset * scale,
                (width - 2f * inset) * scale, UiGraphConnections.TargetHeight * scale);
            var enabled = open || geometry.Rows > 0 && surface.GraphForConnections is { } focused &&
                UiGraphConnections.IsActionable(focused);
            PaintGraphConnectionControl("/graph-connections", null, "GraphConnectionsAction", label, label,
                launcher, clip, UiPaintRole.Raised, enabled, open, UiCaptureControlState.Rest);
            if (!open) return false;

            var graph = model!.Graph!;
            var pane = bounds.Inset(0f, UiGraphConnectionsViewport.RowSlot * scale, 0f, 0f);
            var paneClip = pane.Intersect(clip);
            Add("/0", graph.Id, "GraphConnectionsList", pane, paneClip, EmptyContent, new UiCaptureNodePaint(null, background));
            var first = UiGraphConnections.Reveal(model.SelectedIndex, model.First, geometry.Rows, model.Count);
            var count = System.Math.Min(geometry.Rows, model.Count);
            var selectedLabel = string.Empty;
            for (var slot = 0; slot < count; slot++)
            {
                var index = first + slot;
                var edge = graph.Edges[index];
                var rowLabel = UiGraphConnections.RowLabel(graph, edge);
                var selected = index == model.SelectedIndex;
                if (selected) selectedLabel = rowLabel;
                PaintGraphConnectionControl(ChildPath("/0", slot), edge.Id, "GraphConnectionRow", rowLabel, rowLabel,
                    Place(geometry.Row(slot)), paneClip, UiPaintRole.Surface, enabled: true, selected,
                    selected ? controlState : UiCaptureControlState.Rest);
            }

            var removeLabel = UiGraphConnections.RemovePrefix + selectedLabel;
            var remove = Place(geometry.Remove);
            var removeTextWidth = MeasureText(removeLabel, UiTextStyle.Label, float.PositiveInfinity, wrap: false).Width;
            var removeText = removeTextWidth + GraphLabelPadding * scale > remove.Width ? "Remove" : removeLabel;
            PaintGraphConnectionControl("/0/remove", model.Selected?.Id, "GraphConnectionRemove", removeText, removeLabel,
                remove, paneClip, UiPaintRole.Surface, enabled: true, selected: false, UiCaptureControlState.Rest);
            return true;

            UiCaptureRect Place(UiLayoutRect rect) =>
                new UiCaptureRect(pane.X + (float)rect.X * scale, pane.Y + (float)rect.Y * scale,
                    (float)rect.Width * scale, (float)rect.Height * scale);
        }

        private void PaintGraphConnectionControl(string path, string? id, string kind, string text, string label,
            UiCaptureRect bounds, UiCaptureRect clip, UiPaintRole backing, bool enabled, bool selected, UiCaptureControlState state)
        {
            var role = !enabled ? UiPaintRole.Sunken : selected ? UiPaintRole.Selected : backing;
            var flags = ControlFlags(enabled) | (selected ? UiCaptureNodeFlags.Selected : UiCaptureNodeFlags.None);
            var pair = PairFor(role, UiTone.Neutral);
            var fill = StatefulFill(pair, state, TextMinimum, switchControl: true);
            Add(path, id, kind, bounds, clip, TextContent(text, UiTextStyle.Label, flags, label),
                new UiCaptureNodePaint(pair.On, fill, theme.Descriptor.Shape.Control * layoutScale, 0f, 0f, 0f));
            Add(path + "/hit", null, "HitTarget", bounds, clip, StateLabel(flags | UiCaptureNodeFlags.HitTarget, label), default);
            PaintFocus(path, state, bounds, clip, backing, label);
        }

        private static UiCaptureNodeContent StateLabel(UiCaptureNodeFlags flags, string label) =>
            new UiCaptureNodeContent(string.Empty, label, 0f, 0f, flags);
    }
}
