using System;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private void PaintList(UiVirtualList list, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            PaintHitTarget(path, bounds, clip, list.Enabled);
            Add(path, list.Id, nameof(UiVirtualList), bounds, clip, EmptyContent, default);
            surface.TryGetSelectedListItem(list.Id!, out var selection);
            var count = Math.Min(list.Items.Count, list.VisibleRows);
            var state = State(list.Enabled);
            var background = clip.Intersect(bounds);
            for (var index = 0; index < count; index++)
            {
                var item = list.Items[index];
                var selected = item.Id == selection;
                var role = selected ? UiPaintRole.Selected : UiPaintRole.Sunken;
                var pair = PairFor(role, list.Enabled ? UiTone.Neutral : UiTone.Muted);
                var fill = StatefulFill(pair, state, TextMinimum, switchControl: false);
                var flags = ControlFlags(list.Enabled);
                if (selected)
                    flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                var row = new UiCaptureRect(bounds.X, bounds.Y + index * ListStride, bounds.Width, ControlHeight);
                var itemPath = ChildPath(path, index) + ":" + Uri.EscapeDataString(item.Id);
                var paint = new UiCaptureNodePaint(pair.On, fill, theme.Descriptor.Shape.Small * layoutScale,
                    BorderWidth(selected), 0f, 0f);
                Add(itemPath, item.Id, "ListItem", row, background, TextContent(item.Title, UiTextStyle.Body, flags), paint);
                if (item.Subtitle.Length > 0)
                {
                    var size = MeasureText(item.Subtitle, UiTextStyle.Caption, row.Width);
                    var subtitle = new UiCaptureRect(row.Right - size.Width - 8f * layoutScale, row.Y, size.Width, row.Height);
                    var ink = UiThemeColors.RepairForeground(scheme.Muted, fill, TextMinimum);
                    Add(itemPath + "/subtitle", null, "ListItemSubtitle", subtitle, background,
                        TextContent(item.Subtitle, UiTextStyle.Caption, flags), new UiCaptureNodePaint(ink, fill));
                }
            }

            PaintFocus(path, state, bounds, clip, UiPaintRole.Surface);
        }

        private void PaintGraph(UiGraphCanvas graph, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            PaintHitTarget(path, bounds, clip, graph.Enabled);
            var graphBackground = scheme.GetPair(UiPaintRole.Sunken).Fill;
            Add(path, graph.Id, nameof(UiGraphCanvas), bounds, clip, EmptyContent, new UiCaptureNodePaint(null, graphBackground));
            surface.TryGetSelectedGraphNode(graph.Id!, out var selection);
            if (!surface.TryGetGraphViewport(graph.Id!, out var camera)) camera = graph.Viewport;
            var worldScale = camera.Zoom * layoutScale;
            var graphClip = clip.Intersect(bounds);
            for (var index = 0; index < graph.Nodes.Count; index++)
            {
                var node = graph.Nodes[index];
                var selected = node.Id == selection;
                var enabled = graph.Enabled && node.Enabled;
                var role = selected ? UiPaintRole.Selected : UiPaintRole.Raised;
                var pair = PairFor(role, enabled ? node.Tone : UiTone.Muted);
                // Graph zoom transforms positions. Text remains at the user's chosen readable scale.
                var width = 176f * layoutScale;
                var height = Math.Max(48f * layoutScale, MeasureText(node.Title, UiTextStyle.Label, width).Height);
                var left = bounds.X + (bounds.Width - width) / 2f + (node.Position.X + camera.Offset.X) * worldScale;
                var top = bounds.Y + (bounds.Height - height) / 2f - (node.Position.Y + camera.Offset.Y) * worldScale;
                var box = new UiCaptureRect(left, top, width, height);
                var flags = ControlFlags(enabled);
                if (selected)
                    flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                var nodePath = ChildPath(path, index) + ":" + Uri.EscapeDataString(node.Id);
                var paint = new UiCaptureNodePaint(pair.On, pair.Fill, theme.Descriptor.Shape.Medium * layoutScale,
                    BorderWidth(selected), 0f, 0f);
                Add(nodePath, node.Id, "GraphNode", box, graphClip, TextContent(node.Title, UiTextStyle.Label, flags), paint);
            }
        }
    }
}
