namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        internal static UiGraphConnectionsViewport MeasureGraphConnections(FakeUiService service, FakeUiSurface surface,
            UiCaptureViewport viewport)
        {
            var capture = new FakeUiTreeCapture(service, surface, viewport, UiCaptureControlState.Rest, null);
            var bounds = capture.SurfaceBounds();
            return capture.GraphContentFits() && capture.MeasureConnectionsLauncher(bounds).SlotWidth > 0d
                ? capture.ConnectionsAperture(bounds) : default;
        }

        private bool GraphContentFits()
        {
            if (surface.Content is not { } content) return false;
            try { VerifyComposition(content); }
            catch (UiLayoutException) { return false; }
            return true;
        }

        internal static int SelectedConnectionLabelPages(FakeUiService service, FakeUiSurface surface, UiCaptureViewport viewport)
        {
            if (surface.GraphConnections is not { Graph: { } graph, Selected: { } edge }) return 0;
            var capture = new FakeUiTreeCapture(service, surface, viewport, UiCaptureControlState.Rest, null);
            var geometry = capture.ConnectionsAperture(capture.SurfaceBounds());
            if (geometry.Rows == 0) return 0;
            var labels = UiGraphConnections.Labels(graph, edge);
            return UiGraphConnectionLabelPages.Measure(labels, labels.Full, geometry.Row(0), capture.MeasureConnectionsText).Count;
        }

        private (double Width, double Height) MeasureConnectionsText(string value)
        {
            var size = MeasureText(value, UiTextStyle.Label, float.PositiveInfinity, wrap: false);
            return (size.Width / layoutScale, size.Height / layoutScale);
        }

        private UiGraphConnectionsViewport ConnectionsAperture(UiCaptureRect bounds) =>
            new UiGraphConnectionsViewport(bounds.Width / layoutScale,
                bounds.Height / layoutScale - UiGraphConnectionsViewport.RowSlot);

        private (string Caption, double SlotWidth) MeasureConnectionsLauncher(UiCaptureRect bounds) =>
            UiGraphConnectionsViewport.MeasureLauncher(bounds.Width / layoutScale, MeasureConnectionsText);

        private bool PaintGraphConnections(UiCaptureRect bounds, UiCaptureRect clip, UiThemeColor? background)
        {
            if (surface.FirstGraphWithConnections == null || !GraphContentFits()) return false;
            var scale = layoutScale;
            var measurement = MeasureConnectionsLauncher(bounds);
            var width = (float)measurement.SlotWidth;
            if (width <= 0f)
            {
                nodes.Add(new UiCapturedNode("/graph-connections", null, "GraphConnectionsUnavailable", default,
                    new UiCaptureNodeContent(string.Empty, UiGraphConnections.LauncherLabel, 0f, 0f,
                        UiCaptureNodeFlags.LayoutDiagnostic | UiCaptureNodeFlags.InvalidLayout), default,
                    theme.Descriptor.Id, theme.UsedFallback, isRendered: false, clipped: false, occluded: false));
                return false;
            }

            var model = surface.GraphConnections;
            var geometry = ConnectionsAperture(bounds);
            var open = model?.Graph != null && geometry.Rows > 0;
            var inset = UiGraphConnections.Gutter;
            var launcher = new UiCaptureRect(bounds.Right - (48f + width - inset) * scale, bounds.Y + inset * scale,
                (width - 2f * inset) * scale, UiGraphConnections.TargetHeight * scale);
            var enabled = open || geometry.Rows > 0 && surface.GraphForConnections is { } focused &&
                UiGraphConnections.IsActionable(focused);
            PaintGraphConnectionControl("/graph-connections", null, "GraphConnectionsAction", measurement.Caption,
                UiGraphConnections.LauncherLabel,
                launcher, clip, UiPaintRole.Raised, enabled, open, open ? UiCaptureControlState.Rest : State(enabled));
            if (!open) return false;

            var graph = model!.Graph!;
            var pane = bounds.Inset(0f, UiGraphConnectionsViewport.RowSlot * scale, 0f, 0f);
            var paneClip = pane.Intersect(clip);
            Add("/0", graph.Id, "GraphConnectionsList", pane, paneClip, EmptyContent, new UiCaptureNodePaint(null, background));
            var first = UiGraphConnections.Reveal(model.SelectedIndex, model.First, geometry.Rows, model.Count);
            var count = System.Math.Min(geometry.Rows, model.Count);
            var selectedLabel = string.Empty;
            var selectedFits = false;
            for (var slot = 0; slot < count; slot++)
            {
                var index = first + slot;
                var edge = graph.Edges[index];
                var labels = UiGraphConnections.Labels(graph, edge);
                var full = labels.Full;
                var selected = index == model.SelectedIndex;
                var row = geometry.Row(slot);
                var pages = UiGraphConnectionLabelPages.Measure(labels, full, row, MeasureConnectionsText);
                if (selected)
                {
                    selectedLabel = full;
                    selectedFits = pages.Count != 0;
                }

                PaintGraphConnectionControl(ChildPath("/0", slot), edge.Id, "GraphConnectionRow",
                    pages.Display(selected ? model.LabelPage : 0), full, Place(row), paneClip, UiPaintRole.Surface,
                    enabled: true, selected, selected && controlState != UiCaptureControlState.Pressed ? controlState : UiCaptureControlState.Rest,
                    labelFits: pages.Count != 0);
            }

            var removeLabel = UiGraphConnections.RemovePrefix + selectedLabel;
            var remove = geometry.Remove;
            var caption = UiGraphConnectionLabelPages.RemoveCaption(removeLabel, remove, MeasureConnectionsText);
            PaintGraphConnectionControl("/0/remove", model.Selected?.Id, "GraphConnectionRemove", caption, removeLabel,
                Place(remove), paneClip, UiPaintRole.Surface, enabled: selectedFits && caption.Length != 0, selected: false,
                controlState == UiCaptureControlState.Pressed ? controlState : UiCaptureControlState.Rest, labelFits: caption.Length != 0);
            return true;

            UiCaptureRect Place(UiLayoutRect rect) =>
                new UiCaptureRect(pane.X + (float)rect.X * scale, pane.Y + (float)rect.Y * scale,
                    (float)rect.Width * scale, (float)rect.Height * scale);
        }

        private void PaintGraphConnectionControl(string path, string? id, string kind, string text, string label,
            UiCaptureRect bounds, UiCaptureRect clip, UiPaintRole backing, bool enabled, bool selected, UiCaptureControlState state,
            bool labelFits = true)
        {
            var role = !enabled ? UiPaintRole.Sunken : selected ? UiPaintRole.Selected : backing;
            var flags = ControlFlags(enabled) | (selected ? UiCaptureNodeFlags.Selected : UiCaptureNodeFlags.None);
            var pair = PairFor(role, UiTone.Neutral);
            var tints = UiThemeColors.ResolveTints(pair, TextMinimum, UiThemeColors.ControlHighlighted,
                UiThemeColors.ControlHighlighted, UiThemeColors.ControlPressed);
            var fill = StateFill(pair.Fill, state, tints);
            var authoredSelectionCue = enabled && selected && pair.Fill.Equals(PairFor(backing, UiTone.Neutral).Fill);
            if (authoredSelectionCue) flags |= UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            var labelFlags = labelFits ? flags : flags | UiCaptureNodeFlags.InvalidLayout;
            Add(path, id, kind, bounds, clip, TextContent(text, UiTextStyle.Label, labelFlags, label),
                new UiCaptureNodePaint(pair.On, fill, theme.Descriptor.Shape.Control * layoutScale, 0f, 0f, 0f));
            Add(path + "/hit", null, "HitTarget", bounds, clip, StateLabel(flags | UiCaptureNodeFlags.HitTarget, label), default);
            if (enabled)
                PaintStateCues(path, label, state, bounds, clip, pair.Fill, tints, pair.On, fill, authoredSelectionCue: authoredSelectionCue);
            PaintFocus(path, state, bounds, clip, backing, label);
        }

        private static UiCaptureNodeContent StateLabel(UiCaptureNodeFlags flags, string label) =>
            new UiCaptureNodeContent(string.Empty, label, 0f, 0f, flags);
    }
}
