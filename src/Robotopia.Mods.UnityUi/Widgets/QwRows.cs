using System;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Section header: heading label over a brand divider.</summary>
    public sealed class QwSectionHeader : QwWidget, IQwThemeAware
    {
        private readonly QwLabel heading;
        private readonly QwImage divider;

        internal QwSectionHeader(QwContainer parent, string title)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Section"))
        {
            QwLayout.ApplyColumn(Go, QwGap.Xs, QwGap.None);
            var container = new QwContainer(Host, Scheme, Go);
            heading = container.Label(title, QwTextStyle.Heading);
            divider = container.Divider();
            this.FixedHeight(QwTokens.HeadingSize + 12f);
        }

        public void SetTitle(string title)
        {
            heading.SetText(title);
        }

        public void ApplyTheme(QwResolvedTheme theme)
        {
            divider.SetColor(theme.Tint);
        }
    }

    /// <summary>Dense key/value row for settings and diagnostics surfaces.</summary>
    public sealed class QwKeyValueRow : QwWidget
    {
        private readonly QwLabel valueLabel;

        internal QwKeyValueRow(QwContainer parent, string key, string value)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("KeyValue"))
        {
            QwLayout.ApplyRow(Go, QwGap.Sm, QwGap.None);
            this.FixedHeight(24f);
            var container = new QwContainer(Host, Scheme, Go);
            container.Label(key, QwTextStyle.Caption).Tone(QwTone.Muted).FixedWidth(170f);
            valueLabel = container.Label(value, QwTextStyle.Body);
            valueLabel.Flex(1f, 0f);
        }

        public void SetValue(string value)
        {
            valueLabel.SetText(value);
        }
    }

    /// <summary>
    /// Selectable list row: title + subtitle + trailing badge with owned selection
    /// visuals (SelectedTint fill + strong ring). The pooled unit for QwListView and
    /// usable standalone in static lists.
    /// </summary>
    public sealed class QwListRow : QwWidget, IQwThemeAware
    {
        private readonly UImage fill;
        private readonly UImage ring;
        private Action? onClick;
        private bool selected;

        internal QwListRow(QwContainer parent)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("ListRow"))
        {
            fill = Go.AddComponent<UImage>();
            fill.sprite = QwSprites.Fill(QwRadius.Chip);
            fill.type = UImage.Type.Sliced;

            var ringGo = new GameObject("Ring", typeof(RectTransform));
            ringGo.transform.SetParent(Go.transform, false);
            ring = ringGo.AddComponent<UImage>();
            ring.sprite = QwSprites.Ring(QwRadius.Chip, QwTokens.BorderStandard);
            ring.type = UImage.Type.Sliced;
            ring.raycastTarget = false;
            QwAnchors.Stretch((RectTransform)ringGo.transform);
            var ringLayout = ringGo.AddComponent<LayoutElement>();
            ringLayout.ignoreLayout = true;

            QwLayout.ApplyRow(Go, QwGap.Sm, QwGap.Sm);
            this.FixedHeight(QwTokens.ListRowHeight);

            var content = new QwContainer(Host, Scheme, Go);
            Title = content.Label(string.Empty, QwTextStyle.Body);
            Title.Flex(1f, 0f);
            Subtitle = content.Label(string.Empty, QwTextStyle.Caption).Tone(QwTone.Muted);
            Badge = content.Badge(string.Empty, QwTone.Neutral);

            var button = Go.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(() => onClick?.Invoke());

            ApplyTheme(Theme);
        }

        public QwLabel Title { get; }

        public QwLabel Subtitle { get; }

        public QwBadge Badge { get; }

        public bool Selected => selected;

        public QwListRow OnClick(Action handler)
        {
            onClick = handler;
            return this;
        }

        /// <summary>Dirty-checked selection visuals.</summary>
        public void SetSelected(bool value)
        {
            if (selected == value)
            {
                return;
            }

            selected = value;
            ApplyTheme(Theme);
        }

        public void ApplyTheme(QwResolvedTheme theme)
        {
            fill.color = selected ? theme.SelectedTint : theme.SurfaceSunken;
            ring.color = selected ? theme.OutlineStrong : Color.clear;
        }
    }
}
