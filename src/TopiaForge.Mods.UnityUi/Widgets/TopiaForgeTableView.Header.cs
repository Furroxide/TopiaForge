using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeTableView<T>
    {
        private const float HeaderHeight = 26f;
        private const float IndicatorSize = 12f;

        private HeaderCell[] headerCells = null!;
        private TopiaForgeImage headerDivider = null!;

        private void BuildHeader(TopiaForgeContainer body)
        {
            // Same horizontal padding and spacing as TopiaForgeListRow, so header and cell widths line up.
            var header = body.CreateChild("Header");
            TopiaForgeLayout.ApplyRow(header.Go, TopiaForgeGap.Sm, TopiaForgeGap.Sm);
            header.Go.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(8, 8, 2, 2);
            header.FixedHeight(HeaderHeight);

            headerCells = new HeaderCell[columns.Length];
            for (var index = 0; index < columns.Length; index++)
            {
                var column = columns[index];
                var cell = header.CreateChild("Header" + index);
                ApplyColumnWidth(cell, column);
                var fill = cell.Go.AddComponent<UImage>();
                fill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
                fill.type = UImage.Type.Sliced;
                fill.raycastTarget = column.IsSortable;

                var label = cell.Label(column.Header, TopiaForgeTextStyle.Caption).Ellipsis().Free();
                TopiaForgeAnchors.Stretch(label.Rect, 6f, 0f, column.IsSortable ? IndicatorSize + 8f : 6f, 0f);
                if (column.Alignment == TopiaForgeColumnAlignment.Right)
                {
                    label.AlignRight();
                }
                else if (column.Alignment == TopiaForgeColumnAlignment.Center)
                {
                    label.AlignCenter();
                }

                var indicator = cell.FreeImage("SortIndicator").Icon(TopiaForgeIcon.ChevronDown);
                indicator.Rect.anchorMin = new Vector2(1f, 0.5f);
                indicator.Rect.anchorMax = new Vector2(1f, 0.5f);
                indicator.Rect.pivot = new Vector2(1f, 0.5f);
                indicator.Rect.anchoredPosition = new Vector2(-5f, 0f);
                indicator.Rect.sizeDelta = new Vector2(IndicatorSize, IndicatorSize);
                indicator.SetVisible(false);

                Button? button = null;
                if (column.IsSortable)
                {
                    var captured = index;
                    button = cell.Go.AddComponent<Button>();
                    button.targetGraphic = fill;
                    var colors = button.colors;
                    colors.normalColor = Color.white;
                    colors.highlightedColor = new Color(1.06f, 1.06f, 1.06f, 1f);
                    colors.pressedColor = new Color(0.94f, 0.94f, 0.94f, 1f);
                    colors.selectedColor = new Color(1.04f, 1.04f, 1.04f, 1f);
                    colors.disabledColor = Color.white;
                    button.colors = colors;
                    button.onClick.AddListener(() => ToggleSort(captured));
                }

                headerCells[index] = new HeaderCell(fill, label, indicator, button);
            }

            headerDivider = body.Divider();
        }

        private void PaintHeaders(TopiaForgeResolvedTheme theme)
        {
            for (var index = 0; index < headerCells.Length; index++)
            {
                var cell = headerCells[index];
                var direction = sort.DirectionOf(index);
                var sorted = direction != TopiaForgeSortDirection.None;
                cell.Fill.color = theme.SurfaceSunken;
                cell.Label.SetTone(sorted ? TopiaForgeTone.Neutral : TopiaForgeTone.Muted);
                cell.Indicator.SetTone(TopiaForgeTone.Primary);
                cell.Indicator.SetVisible(sorted);
                cell.Indicator.SetRotation(direction == TopiaForgeSortDirection.Ascending ? 180f : 0f);
            }

            headerDivider.SetColor(theme.Tint);
        }

        private void SetHeadersEnabled(bool enabled)
        {
            for (var index = 0; index < headerCells.Length; index++)
            {
                var button = headerCells[index].Button;
                if (button != null)
                {
                    button.interactable = enabled;
                }
            }
        }

        private sealed class HeaderCell
        {
            public HeaderCell(UImage fill, TopiaForgeLabel label, TopiaForgeImage indicator, Button? button)
            {
                Fill = fill;
                Label = label;
                Indicator = indicator;
                Button = button;
            }

            public UImage Fill { get; }

            public TopiaForgeLabel Label { get; }

            public TopiaForgeImage Indicator { get; }

            public Button? Button { get; }
        }
    }
}
