using System;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private void PaintList(UiVirtualList list, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            Add(path, list.Id, nameof(UiVirtualList), bounds, clip, EmptyContent, default);
            var (selection, offset) = surface.GetListViewport(list.Id!);
            var first = (int)(offset / UiVirtualListGeometry.Stride);
            var end = Math.Min(list.Items.Count, first + list.VisibleRows);
            var state = State(list.Enabled);
            var background = clip.Intersect(bounds);
            var gutter = UiVirtualListGeometry.Gutter * layoutScale;
            for (var index = first; index < end; index++)
            {
                var item = list.Items[index];
                var selected = item.Id == selection;
                var role = UiControlPaint.ListRowBackground(list.Enabled, selected);
                var pair = PairFor(role, list.Enabled ? UiTone.Neutral : UiTone.Muted);
                var rowState = selected || (selection == null && index == first) ? state : UiCaptureControlState.Rest;
                var fill = StatefulFill(pair, rowState, TextMinimum, switchControl: false);
                var flags = ControlFlags(list.Enabled);
                if (selected)
                    flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                var top = bounds.Y + (UiVirtualListGeometry.RowTop(index) - offset) * layoutScale;
                var row = new UiCaptureRect(bounds.X + gutter, top, Math.Max(0f, bounds.Width - 2f * gutter), ControlHeight);
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
                PaintFocus(itemPath, rowState, row, background, UiPaintRole.Surface, item.Title);
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
                var width = UiGraphControlGeometry.NodeWidth * layoutScale;
                var height = UiGraphControlGeometry.NodeHeight(node) * layoutScale;
                var left = bounds.X + (bounds.Width - width) / 2f + (node.Position.X + camera.Offset.X) * worldScale;
                var top = bounds.Y + (bounds.Height - height) / 2f - (node.Position.Y + camera.Offset.Y) * worldScale;
                var box = new UiCaptureRect(left, top, width, height);
                var flags = ControlFlags(enabled);
                if (selected)
                    flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                var nodePath = ChildPath(path, index) + ":" + Uri.EscapeDataString(node.Id);
                var state = selected || (selection == null && index == 0) ? State(enabled) : UiCaptureControlState.Rest;
                var header = GraphBox(box, UiGraphControlGeometry.Header);
                var headerState = node.Ports.Count != 0 && state == UiCaptureControlState.Focused
                    ? UiCaptureControlState.Rest : state;
                var fill = StatefulFill(pair, headerState, TextMinimum, switchControl: false);
                PaintHitTarget(nodePath, header, graphClip, enabled);
                var paint = new UiCaptureNodePaint(pair.On, fill, theme.Descriptor.Shape.Control * layoutScale, 0f, 0f, 0f);
                var title = GraphLabel(header, UiGraphControlGeometry.TitleTop, UiGraphControlGeometry.TitleHeight);
                Add(nodePath, node.Id, "GraphNode", title, graphClip, TextContent(node.Title, UiTextStyle.Label, flags), paint);
                var subtitle = node.Subtitle.Length == 0 ? node.Type : node.Subtitle;
                var subtitleBounds = GraphLabel(header, UiGraphControlGeometry.SubtitleTop, UiGraphControlGeometry.SubtitleHeight);
                Add(nodePath + "/subtitle", null, "GraphNodeSubtitle", subtitleBounds, graphClip,
                    TextContent(subtitle, UiTextStyle.Caption, flags), new UiCaptureNodePaint(pair.On, fill));
                PaintFocus(nodePath, headerState, header, graphClip, role, node.Title);

                var inputs = 0;
                var outputs = 0;
                for (var portIndex = 0; portIndex < node.Ports.Count; portIndex++)
                {
                    var port = node.Ports[portIndex];
                    var input = port.Direction == UiGraphPortDirection.Input;
                    var portBounds = GraphBox(box, UiGraphControlGeometry.Port(port.Direction, input ? inputs++ : outputs++));
                    var portPath = ChildPath(nodePath + "/ports", portIndex) + ":" + Uri.EscapeDataString(port.Id);
                    var portState = portIndex == 0 ? state : UiCaptureControlState.Rest;
                    PaintGraphPort(port, portPath, portBounds, graphClip, enabled, role, portState);
                }
            }
        }

        private void PaintGraphPort(UiGraphPort port, string path, UiCaptureRect bounds, UiCaptureRect clip,
            bool enabled, UiPaintRole role, UiCaptureControlState state)
        {
            var pair = PairFor(role, enabled ? UiTone.Neutral : UiTone.Muted);
            var fill = StatefulFill(pair, state, TextMinimum, switchControl: false);
            var flags = ControlFlags(enabled);
            var input = port.Direction == UiGraphPortDirection.Input;
            var inset = UiGraphControlGeometry.MarkInset * layoutScale;
            var labelInset = UiGraphControlGeometry.PortLabelInset * layoutScale;
            var label = bounds.Inset(input ? labelInset : inset, 0f, input ? inset : labelInset, 0f);
            Add(path, port.Id, "GraphPort", label, clip, TextContent(UiGraphControlGeometry.PortText(port), UiTextStyle.Label, flags),
                new UiCaptureNodePaint(pair.On, fill, theme.Descriptor.Shape.Control * layoutScale, 0f, 0f, 0f));
            var size = UiGraphControlGeometry.MarkSize * layoutScale;
            var mark = new UiCaptureRect(input ? bounds.X + inset : bounds.Right - inset - size,
                bounds.Y + (bounds.Height - size) / 2f, size, size);
            Add(path + "/mark", null, "GraphPortMark", mark, clip,
                new UiCaptureNodeContent(string.Empty, port.Label, 0f, 0f, flags | UiCaptureNodeFlags.SemanticMark),
                new UiCaptureNodePaint(pair.On, fill));
            PaintHitTarget(path, bounds, clip, enabled);
            PaintFocus(path, state, bounds, clip, role, port.Label);
        }

        private UiCaptureRect GraphBox(UiCaptureRect origin, (float X, float Y, float Width, float Height) box) =>
            new UiCaptureRect(origin.X + box.X * layoutScale, origin.Y + box.Y * layoutScale,
                box.Width * layoutScale, box.Height * layoutScale);

        private UiCaptureRect GraphLabel(UiCaptureRect header, float top, float height) =>
            GraphBox(header, (UiGraphControlGeometry.HeaderLabelInset, top,
                UiGraphControlGeometry.Header.Width - 2f * UiGraphControlGeometry.HeaderLabelInset, height));
    }
}
