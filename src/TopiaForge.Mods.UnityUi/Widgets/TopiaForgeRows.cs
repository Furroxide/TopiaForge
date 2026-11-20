using System;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

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

        /// <summary>Sets the title.</summary>
        public void SetTitle(string title)
        {
            heading.SetText(title);
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
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

        /// <summary>Sets the value.</summary>
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
    public sealed partial class TopiaForgeListRow : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly TopiaForgeRoundedImage fill;
        private readonly TopiaForgeRoundedImage ring;
        private readonly Button button;
        private TopiaForgeControlCues? cues;
        private Action? onClick;
        private Action<UiPaintRole>? setFocusBackdrop;
        private bool selected;
        private bool declarative;

        internal TopiaForgeListRow(TopiaForgeContainer parent)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("ListRow"))
        {
            fill = Go.AddComponent<TopiaForgeRoundedImage>();
            fill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
            fill.type = Image.Type.Sliced;

            var ringGo = new GameObject("Ring", typeof(RectTransform));
            ringGo.transform.SetParent(Go.transform, false);
            ring = ringGo.AddComponent<TopiaForgeRoundedImage>();
            ring.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Chip, TopiaForgeTokens.BorderStandard);
            ring.type = Image.Type.Sliced;
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

            button = Go.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(() => TopiaForgeCallbacks.Player(Host.OwnerId, onClick, "List row click"));

            ApplyTheme(Theme);
        }

        /// <summary>Gets title.</summary>
        public TopiaForgeLabel Title { get; }

        /// <summary>Gets subtitle.</summary>
        public TopiaForgeLabel Subtitle { get; }

        /// <summary>Gets badge.</summary>
        public TopiaForgeBadge Badge { get; }

        /// <summary>Gets selected.</summary>
        public bool Selected => selected;

        /// <summary>Registers the callback invoked when the row is clicked.</summary>
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

        internal void UseDeclarativePaint(bool enabled, UiPaintRole backdrop)
        {
            declarative = true;
            Title.Go.name = "RowTitle";
            Subtitle.Go.name = "RowSubtitle";
            ConfigureDeclarativeText();
            cues ??= TopiaForgeControlCues.Install(button, Rect);
            button.interactable = enabled;
            setFocusBackdrop = TopiaForgeControlFocus.Install(this, button, backdrop);
            ApplyTheme(Theme);
        }

        internal void SetDeclarativeBackdrop(UiPaintRole role) => setFocusBackdrop?.Invoke(role);

        internal void SetDeclarativeEnabled(bool enabled)
        {
            button.interactable = enabled;
            ApplyTheme(Theme);
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            var role = UiControlPaint.ListRowBackground(button.interactable, selected);
            if (declarative && theme.FieldPaint(role, UiTone.Neutral, button.interactable) is { } paint)
            {
                fill.SetShape(theme.Descriptor?.Shape.Small);
                ring.SetShape(theme.Descriptor?.Shape.Small,
                    theme.HighContrast ? TopiaForgeTokens.BorderStrong : TopiaForgeTokens.BorderStandard);
                button.colors = paint.ResolvedStates(button.colors);
                fill.color = paint.Fill;
                ring.color = selected ? theme.BoundaryOn(role) : Color.clear;
                Title.SetResolvedInk(paint.Content);
                Subtitle.SetResolvedInk(paint.Content);
                Badge.SetRowBoundary(paint.Content);
                cues?.Apply(fill.color, paint.Content,
                    theme.Descriptor?.Shape.Small ?? UiThemePackParser.Default.Shape.Small, theme.HighContrast);
                return;
            }

            fill.color = selected ? theme.SelectedTint : theme.SurfaceSunken;
            ring.color = selected ? theme.OutlineStrong : Color.clear;
        }
    }
}
