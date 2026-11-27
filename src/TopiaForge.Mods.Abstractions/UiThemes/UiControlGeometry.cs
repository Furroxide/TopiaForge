using System;

namespace TopiaForge.Mods
{
    internal static class UiControlFocusGeometry
    {
        internal const float Gap = 2f;
        internal const float MaximumStroke = 3f;
        internal const float MaximumOutset = Gap + MaximumStroke;

        internal static float Stroke(bool highContrast) => highContrast ? MaximumStroke : 2f;
    }

    internal static class UiGraphControlGeometry
    {
        internal const float NodeWidth = 216f;
        internal const float Inset = UiControlFocusGeometry.MaximumOutset + 1f;
        internal const float Spacing = 2f * Inset;
        internal const float HeaderHeight = 60f;
        internal const float PortHeight = 40f;
        internal const float MarkSize = 12f;
        internal const float MarkInset = 6f;
        internal const float PortLabelInset = MarkSize + MarkInset + 4f;
        internal const float HeaderLabelInset = 10f;
        internal const float TitleTop = 4f;
        internal const float TitleHeight = 24f;
        internal const float SubtitleTop = TitleTop + TitleHeight;
        internal const float SubtitleHeight = 26f;
        private const float PortWidth = (NodeWidth - 2f * Inset - Spacing) / 2f;
        private const float PortsTop = Inset + HeaderHeight + Spacing;

        internal static (float X, float Y, float Width, float Height) Header =>
            (Inset, Inset, NodeWidth - 2f * Inset, HeaderHeight);

        internal static (float X, float Y, float Width, float Height) Port(UiGraphPortDirection direction, int row) =>
            (direction == UiGraphPortDirection.Input ? Inset : NodeWidth - Inset - PortWidth,
                PortsTop + row * (PortHeight + Spacing), PortWidth, PortHeight);

        internal static float NodeHeight(UiGraphNode node)
        {
            var inputs = 0;
            for (var index = 0; index < node.Ports.Count; index++)
                if (node.Ports[index].Direction == UiGraphPortDirection.Input) inputs++;
            var rows = Math.Max(inputs, node.Ports.Count - inputs);
            return rows == 0 ? 94f : Port(UiGraphPortDirection.Input, rows - 1).Y + PortHeight + Inset;
        }

        internal static string PortText(UiGraphPort port) => port.Required ? port.Label + " *" : port.Label;
    }

    internal static class UiVirtualListGeometry
    {
        internal const float RowHeight = 40f;
        internal const float Gutter = UiControlFocusGeometry.MaximumOutset + 1f;
        internal const float Spacing = 2f * Gutter;
        internal const float Stride = RowHeight + Spacing;

        internal static float Height(int count) => count * Stride;
        internal static float RowTop(int index) => Gutter + Height(index);
        internal static int IndexAtRowTop(float top) => (int)Math.Round((top - Gutter) / Stride);

        internal static float RevealRow(int index, float offset, float viewportHeight, int count)
        {
            var top = Height(index);
            var bottom = top + Stride;
            if (top < offset) offset = top;
            else if (bottom > offset + viewportHeight) offset = bottom - viewportHeight;
            return Math.Max(0f, Math.Min(offset, Height(count) - viewportHeight));
        }
    }
}
