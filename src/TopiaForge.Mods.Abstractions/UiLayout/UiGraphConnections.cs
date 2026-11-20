using System;

namespace TopiaForge.Mods
{
    internal sealed class UiGraphConnections
    {
        internal const string LauncherLabel = "Connections";
        internal const string RemovePrefix = "Remove connection from ";
        internal const int MaximumVisibleRows = 12;
        internal const float TargetHeight = UiVirtualListGeometry.RowHeight;
        internal const float Gutter = UiVirtualListGeometry.Gutter;

        private int visibleRows;

        internal UiGraphCanvas? Graph { get; private set; }
        internal int SelectedIndex { get; private set; } = -1;
        internal int First { get; private set; }
        internal int LabelPage { get; private set; }
        internal int Count => Graph?.Edges.Count ?? 0;
        internal int DrawnRows => Math.Min(visibleRows, Count);
        internal UiGraphEdge? Selected => SelectedIndex < 0 ? null : Graph!.Edges[SelectedIndex];

        internal static bool IsActionable(UiGraphCanvas graph) =>
            graph.Enabled && graph.ConnectionRemoved != null && graph.Edges.Count != 0;

        internal bool Bind(UiGraphCanvas? graph)
        {
            if (ReferenceEquals(Graph, graph)) return false;
            LabelPage = 0;
            var sameGraph = string.Equals(Graph?.Id, graph?.Id, StringComparison.Ordinal);
            var retainedId = sameGraph ? Selected?.Id : null;
            var priorIndex = sameGraph ? SelectedIndex : 0;
            if (!sameGraph) First = 0;
            Graph = graph;
            SelectedIndex = Count == 0 ? -1 : Math.Clamp(priorIndex, 0, Count - 1);
            if (retainedId != null)
            {
                for (var index = 0; index < Count; index++)
                {
                    if (!string.Equals(graph!.Edges[index].Id, retainedId, StringComparison.Ordinal)) continue;
                    SelectedIndex = index;
                    break;
                }
            }

            Reveal();
            return true;
        }

        internal bool Resize(int rows)
        {
            if (visibleRows == rows) return false;
            visibleRows = rows;
            Reveal();
            return true;
        }

        internal bool Select(int index)
        {
            if (index < 0 || index >= Count || index == SelectedIndex) return false;
            SelectedIndex = index;
            LabelPage = 0;
            Reveal();
            return true;
        }

        internal bool Move(int step) =>
            SelectedIndex >= 0 && Select(Math.Clamp(SelectedIndex + step, 0, Count - 1));

        private void Reveal()
        {
            if (visibleRows == 0 || SelectedIndex < 0)
            {
                First = 0;
                return;
            }

            First = Reveal(SelectedIndex, First, visibleRows, Count);
        }

        internal static int Reveal(int selected, int first, int rows, int count)
        {
            if (selected < first) first = selected;
            if (selected >= first + rows) first = selected - rows + 1;
            return Math.Min(first, Math.Max(0, count - rows));
        }

        internal bool AdvanceLabel(int pages)
        {
            if (pages <= 1) return false;
            LabelPage = LabelPage >= pages - 1 ? 0 : LabelPage + 1;
            return true;
        }

        internal static UiGraphConnectionLabels Labels(UiGraphCanvas graph, UiGraphEdge edge) =>
            new UiGraphConnectionLabels(Endpoint(graph, edge.SourceNodeId, edge.SourcePortId),
                Endpoint(graph, edge.TargetNodeId, edge.TargetPortId));

        private static string Endpoint(UiGraphCanvas graph, string nodeId, string portId)
        {
            for (var index = 0; index < graph.Nodes.Count; index++)
            {
                var node = graph.Nodes[index];
                if (!string.Equals(node.Id, nodeId, StringComparison.Ordinal)) continue;
                for (var portIndex = 0; portIndex < node.Ports.Count; portIndex++)
                {
                    var port = node.Ports[portIndex];
                    if (!string.Equals(port.Id, portId, StringComparison.Ordinal)) continue;
                    return SingleLine(node.Title, "Node") + " / " + SingleLine(port.Label, "Port");
                }
            }

            throw new InvalidOperationException("An admitted graph edge lost an endpoint.");
        }

        private static string SingleLine(string text, string fallback)
        {
            var safe = UiPlainText.Sanitize(text, 256);
            return safe.Length == 0 ? fallback : safe;
        }
    }

    internal readonly struct UiGraphConnectionsViewport
    {
        internal const float RowSlot = UiVirtualListGeometry.Stride;
        private readonly double width;
        private readonly double height;
        private readonly bool sideBySide;

        internal UiGraphConnectionsViewport(double width, double height)
        {
            this.width = width;
            this.height = height;
            sideBySide = height < RowSlot * 2;
            var minimumWidth = sideBySide ? RowSlot * 2 : RowSlot;
            Rows = width >= minimumWidth && height >= RowSlot
                ? sideBySide ? 1 : (int)Math.Min(UiGraphConnections.MaximumVisibleRows, Math.Floor(height / RowSlot) - 1)
                : 0;
        }

        internal int Rows { get; }

        internal static (string Caption, double SlotWidth) MeasureLauncher(double titleWidth,
            Func<string, (double Width, double Height)> measure)
        {
            const float reserved = UiGraphConnections.TargetHeight * 2 + 16f;
            var available = titleWidth - reserved;
            if (available < RowSlot) return (string.Empty, 0d);
            var target = Control(0d, 0d, available);
            var caption = UiGraphConnectionLabelPages.LauncherCaption(target, measure);
            if (caption.Length == 0) return (string.Empty, 0d);
            var required = measure(caption).Width +
                2 * (UiGraphConnections.Gutter + UiGraphConnectionLabelPages.HorizontalInset);
            return (caption, Math.Max(RowSlot, required));
        }

        internal UiLayoutRect Row(int index) =>
            Control(UiGraphConnections.Gutter, UiVirtualListGeometry.RowTop(index), sideBySide ? width / 2 : width);

        internal UiLayoutRect Remove =>
            sideBySide ? Control(width / 2 + UiGraphConnections.Gutter, UiGraphConnections.Gutter, width / 2)
                : Control(UiGraphConnections.Gutter, height - RowSlot + UiGraphConnections.Gutter, width);

        private static UiLayoutRect Control(double x, double y, double slotWidth) =>
            new UiLayoutRect(x, y, slotWidth - UiGraphConnections.Gutter * 2, UiGraphConnections.TargetHeight);
    }
}
