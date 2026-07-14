using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Panel chrome presets from the brand shape language.</summary>
    public enum TopiaForgePanelStyle
    {
        /// <summary>Surface fill only (radius 18).</summary>
        Plain,

        /// <summary>The launcher card: radius 26, strong orange border, hard card shadow.</summary>
        Card,

        /// <summary>Gameplay overlay panel: radius 18, subtle border, small hard shadow.</summary>
        HudPanel,

        /// <summary>Recessed area (list/scroll backgrounds): sunken surface, no shadow.</summary>
        Sunken,
    }

    /// <summary>
    /// A container with brand chrome: 9-sliced rounded fill, optional border ring, and
    /// the hard offset shadow rendered as a sibling image behind the fill (9-slice-safe
    /// and retintable per scheme). Decorative children ignore the panel's layout group.
    /// </summary>
    public sealed class TopiaForgePanel : TopiaForgeContainer, ITopiaForgeThemeAware
    {
        private readonly TopiaForgePanelStyle style;
        private readonly UImage? shadow;
        private readonly UImage fill;
        private readonly UImage? ring;

        internal TopiaForgePanel(TopiaForgeContainer parent, TopiaForgePanelStyle panelStyle)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Panel"))
        {
            style = panelStyle;
            var radius = style == TopiaForgePanelStyle.Card ? TopiaForgeRadius.Card : TopiaForgeRadius.Control;

            if (style == TopiaForgePanelStyle.Card || style == TopiaForgePanelStyle.HudPanel)
            {
                var offsetX = style == TopiaForgePanelStyle.Card ? TopiaForgeTokens.ShadowCardX : TopiaForgeTokens.ShadowSmallX;
                var offsetY = style == TopiaForgePanelStyle.Card ? TopiaForgeTokens.ShadowCardY : TopiaForgeTokens.ShadowSmallY;
                shadow = CreateDecor("Shadow", TopiaForgeSprites.Fill(radius));
                var shadowRect = shadow.rectTransform;
                shadowRect.offsetMin = new Vector2(offsetX, offsetY);
                shadowRect.offsetMax = new Vector2(offsetX, offsetY);
            }

            fill = CreateDecor("Fill", TopiaForgeSprites.Fill(radius));
            fill.raycastTarget = true; // panels block clicks from falling through to the game/HUD behind

            if (style == TopiaForgePanelStyle.Card || style == TopiaForgePanelStyle.HudPanel)
            {
                var thickness = style == TopiaForgePanelStyle.Card ? TopiaForgeTokens.BorderStandard : TopiaForgeTokens.BorderStandard;
                ring = CreateDecor("Ring", TopiaForgeSprites.Ring(radius, thickness));
            }

            ApplyTheme(Theme);
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            switch (style)
            {
                case TopiaForgePanelStyle.Card:
                    fill.color = theme.Surface;
                    if (ring != null)
                    {
                        ring.color = theme.OutlineStrong;
                    }

                    if (shadow != null)
                    {
                        shadow.color = theme.ShadowStrong;
                    }

                    break;
                case TopiaForgePanelStyle.HudPanel:
                    fill.color = theme.Surface;
                    if (ring != null)
                    {
                        ring.color = theme.Outline;
                    }

                    if (shadow != null)
                    {
                        shadow.color = theme.Shadow;
                    }

                    break;
                case TopiaForgePanelStyle.Sunken:
                    fill.color = theme.SurfaceSunken;
                    break;
                default:
                    fill.color = theme.Surface;
                    break;
            }
        }

        private UImage CreateDecor(string name, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = UImage.Type.Sliced;
            image.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            var layout = go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            return image;
        }
    }
}
