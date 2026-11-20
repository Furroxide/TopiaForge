using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeListView<T>
    {
        private IReadOnlyList<UiListItem>? measuredItems;
        private TopiaForgeResolvedTheme? measuredTheme;
        private float measuredScale;
        private UiVirtualListRowLayout[] rowLayouts = Array.Empty<UiVirtualListRowLayout>();
        private IReadOnlyList<UiListItem>? placedItems;
        private UiVirtualListRowLayout[]? placedLayouts;
        private int widestRow;
        private double placedWidth;

        private double RequireRowWidth(double available)
        {
            if (items is not IReadOnlyList<UiListItem> data)
                throw new InvalidOperationException("Declarative list rows require safe list items.");
            if (data.Count == 0)
            {
                measuredItems = data;
                rowLayouts = Array.Empty<UiVirtualListRowLayout>();
                return 0d;
            }
            var theme = Theme;
            var scale = Host.EffectiveUiScale;
            if (!ReferenceEquals(measuredItems, data) || !ReferenceEquals(measuredTheme, theme) || measuredScale != scale)
            {
                // A single pooled row supplies the three actual TMP faces. Hidden items never create their own widgets.
                var layouts = new UiVirtualListRowLayout[data.Count];
                pool[0].MeasureDeclarativeRows(data, layouts);
                var widest = 0;
                for (var index = 0; index < layouts.Length; index++)
                {
                    layouts[index].RequireFits(double.PositiveInfinity, index);
                    if (layouts[index].RequiredWidth > layouts[widest].RequiredWidth) widest = index;
                }
                measuredItems = data;
                measuredTheme = theme;
                measuredScale = scale;
                rowLayouts = layouts;
                widestRow = widest;
            }

            rowLayouts[widestRow].RequireFits(available, widestRow);
            return rowLayouts[widestRow].RequiredWidth + 2d * UiVirtualListGeometry.Gutter;
        }

        private void PlaceRows(double width)
        {
            if (ReferenceEquals(placedItems, measuredItems) && ReferenceEquals(placedLayouts, rowLayouts) && placedWidth == width) return;
            placedItems = measuredItems;
            placedLayouts = rowLayouts;
            placedWidth = width;
            for (var index = 0; index < pool.Count; index++)
                PlacePooledRow(pool[index], poolBinding[index]);
        }

        private void PlacePooledRow(TopiaForgeListRow row, int index)
        {
            if (placedLayouts == null || !ReferenceEquals(placedItems, items) || (uint)index >= (uint)placedLayouts.Length) return;
            row.PlaceDeclarativeRow(placedLayouts[index], placedWidth - 2d * UiVirtualListGeometry.Gutter);
        }
    }
}
