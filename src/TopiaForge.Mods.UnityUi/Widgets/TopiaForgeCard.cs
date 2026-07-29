using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A clickable preview card for grids and galleries: a square preview area (any
    /// Texture, or a placeholder icon while one is pending), a caption, and an optional
    /// corner badge. Hover brightens the fill and strengthens the ring; press reuses the
    /// button's sticker motion. Chain <c>.Tooltip("…")</c> for hover details.
    ///
    /// Pooling note: <see cref="TopiaForgeWidget.SetVisible"/> hides via CanvasGroup, so a
    /// hidden card still occupies its grid cell. Grid consumers must toggle
    /// <c>card.Go.SetActive(...)</c> instead — GridLayoutGroup skips inactive children.
    /// </summary>
    public sealed class TopiaForgeCard : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly Button button;
        private readonly UImage shadow;
        private readonly UImage fill;
        private readonly UImage ring;
        private readonly UImage previewFrame;
        private readonly RawImage preview;
        private readonly UImage placeholder;
        private readonly TextMeshProUGUI caption;
        private readonly UImage badgeFill;
        private readonly UImage badgeRing;
        private readonly TextMeshProUGUI badgeLabel;
        private readonly RectTransform badgeRect;

        private string lastTitle;
        private string lastBadgeText = string.Empty;
        private TopiaForgeTone badgeTone = TopiaForgeTone.Neutral;
        private Texture? previewTexture;
        private TopiaForgeIcon placeholderIcon = TopiaForgeIcon.Grip;
        private bool selected;
        private bool hovered;
        private bool enabledState = true;

        internal TopiaForgeCard(TopiaForgeContainer parent, string title, Action onClick)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Card"))
        {
            lastTitle = title;

            // Shadow sits outside the body so the press effect can push onto it.
            shadow = CreateStretched(Go.transform, "Shadow", TopiaForgeSprites.Fill(TopiaForgeRadius.Control), sliced: true);
            var shadowRect = shadow.rectTransform;
            shadowRect.offsetMin = new Vector2(TopiaForgeTokens.ShadowSmallX, TopiaForgeTokens.ShadowSmallY);
            shadowRect.offsetMax = new Vector2(TopiaForgeTokens.ShadowSmallX, TopiaForgeTokens.ShadowSmallY);

            var bodyGo = new GameObject("Body", typeof(RectTransform));
            bodyGo.transform.SetParent(Go.transform, false);
            var body = (RectTransform)bodyGo.transform;
            TopiaForgeAnchors.Stretch(body);

            fill = CreateStretched(body, "Fill", TopiaForgeSprites.Fill(TopiaForgeRadius.Control), sliced: true);
            fill.raycastTarget = true;
            ring = CreateStretched(body, "Ring", TopiaForgeSprites.Ring(TopiaForgeRadius.Control, TopiaForgeTokens.BorderStandard), sliced: true);

            // Preview area: sunken frame filling the card above the caption strip.
            var frameGo = new GameObject("PreviewFrame", typeof(RectTransform));
            frameGo.transform.SetParent(body, false);
            previewFrame = frameGo.AddComponent<UImage>();
            previewFrame.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
            previewFrame.type = UImage.Type.Sliced;
            previewFrame.raycastTarget = false;
            var frameRect = (RectTransform)frameGo.transform;
            frameRect.anchorMin = new Vector2(0f, 0f);
            frameRect.anchorMax = new Vector2(1f, 1f);
            frameRect.offsetMin = new Vector2(6f, CaptionBandHeight);
            frameRect.offsetMax = new Vector2(-6f, -6f);

            var previewGo = new GameObject("Preview", typeof(RectTransform));
            previewGo.transform.SetParent(frameGo.transform, false);
            preview = previewGo.AddComponent<RawImage>();
            preview.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)previewGo.transform, 2f, 2f, 2f, 2f);
            previewGo.SetActive(false);

            var placeholderGo = new GameObject("Placeholder", typeof(RectTransform));
            placeholderGo.transform.SetParent(frameGo.transform, false);
            placeholder = placeholderGo.AddComponent<UImage>();
            placeholder.sprite = TopiaForgeSprites.Icon(placeholderIcon);
            placeholder.raycastTarget = false;
            var placeholderRect = (RectTransform)placeholderGo.transform;
            placeholderRect.anchorMin = new Vector2(0.5f, 0.5f);
            placeholderRect.anchorMax = new Vector2(0.5f, 0.5f);
            placeholderRect.sizeDelta = new Vector2(28f, 28f);

            // Caption strip along the bottom edge.
            var captionGo = new GameObject("Caption", typeof(RectTransform));
            captionGo.transform.SetParent(body, false);
            caption = TopiaForgeTmp.Create(captionGo);
            caption.fontSize = TopiaForgeTokens.CaptionSize;
            caption.alignment = TextAlignmentOptions.Center;
            caption.textWrappingMode = TextWrappingModes.NoWrap;
            caption.overflowMode = TextOverflowModes.Ellipsis;
            var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Label);
            if (font != null)
            {
                caption.font = font;
            }

            caption.text = title;
            var captionRect = (RectTransform)captionGo.transform;
            captionRect.anchorMin = new Vector2(0f, 0f);
            captionRect.anchorMax = new Vector2(1f, 0f);
            captionRect.pivot = new Vector2(0.5f, 0f);
            captionRect.offsetMin = new Vector2(8f, 4f);
            captionRect.offsetMax = new Vector2(-8f, CaptionBandHeight - 6f);

            // Corner badge chip (hidden until SetBadge).
            var badgeGo = new GameObject("Badge", typeof(RectTransform));
            badgeGo.transform.SetParent(body, false);
            badgeRect = (RectTransform)badgeGo.transform;
            badgeRect.anchorMin = new Vector2(1f, 1f);
            badgeRect.anchorMax = new Vector2(1f, 1f);
            badgeRect.pivot = new Vector2(1f, 1f);
            badgeRect.anchoredPosition = new Vector2(-8f, -8f);
            badgeRect.sizeDelta = new Vector2(38f, 16f);
            badgeFill = badgeGo.AddComponent<UImage>();
            badgeFill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
            badgeFill.type = UImage.Type.Sliced;
            badgeFill.raycastTarget = false;

            var badgeRingGo = new GameObject("Ring", typeof(RectTransform));
            badgeRingGo.transform.SetParent(badgeGo.transform, false);
            badgeRing = badgeRingGo.AddComponent<UImage>();
            badgeRing.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Chip, TopiaForgeTokens.BorderHairline);
            badgeRing.type = UImage.Type.Sliced;
            badgeRing.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)badgeRingGo.transform);

            var badgeLabelGo = new GameObject("Label", typeof(RectTransform));
            badgeLabelGo.transform.SetParent(badgeGo.transform, false);
            badgeLabel = TopiaForgeTmp.Create(badgeLabelGo);
            badgeLabel.fontSize = 9f;
            badgeLabel.alignment = TextAlignmentOptions.Center;
            badgeLabel.textWrappingMode = TextWrappingModes.NoWrap;
            if (font != null)
            {
                badgeLabel.font = font;
            }

            TopiaForgeAnchors.Stretch((RectTransform)badgeLabelGo.transform, 4f, 1f, 4f, 1f);
            badgeGo.SetActive(false);

            button = Go.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(() => TopiaForgeCallbacks.Invoke(onClick, "Card click"));

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.06f, 1.06f, 1.06f, 1f);
            colors.pressedColor = new Color(0.94f, 0.94f, 0.94f, 1f);
            colors.selectedColor = new Color(1.04f, 1.04f, 1.04f, 1f);
            colors.disabledColor = Color.white; // disabled visuals are theme-driven, not multiplied
            button.colors = colors;

            var press = Go.AddComponent<TopiaForgePressEffect>();
            press.Initialize(Host, body, shadow);

            var hover = Go.AddComponent<TopiaForgeHoverNotifier>();
            hover.Changed = value =>
            {
                hovered = value;
                ApplyTheme(Theme);
            };

            ApplyTheme(Theme);
        }

        private const float CaptionBandHeight = 26f;

        /// <summary>Dirty-checked caption update.</summary>
        public void SetTitle(string value)
        {
            if (string.Equals(lastTitle, value, StringComparison.Ordinal))
            {
                return;
            }

            lastTitle = value;
            caption.text = value;
        }

        /// <summary>Dirty-checked corner badge; empty text hides the chip.</summary>
        public void SetBadge(string text, TopiaForgeTone tone)
        {
            var show = !string.IsNullOrEmpty(text);
            if (badgeRect.gameObject.activeSelf != show)
            {
                badgeRect.gameObject.SetActive(show);
            }

            if (!show)
            {
                return;
            }

            var changed = false;
            if (!string.Equals(lastBadgeText, text, StringComparison.Ordinal))
            {
                lastBadgeText = text;
                badgeLabel.text = text;
                var width = badgeLabel.GetPreferredValues(text).x + 10f;
                badgeRect.sizeDelta = new Vector2(width, badgeRect.sizeDelta.y);
                changed = true;
            }

            if (badgeTone != tone)
            {
                badgeTone = tone;
                changed = true;
            }

            if (changed)
            {
                ApplyTheme(Theme);
            }
        }

        /// <summary>
        /// Dirty-checked preview swap. A texture replaces the placeholder icon; null
        /// falls back to it. The card does not own the texture's lifetime.
        /// </summary>
        public void SetPreviewTexture(Texture? texture)
        {
            if (ReferenceEquals(previewTexture, texture))
            {
                return;
            }

            previewTexture = texture;
            preview.texture = texture;
            preview.gameObject.SetActive(texture != null);
            placeholder.gameObject.SetActive(texture == null);
        }

        /// <summary>Dirty-checked placeholder icon shown while no texture is set.</summary>
        public void SetPreviewIcon(TopiaForgeIcon icon)
        {
            if (placeholderIcon == icon)
            {
                return;
            }

            placeholderIcon = icon;
            placeholder.sprite = TopiaForgeSprites.Icon(icon);
        }

        /// <summary>Dirty-checked selection ring.</summary>
        public void SetSelected(bool value)
        {
            if (selected == value)
            {
                return;
            }

            selected = value;
            ApplyTheme(Theme);
        }

        /// <summary>Dirty-checked interactability + disabled visuals.</summary>
        public void SetEnabled(bool value)
        {
            if (enabledState == value)
            {
                return;
            }

            enabledState = value;
            button.interactable = value;
            ApplyTheme(Theme);
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            if (!enabledState)
            {
                fill.color = theme.Tint;
                ring.color = theme.Tint;
                caption.color = theme.TextFaint;
                placeholder.color = theme.TextFaint;
                shadow.color = Color.clear;
            }
            else
            {
                fill.color = theme.Surface;
                ring.color = selected || hovered ? theme.OutlineStrong : theme.Outline;
                caption.color = theme.Text;
                placeholder.color = theme.TextFaint;
                shadow.color = theme.Shadow;
            }

            previewFrame.color = theme.SurfaceSunken;

            var accent = theme.ToneColor(badgeTone);
            var background = accent;
            background.a = Scheme == TopiaForgeScheme.Paper ? 0.14f : 0.22f;
            badgeFill.color = background;
            badgeRing.color = accent;
            badgeLabel.color = badgeTone == TopiaForgeTone.Neutral ? theme.TextMuted : accent;
        }

        private static UImage CreateStretched(Transform parent, string name, Sprite sprite, bool sliced)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = sliced ? UImage.Type.Sliced : UImage.Type.Simple;
            image.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            return image;
        }
    }

    /// <summary>Forwards pointer enter/exit to a widget-side callback.</summary>
    internal sealed class TopiaForgeHoverNotifier : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public Action<bool>? Changed;

        public void OnPointerEnter(PointerEventData eventData)
        {
            TopiaForgeCallbacks.Invoke(Changed, true, "Card hover");
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            TopiaForgeCallbacks.Invoke(Changed, false, "Card hover");
        }
    }
}
