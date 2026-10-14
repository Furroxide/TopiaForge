using System;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A handle to one tracked name tag. Tags are pooled, so a handle goes stale once its tag is
    /// removed: every call on a stale handle is ignored and <see cref="IsTracked"/> turns false,
    /// even after the pooled tag is reused for someone else. Setters dirty-check.
    /// </summary>
    public sealed class TopiaForgeNametag
    {
        private readonly TopiaForgeNametagLayer layer;
        private readonly int slot;
        private readonly int generation;

        internal TopiaForgeNametag(TopiaForgeNametagLayer layer, int slot, int generation)
        {
            this.layer = layer;
            this.slot = slot;
            this.generation = generation;
        }

        /// <summary>Gets whether the tag is still tracked by its layer.</summary>
        public bool IsTracked => layer.IsCurrent(slot, generation);

        /// <summary>Sets the name line (sanitized plain text, at most 32 characters).</summary>
        public void SetName(string name)
        {
            if (IsTracked)
            {
                layer.SlotAt(slot).SetName(name);
            }
        }

        /// <summary>Sets the secondary line ("Reconnecting…", "Spectating"); empty hides it.</summary>
        public void SetSecondary(string text)
        {
            if (IsTracked)
            {
                layer.SlotAt(slot).SetSecondary(text);
            }
        }

        /// <summary>Sets a badge beside the name ("HOST"); empty hides it.</summary>
        public void SetBadge(string text, TopiaForgeTone tone = TopiaForgeTone.Accent)
        {
            if (IsTracked)
            {
                layer.SlotAt(slot).SetBadge(text, tone);
            }
        }

        /// <summary>Sets the talking glyph slot; idle hides it.</summary>
        public void SetTalkState(TopiaForgeTalkState state)
        {
            if (IsTracked)
            {
                layer.SlotAt(slot).Talk.SetState(state);
            }
        }

        /// <summary>Sets the name's semantic tone.</summary>
        public void SetTone(TopiaForgeTone tone)
        {
            if (IsTracked)
            {
                layer.SlotAt(slot).Name.SetTone(tone);
            }
        }

        /// <summary>Sets how far above its anchor the tag sits, in meters.</summary>
        public void SetVerticalOffset(float meters)
        {
            if (IsTracked)
            {
                layer.SlotAt(slot).VerticalOffset = float.IsNaN(meters) || float.IsInfinity(meters) ? 0f : meters;
            }
        }

        /// <summary>Re-targets the tag to another anchor.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="anchor"/> is null.</exception>
        public void SetAnchor(ITopiaForgeNametagAnchor anchor)
        {
            if (anchor == null)
            {
                throw new ArgumentNullException(nameof(anchor));
            }

            if (IsTracked)
            {
                layer.SlotAt(slot).Anchor = anchor;
            }
        }

        /// <summary>Stops tracking and returns the tag to the pool; later calls are ignored.</summary>
        public void Remove()
        {
            if (IsTracked)
            {
                layer.Release(slot);
            }
        }
    }

    /// <summary>One pooled tag's visuals and tracking state.</summary>
    internal sealed class TopiaForgeNametagSlot
    {
        private const int MaxNameChars = 32;
        private const int MaxSecondaryChars = 48;
        private const int MaxBadgeChars = 12;

        private readonly RectTransform root;
        private readonly CanvasGroup group;
        private readonly UImage backing;
        private readonly TopiaForgeLabel secondary;
        private readonly TopiaForgeBadge badge;
        private string? rawName;
        private string? rawSecondary;
        private string? rawBadge;
        private TopiaForgeTone badgeTone = TopiaForgeTone.Accent;
        private Vector2 shownPosition = new Vector2(float.NaN, float.NaN);
        private float shownScale = -1f;
        private float shownAlpha = -1f;

        internal TopiaForgeNametagSlot(TopiaForgeContainer layer, int index)
        {
            var go = layer.CreateChildGameObject("Tag" + index);
            root = (RectTransform)go.transform;
            root.anchorMin = new Vector2(0.5f, 0.5f);
            root.anchorMax = new Vector2(0.5f, 0.5f);
            root.pivot = new Vector2(0.5f, 0f);
            group = go.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            var backingGo = new GameObject("Backing", typeof(RectTransform));
            backingGo.transform.SetParent(go.transform, false);
            backing = backingGo.AddComponent<UImage>();
            backing.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Tip);
            backing.type = UImage.Type.Sliced;
            backing.raycastTarget = false;
            backingGo.AddComponent<LayoutElement>().ignoreLayout = true;
            TopiaForgeAnchors.Stretch((RectTransform)backingGo.transform);

            var stack = go.AddComponent<VerticalLayoutGroup>();
            stack.padding = new RectOffset(8, 8, 3, 4);
            stack.childAlignment = TextAnchor.UpperCenter;
            stack.childControlWidth = true;
            stack.childControlHeight = true;
            stack.childForceExpandWidth = false;
            stack.childForceExpandHeight = false;
            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var content = new TopiaForgeContainer(layer.Host, layer.Scheme, go);
            var head = content.Row(TopiaForgeGap.Xs);
            head.Go.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            Talk = head.TalkIndicator(16f).HideWhenIdle();
            Name = head.Label(string.Empty, TopiaForgeTextStyle.Label).NoWrap().PlainText();
            badge = head.Badge(string.Empty, badgeTone).PlainText();
            badge.Go.SetActive(false);
            secondary = content.Label(TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).NoWrap().PlainText();
            secondary.Go.SetActive(false);
            go.SetActive(false);
        }

        public bool Active { get; private set; }

        public int Generation { get; private set; }

        public ITopiaForgeNametagAnchor? Anchor { get; set; }

        public float VerticalOffset { get; set; }

        public TopiaForgeLabel Name { get; }

        public TopiaForgeTalkIndicator Talk { get; }

        public void Activate(ITopiaForgeNametagAnchor anchor, float verticalOffset)
        {
            Active = true;
            unchecked
            {
                Generation++;
            }

            Anchor = anchor;
            VerticalOffset = float.IsNaN(verticalOffset) || float.IsInfinity(verticalOffset) ? 0f : verticalOffset;
            SetSecondary(string.Empty);
            SetBadge(string.Empty, TopiaForgeTone.Accent);
            Talk.SetState(TopiaForgeTalkState.Idle);
            Name.SetTone(TopiaForgeTone.Neutral);
            Hide();
            root.gameObject.SetActive(true);
        }

        public void Deactivate()
        {
            Active = false;
            Anchor = null;
            root.gameObject.SetActive(false);
        }

        public void SetName(string? value)
        {
            if (string.Equals(value, rawName, StringComparison.Ordinal))
            {
                return;
            }

            rawName = value;
            Name.SetText(TopiaForgePlainText.Sanitize(value, MaxNameChars));
        }

        public void SetSecondary(string? value)
        {
            if (string.Equals(value, rawSecondary, StringComparison.Ordinal))
            {
                return;
            }

            rawSecondary = value;
            var text = TopiaForgePlainText.Sanitize(value, MaxSecondaryChars);
            secondary.SetText(text);
            if (secondary.Go.activeSelf != text.Length > 0)
            {
                secondary.Go.SetActive(text.Length > 0);
            }
        }

        public void SetBadge(string? value, TopiaForgeTone tone)
        {
            if (string.Equals(value, rawBadge, StringComparison.Ordinal) && tone == badgeTone)
            {
                return;
            }

            rawBadge = value;
            badgeTone = tone;
            var text = TopiaForgePlainText.Sanitize(value, MaxBadgeChars);
            if (text.Length > 0)
            {
                badge.Set(text, tone);
            }

            if (badge.Go.activeSelf != text.Length > 0)
            {
                badge.Go.SetActive(text.Length > 0);
            }
        }

        public void Show(Vector2 local, float scale, float alpha)
        {
            if (Mathf.Abs(local.x - shownPosition.x) > 0.05f || Mathf.Abs(local.y - shownPosition.y) > 0.05f || float.IsNaN(shownPosition.x))
            {
                shownPosition = local;
                root.anchoredPosition = local;
            }

            if (Mathf.Abs(scale - shownScale) > 0.002f)
            {
                shownScale = scale;
                root.localScale = new Vector3(scale, scale, 1f);
            }

            if (Mathf.Abs(alpha - shownAlpha) > 0.004f)
            {
                shownAlpha = alpha;
                group.alpha = alpha;
            }
        }

        public void Hide()
        {
            if (shownAlpha != 0f)
            {
                shownAlpha = 0f;
                group.alpha = 0f;
            }
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            var color = theme.Surface;
            if (!theme.HighContrast)
            {
                color.a *= 0.8f;
            }

            backing.color = color;
        }
    }
}
