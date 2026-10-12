using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Talking glyph for idle, talking, muted, muted by host, mic busy and no mic. Every state
    /// differs in glyph or halo (<see cref="TopiaForgeTalkPresentation"/>), so it reads without
    /// color, and an optional caption spells the state out. While talking, the halo breathes
    /// subtly in proportion to the host's motion intensity and holds still under reduced motion.
    /// The animation driver runs only while a halo is actually breathing.
    /// </summary>
    public sealed class TopiaForgeTalkIndicator : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly UImage backing;
        private readonly UImage ring;
        private readonly RectTransform ringRect;
        private readonly UImage icon;
        private readonly Sprite microphone;
        private readonly Sprite microphoneOff;
        private readonly Sprite busy;
        private readonly Sprite noDevice;
        private readonly TopiaForgeContainer content;
        private readonly TopiaForgeTalkIndicatorDriver driver;
        private TopiaForgeLabel? caption;
        private TopiaForgeTalkState state = TopiaForgeTalkState.Idle;
        private bool hideWhenIdle;
        private float shownScale = -1f;
        private float shownAlpha = -1f;

        internal TopiaForgeTalkIndicator(TopiaForgeContainer parent, float size)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("TalkIndicator"))
        {
            size = float.IsNaN(size) ? 20f : Mathf.Clamp(size, 12f, 64f);
            TopiaForgeLayout.ApplyRow(Go, TopiaForgeGap.Xs, TopiaForgeGap.None);
            content = new TopiaForgeContainer(Host, Scheme, Go);
            microphone = TopiaForgeSprites.Icon(TopiaForgeIcon.Microphone);
            microphoneOff = TopiaForgeSprites.Icon(TopiaForgeIcon.MicrophoneOff);
            busy = TopiaForgeSprites.Icon(TopiaForgeIcon.Warning);
            noDevice = TopiaForgeSprites.Icon(TopiaForgeIcon.Cross);

            var glyph = new GameObject("Glyph", typeof(RectTransform));
            glyph.transform.SetParent(Go.transform, false);
            var glyphLayout = glyph.AddComponent<LayoutElement>();
            glyphLayout.minWidth = size;
            glyphLayout.preferredWidth = size;
            glyphLayout.minHeight = size;
            glyphLayout.preferredHeight = size;

            backing = CreateImage(glyph.transform, "Backing", TopiaForgeSprites.Circle(), sliced: false);
            TopiaForgeAnchors.Stretch(backing.rectTransform);

            ring = CreateImage(glyph.transform, "Halo", TopiaForgeSprites.Ring(TopiaForgeRadius.Chip, TopiaForgeTokens.BorderStandard), sliced: true);
            ringRect = ring.rectTransform;
            TopiaForgeAnchors.Stretch(ringRect, -2f, -2f, -2f, -2f);

            icon = CreateImage(glyph.transform, "Icon", microphone, sliced: false);
            var iconRect = icon.rectTransform;
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(size * 0.78f, size * 0.78f);

            driver = Go.AddComponent<TopiaForgeTalkIndicatorDriver>();
            driver.Indicator = this;
            driver.enabled = false;

            this.FixedHeight(size + 4f);
            Repaint(Theme);
        }

        /// <summary>Gets the state shown.</summary>
        public TopiaForgeTalkState State => state;

        /// <summary>Adds (or removes) a caption that names the state ("MUTED BY HOST").</summary>
        public TopiaForgeTalkIndicator ShowCaption(bool show = true)
        {
            if (show && caption == null)
            {
                caption = content.Label(TopiaForgeTextStyle.Caption).NoWrap();
                Repaint(Theme);
            }

            if (caption != null && caption.Go.activeSelf != show)
            {
                caption.Go.SetActive(show);
            }

            return this;
        }

        /// <summary>Collapses the indicator while idle (name tags and scoreboard rows).</summary>
        public TopiaForgeTalkIndicator HideWhenIdle(bool hide = true)
        {
            hideWhenIdle = hide;
            UpdateVisibility();
            return this;
        }

        /// <summary>Dirty-checked state change.</summary>
        public void SetState(TopiaForgeTalkState value)
        {
            if (value == state)
            {
                return;
            }

            state = value;
            Repaint(Theme);
        }

        /// <summary>Applies the resolved theme; accessibility changes also re-evaluate the animation.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            Repaint(theme);
        }

        internal void Tick()
        {
            TopiaForgeBreath.Evaluate(Time.unscaledTime, Host.EffectiveMotion, out var scale, out var alpha);
            ApplyHalo(scale, alpha);
        }

        private void Repaint(TopiaForgeResolvedTheme theme)
        {
            var presentation = TopiaForgeTalkPresentation.For(state);
            var tone = theme.ToneColor(ToneFor(state));
            var sprite = SpriteFor(presentation.Glyph);
            if (!ReferenceEquals(icon.sprite, sprite))
            {
                icon.sprite = sprite;
            }

            icon.color = tone;
            var fillColor = tone;
            fillColor.a = state == TopiaForgeTalkState.Idle ? 0f : Scheme == TopiaForgeScheme.Paper ? 0.14f : 0.22f;
            backing.color = fillColor;

            var hasHalo = presentation.Ring != TopiaForgeTalkRing.None;
            if (ring.enabled != hasHalo)
            {
                ring.enabled = hasHalo;
            }

            var haloColor = tone;
            haloColor.a = shownAlpha >= 0f ? shownAlpha : TopiaForgeBreath.RestAlpha;
            ring.color = haloColor;

            if (caption != null)
            {
                caption.SetText(presentation.Caption);
                caption.SetTone(ToneFor(state));
            }

            var animate = presentation.Ring == TopiaForgeTalkRing.Pulsing && Host.EffectiveMotion > 0f;
            if (driver.enabled != animate)
            {
                driver.enabled = animate;
            }

            if (!animate)
            {
                ApplyHalo(1f, TopiaForgeBreath.RestAlpha);
            }

            UpdateVisibility();
        }

        private void ApplyHalo(float scale, float alpha)
        {
            if (Mathf.Abs(scale - shownScale) > 0.0005f)
            {
                shownScale = scale;
                ringRect.localScale = new Vector3(scale, scale, 1f);
            }

            if (Mathf.Abs(alpha - shownAlpha) > 0.002f)
            {
                shownAlpha = alpha;
                var color = ring.color;
                color.a = alpha;
                ring.color = color;
            }
        }

        private void UpdateVisibility()
        {
            var show = !hideWhenIdle || state != TopiaForgeTalkState.Idle;
            if (Go.activeSelf != show)
            {
                Go.SetActive(show);
            }
        }

        private Sprite SpriteFor(TopiaForgeTalkGlyph glyph)
        {
            switch (glyph)
            {
                case TopiaForgeTalkGlyph.MicrophoneOff:
                    return microphoneOff;
                case TopiaForgeTalkGlyph.Busy:
                    return busy;
                case TopiaForgeTalkGlyph.NoDevice:
                    return noDevice;
                default:
                    return microphone;
            }
        }

        private static TopiaForgeTone ToneFor(TopiaForgeTalkState value)
        {
            switch (value)
            {
                case TopiaForgeTalkState.Talking:
                    return TopiaForgeTone.Success;
                case TopiaForgeTalkState.Muted:
                case TopiaForgeTalkState.MicBusy:
                    return TopiaForgeTone.Warning;
                case TopiaForgeTalkState.MutedByHost:
                    return TopiaForgeTone.Danger;
                case TopiaForgeTalkState.NoMic:
                    return TopiaForgeTone.Muted;
                default:
                    return TopiaForgeTone.Faint;
            }
        }

        private static UImage CreateImage(Transform parent, string name, Sprite sprite, bool sliced)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = sliced ? UImage.Type.Sliced : UImage.Type.Simple;
            image.raycastTarget = false;
            return image;
        }
    }

    /// <summary>Per-frame halo driver, enabled only while a talking halo breathes.</summary>
    internal sealed class TopiaForgeTalkIndicatorDriver : MonoBehaviour
    {
        public TopiaForgeTalkIndicator? Indicator;

        private void Update()
        {
            Indicator?.Tick();
        }
    }
}
