using System;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private void PaintList(UiVirtualList list, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            Add(path, list.Id, nameof(UiVirtualList), bounds, clip, EmptyContent, default);
            surface.TryGetSelectedListItem(list.Id!, out var selection);
            var count = Math.Min(list.Items.Count, list.VisibleRows);
            var state = State(list.Enabled);
            var background = clip.Intersect(bounds);
            for (var index = 0; index < count; index++)
            {
                var item = list.Items[index];
                var selected = item.Id == selection;
                var role = UiControlPaint.ListRowBackground(list.Enabled, selected);
                var pair = PairFor(role, list.Enabled ? UiTone.Neutral : UiTone.Muted);
                var rowState = selected || (selection == null && index == 0) ? state : UiCaptureControlState.Rest;
                var fill = StatefulFill(pair, rowState, TextMinimum, switchControl: false);
                var flags = ControlFlags(list.Enabled);
                if (selected)
                    flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                var row = new UiCaptureRect(bounds.X, bounds.Y + index * ListStride, bounds.Width, ControlHeight);
                var itemPath = ChildPath(path, index) + ":" + Uri.EscapeDataString(item.Id);
                PaintHitTarget(itemPath, row, background, list.Enabled);
                var paint = new UiCaptureNodePaint(pair.On, fill, theme.Descriptor.Shape.Small * layoutScale,
                    BorderWidth(selected), 0f, 0f);
                Add(itemPath, item.Id, "ListItem", row, background, TextContent(item.Title, UiTextStyle.Body, flags), paint);
                if (item.Subtitle.Length > 0)
                {
                    var size = MeasureText(item.Subtitle, UiTextStyle.Caption, row.Width);
                    var subtitle = new UiCaptureRect(row.Right - size.Width - 8f * layoutScale, row.Y, size.Width, row.Height);
                    Add(itemPath + "/subtitle", null, "ListItemSubtitle", subtitle, background,
                        TextContent(item.Subtitle, UiTextStyle.Caption, flags), new UiCaptureNodePaint(pair.On, fill));
                }
                PaintFocus(itemPath, rowState, row, background, UiPaintRole.Surface);
            }
        }

        private void PaintGraph(UiGraphCanvas graph, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
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
                var role = UiControlPaint.GraphNodeBackground(enabled, selected);
                var pair = PairFor(role, enabled ? node.Tone : UiTone.Muted);
                // Graph zoom transforms positions. Text remains at the user's chosen readable scale.
                var width = 216f * layoutScale;
                var height = 94f * layoutScale;
                var left = bounds.X + (bounds.Width - width) / 2f + (node.Position.X + camera.Offset.X) * worldScale;
                var top = bounds.Y + (bounds.Height - height) / 2f - (node.Position.Y + camera.Offset.Y) * worldScale;
                var box = new UiCaptureRect(left, top, width, height);
                var flags = ControlFlags(enabled);
                if (selected)
                    flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                var nodePath = ChildPath(path, index) + ":" + Uri.EscapeDataString(node.Id);
                PaintHitTarget(nodePath, box, graphClip, enabled);
                var state = selected || (selection == null && index == 0) ? State(enabled) : UiCaptureControlState.Rest;
                var fill = StatefulFill(pair, state, TextMinimum, switchControl: false);
                var paint = new UiCaptureNodePaint(pair.On, fill, theme.Descriptor.Shape.Medium * layoutScale,
                    BorderWidth(selected), 0f, 0f);
                Add(nodePath, node.Id, "GraphNode", box, graphClip, TextContent(node.Title, UiTextStyle.Label, flags), paint);
                var subtitle = node.Subtitle.Length == 0 ? node.Type : node.Subtitle;
                var subtitleBounds = box.Inset(10f * layoutScale, 32f * layoutScale, 10f * layoutScale, 10f * layoutScale);
                Add(nodePath + "/subtitle", null, "GraphNodeSubtitle", subtitleBounds, graphClip,
                    TextContent(subtitle, UiTextStyle.Caption, flags), new UiCaptureNodePaint(pair.On, fill));
                PaintFocus(nodePath, state, box, graphClip, UiPaintRole.Sunken);
            }
        }
    }
}
