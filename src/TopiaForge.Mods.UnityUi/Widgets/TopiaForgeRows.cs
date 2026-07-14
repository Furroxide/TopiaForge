using System;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Section header: heading label over a brand divider.</summary>
    public sealed class TopiaForgeSectionHeader : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly TopiaForgeLabel heading;
        private readonly TopiaForgeImage divider;

        internal TopiaForgeSectionHeader(TopiaForgeContainer parent, string title)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Section"))
        {
            TopiaForgeLayout.ApplyColumn(Go, TopiaForgeGap.Xs, TopiaForgeGap.None);
            var container = new TopiaForgeContainer(Host, Scheme, Go);
            heading = container.Label(title, TopiaForgeTextStyle.Heading);
            divider = container.Divider();
            this.FixedHeight(TopiaForgeTokens.HeadingSize + 12f);
        }

        public void SetTitle(string title)
        {
            heading.SetText(title);
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            divider.SetColor(theme.Tint);
        }
    }

    /// <summary>Dense key/value row for settings and diagnostics surfaces.</summary>
    public sealed class TopiaForgeKeyValueRow : TopiaForgeWidget
    {
        private readonly TopiaForgeLabel valueLabel;

        internal TopiaForgeKeyValueRow(TopiaForgeContainer parent, string key, string value)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("KeyValue"))
        {
            TopiaForgeLayout.ApplyRow(Go, TopiaForgeGap.Sm, TopiaForgeGap.None);
            this.FixedHeight(24f);
            var container = new TopiaForgeContainer(Host, Scheme, Go);
            container.Label(key, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedWidth(170f);
            valueLabel = container.Label(value, TopiaForgeTextStyle.Body);
            valueLabel.Flex(1f, 0f);
        }

        public void SetValue(string value)
        {
            valueLabel.SetText(value);
        }
    }

    /// <summary>
    /// Selectable list row: title + subtitle + trailing badge with owned selection
    /// visuals (SelectedTint fill + strong ring). The pooled unit for TopiaForgeListView and
    /// usable standalone in static lists.
    /// </summary>
    public sealed class TopiaForgeListRow : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly UImage fill;
        private readonly UImage ring;
        private Action? onClick;
        private bool selected;

        internal TopiaForgeListRow(TopiaForgeContainer parent)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("ListRow"))
        {
            fill = Go.AddComponent<UImage>();
            fill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
            fill.type = UImage.Type.Sliced;

            var ringGo = new GameObject("Ring", typeof(RectTransform));
            ringGo.transform.SetParent(Go.transform, false);
            ring = ringGo.AddComponent<UImage>();
            ring.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Chip, TopiaForgeTokens.BorderStandard);
            ring.type = UImage.Type.Sliced;
            ring.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)ringGo.transform);
            var ringLayout = ringGo.AddComponent<LayoutElement>();
            ringLayout.ignoreLayout = true;

            TopiaForgeLayout.ApplyRow(Go, TopiaForgeGap.Sm, TopiaForgeGap.Sm);
            this.FixedHeight(TopiaForgeTokens.ListRowHeight);

            var content = new TopiaForgeContainer(Host, Scheme, Go);
            Title = content.Label(string.Empty, TopiaForgeTextStyle.Body);
            Title.Flex(1f, 0f);
            Subtitle = content.Label(string.Empty, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            Badge = content.Badge(string.Empty, TopiaForgeTone.Neutral);

            var button = Go.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(() => TopiaForgeCallbacks.Invoke(onClick, "List row click"));

            ApplyTheme(Theme);
        }

        public TopiaForgeLabel Title { get; }

        public TopiaForgeLabel Subtitle { get; }

        public TopiaForgeBadge Badge { get; }

        public bool Selected => selected;

        public TopiaForgeListRow OnClick(Action handler)
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

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            fill.color = selected ? theme.SelectedTint : theme.SurfaceSunken;
            ring.color = selected ? theme.OutlineStrong : Color.clear;
        }
    }
}
