using System;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private Size MeasureVirtualList(UiVirtualList list, float width)
        {
            var available = width / (double)layoutScale;
            var minimum = (double)UiVirtualListGeometry.RowHeight;
            for (var index = 0; index < list.Items.Count; index++)
            {
                var layout = MeasureVirtualRow(list.Items[index]);
                layout.RequireFits(available, index);
                minimum = Math.Max(minimum, layout.RequiredWidth + 2d * UiVirtualListGeometry.Gutter);
            }

            return new Size(float.IsPositiveInfinity(width) ? (float)(minimum * layoutScale) : width,
                UiVirtualListGeometry.Height(list.VisibleRows) * layoutScale);
        }

        private UiVirtualListRowLayout MeasureVirtualRow(UiListItem item) =>
            new UiVirtualListRowLayout(MeasureRowText(item.Title, UiTextStyle.Body),
                item.Subtitle.Length == 0 ? (UiLayoutExtent?)null : MeasureRowText(item.Subtitle, UiTextStyle.Caption),
                item.Badge.Length == 0 ? (UiLayoutExtent?)null : MeasureRowText(item.Badge, UiTextStyle.Label));

        private UiLayoutExtent MeasureRowText(string text, UiTextStyle style)
        {
            var size = MeasureText(text, style, float.PositiveInfinity, wrap: false);
            return new UiLayoutExtent(size.Width / (double)layoutScale, size.Height / (double)layoutScale);
        }

        private void PaintList(UiVirtualList list, string path, UiCaptureRect bounds, UiCaptureRect clip)
        {
            var enabled = Enabled(list.Enabled);
            Add(path, list.Id, nameof(UiVirtualList), bounds, clip, EmptyContent, default);
            var (selection, offset) = surface.GetListViewport(list.Id!);
            var first = (int)(offset / UiVirtualListGeometry.Stride);
            var end = Math.Min(list.Items.Count, first + list.VisibleRows);
            var state = State(enabled);
            var listClip = clip.Intersect(bounds);
            var gutter = UiVirtualListGeometry.Gutter * layoutScale;
            for (var index = first; index < end; index++)
            {
                var item = list.Items[index];
                var selected = item.Id == selection;
                var role = UiControlPaint.ListRowBackground(enabled, selected);
                var pair = PairFor(role, enabled ? UiTone.Neutral : UiTone.Muted);
                var rowState = selected || (selection == null && index == first) ? state : UiCaptureControlState.Rest;
                var tints = UiThemeColors.ResolveTints(pair, TextMinimum, UiThemeColors.ButtonHovered,
                    UiThemeColors.ButtonSelected, UiThemeColors.ButtonPressed);
                var fill = StateFill(pair.Fill, rowState, tints);
                var flags = ControlFlags(enabled);
                if (selected)
                    flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                var top = bounds.Y + (UiVirtualListGeometry.RowTop(index) - offset) * layoutScale;
                var row = new UiCaptureRect(bounds.X + gutter, top, Math.Max(0f, bounds.Width - 2f * gutter), ControlHeight);
                var itemPath = ChildPath(path, index) + ":" + Uri.EscapeDataString(item.Id);
                var boxes = MeasureVirtualRow(item).Place(row.Width / (double)layoutScale);
                PaintHitTarget(itemPath, row, listClip, enabled);
                var radius = theme.Descriptor.Shape.Small * layoutScale;
                Add(itemPath, item.Id, "ListItem", ScreenRect(boxes.Title, row.X, row.Y), listClip,
                    TextContent(item.Title, UiTextStyle.Body, flags), new UiCaptureNodePaint(pair.On, fill,
                        radius, BorderWidth(selected), 0f, 0f));
                if (item.Subtitle.Length > 0)
                    Add(itemPath + "/subtitle", null, "ListItemSubtitle", ScreenRect(boxes.Subtitle, row.X, row.Y), listClip,
                        TextContent(item.Subtitle, UiTextStyle.Caption, flags), new UiCaptureNodePaint(pair.On, fill));
                if (item.Badge.Length > 0)
                {
                    var badge = scheme.GetPair(UiVirtualListRowLayout.BadgeRole);
                    var stroke = UiControlFocusGeometry.Stroke(preferences.HighContrast) * layoutScale;
                    Add(itemPath + "/badge", null, "ListItemBadge", ScreenRect(boxes.BadgeLabel, row.X, row.Y), listClip,
                        TextContent(item.Badge, UiTextStyle.Label, flags), new UiCaptureNodePaint(badge.On, badge.Fill, radius, stroke, 0f, 0f));
                    Add(itemPath + "/badge/boundary", null, "ListItemBadgeBoundary", ScreenRect(boxes.Badge, row.X, row.Y), listClip,
                        new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, UiCaptureNodeFlags.SemanticMark),
                        new UiCaptureNodePaint(pair.On, fill, radius, stroke));
                }
                PaintStateCues(itemPath, item.Title, rowState, row, listClip, pair.Fill, tints, pair.On, fill,
                    radius: theme.Descriptor.Shape.Small);
                PaintFocus(itemPath, rowState, row, listClip, backdrop, item.Title);
            }
        }
    }
}
