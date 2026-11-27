using System.Collections.Generic;
using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeListRow
    {
        private void ConfigureDeclarativeText()
        {
            Go.GetComponent<HorizontalLayoutGroup>().enabled = false;
            Title.PlainText().NoWrap();
            Subtitle.PlainText().NoWrap();
            Badge.UseDeclarativeRow();
        }

        internal void MeasureDeclarativeRows(IReadOnlyList<UiListItem> items, UiVirtualListRowLayout[] sizes)
        {
            var theme = Theme;
            var descriptor = theme.Descriptor ?? UiThemePackParser.Default;
            Title.ApplyTheme(theme);
            Subtitle.ApplyTheme(theme);
            Badge.ApplyTheme(theme);
            var title = Title.Go.GetComponent<TMP_Text>();
            var subtitle = Subtitle.Go.GetComponent<TMP_Text>();
            var badge = Badge.RowLabel;
            var upperTitle = descriptor.GetText(UiTextStyle.Body).IsUppercase;
            var upperSubtitle = descriptor.GetText(UiTextStyle.Caption).IsUppercase;
            var upperBadge = descriptor.GetText(UiTextStyle.Label).IsUppercase;
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                sizes[index] = new UiVirtualListRowLayout(Measure(title, item.Title, upperTitle),
                    item.Subtitle.Length == 0 ? (UiLayoutExtent?)null : Measure(subtitle, item.Subtitle, upperSubtitle),
                    item.Badge.Length == 0 ? (UiLayoutExtent?)null : Measure(badge, item.Badge, upperBadge));
            }
        }

        private static UiLayoutExtent Measure(TMP_Text label, string text, bool uppercase)
        {
            var size = label.GetPreferredValues(uppercase ? text.ToUpperInvariant() : text);
            return new UiLayoutExtent(size.x, size.y);
        }

        internal void PlaceDeclarativeRow(UiVirtualListRowLayout layout, double width)
        {
            var boxes = layout.Place(width);
            Place(Title.Rect, boxes.Title);
            Subtitle.Go.SetActive(boxes.Subtitle.HasArea);
            if (boxes.Subtitle.HasArea) Place(Subtitle.Rect, boxes.Subtitle);
            Badge.Go.SetActive(boxes.Badge.HasArea);
            if (!boxes.Badge.HasArea) return;
            Place(Badge.Rect, boxes.Badge);
            Place(Badge.RowLabel.rectTransform, boxes.BadgeLabel.Translate(-boxes.Badge.X, -boxes.Badge.Y));
        }

        private static void Place(RectTransform target, UiLayoutRect bounds)
        {
            target.anchorMin = target.anchorMax = target.pivot = new Vector2(0f, 1f);
            target.anchoredPosition = new Vector2((float)bounds.X, -(float)bounds.Y);
            target.sizeDelta = new Vector2((float)bounds.Width, (float)bounds.Height);
        }
    }
}
