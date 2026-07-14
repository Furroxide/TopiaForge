using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Tab strip with owned selection state (no more consumer-side color swapping).
    /// Horizontal bar by default; NavRail() turns it into the vertical manager-style
    /// navigation with full-width items.
    /// </summary>
    public sealed class QwTabs : QwWidget, IQwThemeAware
    {
        private sealed class TabItem
        {
            public TabItem(UImage fill, UImage ring, TextMeshProUGUI label, Button button)
            {
                Fill = fill;
                Ring = ring;
                Label = label;
                Button = button;
            }

            public UImage Fill { get; }
            public UImage Ring { get; }
            public TextMeshProUGUI Label { get; }
            public Button Button { get; }
        }

        private readonly List<TabItem> items = new List<TabItem>();
        private readonly bool vertical;
        private Action<int>? onSelected;
        private int selected;

        internal QwTabs(QwContainer parent, string[] labels, bool navRail)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject(navRail ? "NavRail" : "Tabs"))
        {
            vertical = navRail;
            if (vertical)
            {
                QwLayout.ApplyColumn(Go, QwGap.Xs, QwGap.None);
            }
            else
            {
                QwLayout.ApplyRow(Go, QwGap.Xs, QwGap.None);
                this.FixedHeight(QwTokens.ControlHeight);
            }

            for (var index = 0; index < labels.Length; index++)
            {
                var captured = index;
                var itemGo = new GameObject("Tab_" + labels[index], typeof(RectTransform));
                itemGo.transform.SetParent(Go.transform, false);
                var itemLayout = itemGo.AddComponent<LayoutElement>();
                itemLayout.minHeight = QwTokens.ControlHeight;
                itemLayout.preferredHeight = QwTokens.ControlHeight;
                if (!vertical)
                {
                    itemLayout.flexibleWidth = 1f;
                }

                var fill = itemGo.AddComponent<UImage>();
                fill.sprite = QwSprites.Fill(QwRadius.Control);
                fill.type = UImage.Type.Sliced;

                var ringGo = new GameObject("Ring", typeof(RectTransform));
                ringGo.transform.SetParent(itemGo.transform, false);
                var ring = ringGo.AddComponent<UImage>();
                ring.sprite = QwSprites.Ring(QwRadius.Control, QwTokens.BorderStandard);
                ring.type = UImage.Type.Sliced;
                ring.raycastTarget = false;
                QwAnchors.Stretch((RectTransform)ringGo.transform);

                var labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(itemGo.transform, false);
                var label = QwTmp.Create(labelGo);
                label.fontSize = QwTokens.LabelSize;
                label.alignment = vertical ? TextAlignmentOptions.Left : TextAlignmentOptions.Center;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                var font = QwFonts.For(QwTextStyle.Label);
                if (font != null)
                {
                    label.font = font;
                }

                if (QwFonts.UseFauxBold)
                {
                    label.fontStyle = FontStyles.Bold;
                }

                label.text = labels[index];
                QwAnchors.Stretch((RectTransform)labelGo.transform, vertical ? 14f : 8f, 2f, 8f, 2f);

                var button = itemGo.AddComponent<Button>();
                button.targetGraphic = fill;
                button.onClick.AddListener(() => Select(captured));

                items.Add(new TabItem(fill, ring, label, button));
            }

            Repaint();
        }

        public int Selected => selected;

        public QwTabs OnSelected(Action<int> handler)
        {
            onSelected = handler;
            return this;
        }

        /// <summary>Selects a tab, repaints, and notifies (dirty-checked).</summary>
        public void Select(int index)
        {
            if (index < 0 || index >= items.Count)
            {
                return;
            }

            if (selected != index)
            {
                selected = index;
                Repaint();
            }

            QwCallbacks.Invoke(onSelected, index, "Tab selection");
        }

        /// <summary>Moves selection by a delta (keyboard Up/Down / Ctrl+Tab cycling).</summary>
        public void Cycle(int delta)
        {
            var count = items.Count;
            if (count == 0)
            {
                return;
            }

            Select(((selected + delta) % count + count) % count);
        }

        public void ApplyTheme(QwResolvedTheme theme)
        {
            Repaint();
        }

        private void Repaint()
        {
            var theme = Theme;
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var isSelected = index == selected;
                item.Fill.color = isSelected ? theme.SelectedTint : theme.SurfaceSunken;
                item.Ring.color = isSelected ? theme.OutlineStrong : Color.clear;
                item.Label.color = isSelected ? theme.Text : theme.TextMuted;
            }
        }
    }
}
