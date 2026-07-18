using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Gameplay overlay layer: a Scaled root for docked panels (user HUD scale, the
    /// Zombies 0.75–1.35 clamp), a World root for floater/speech layers (never scaled —
    /// projections must stay exact), and SetInteractive to enable the raycaster only
    /// while a gameplay modal is up. Use .Dynamic() on per-frame-churning subtrees to
    /// isolate their canvas rebuilds from static chrome.
    /// </summary>
    public sealed class TopiaForgeHudLayer : TopiaForgeContainer
    {
        private readonly GraphicRaycaster raycaster;
        private float hudScale = 1f;

        internal TopiaForgeHudLayer(UiHost host, TopiaForgeContainer canvasRoot)
            : base(host, TopiaForgeScheme.Hud, canvasRoot.Go)
        {
            raycaster = Go.GetComponent<GraphicRaycaster>();
            Scaled = Stack("ScaleRoot");
            World = Stack("WorldRoot");
        }

        /// <summary>Docked HUD panels go here; affected by SetHudScale.</summary>
        public TopiaForgeContainer Scaled { get; }

        /// <summary>World-projected layers go here (never scaled).</summary>
        public TopiaForgeContainer World { get; }

        /// <summary>Per-mod HUD scale (clamped 0.75–1.35), dirty-checked.</summary>
        public void SetHudScale(float scale)
        {
            scale = Mathf.Clamp(scale, 0.75f, 1.35f);
            if (Mathf.Approximately(hudScale, scale))
            {
                return;
            }

            hudScale = scale;
            Scaled.Rect.localScale = new Vector3(scale, scale, 1f);
        }

        /// <summary>Enables clicks only while a gameplay modal needs them.</summary>
        public void SetInteractive(bool interactive)
        {
            if (raycaster != null && raycaster.enabled != interactive)
            {
                raycaster.enabled = interactive;
            }
        }

        /// <summary>Pooled drifting damage/score floaters (world-projected).</summary>
        public TopiaForgeFloaterLayer Floaters(int poolSize = 16)
        {
            return new TopiaForgeFloaterLayer(World, poolSize, speechBubbles: false);
        }

        /// <summary>Pooled speech bubbles (world-projected, with backing panel).</summary>
        public TopiaForgeFloaterLayer SpeechBubbles(int poolSize = 12)
        {
            return new TopiaForgeFloaterLayer(World, poolSize, speechBubbles: true);
        }

        /// <summary>Center transient banner with the punch-hold-fade timeline.</summary>
        public TopiaForgeBanner Banner()
        {
            return new TopiaForgeBanner(Scaled);
        }
    }

    /// <summary>
    /// HUD banner ("WAVE 3"): Audiowide display text that punches in, holds, and fades
    /// (ported timings 0.18/0.6/0.4, punch intensity scaled by the motion setting).
    /// Re-triggering resets the timeline.
    /// </summary>
    public sealed class TopiaForgeBanner : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private const float PunchSeconds = 0.18f;
        private const float HoldSeconds = 0.6f;
        private const float FadeSeconds = 0.4f;

        private readonly TextMeshProUGUI label;
        private float shownAt = -999f;
        private TopiaForgeTone tone = TopiaForgeTone.Primary;
        private bool hasCustomColor;
        private Color customColor = Color.white;

        internal TopiaForgeBanner(TopiaForgeContainer parent)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Banner"))
        {
            Rect.anchorMin = new Vector2(0.5f, 0.72f);
            Rect.anchorMax = new Vector2(0.5f, 0.72f);
            Rect.pivot = new Vector2(0.5f, 0.5f);
            Rect.sizeDelta = new Vector2(900f, 80f);

            label = TopiaForgeTmp.Create(Go);
            label.fontSize = TopiaForgeTokens.BannerSize;
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Banner);
            if (font != null)
            {
                label.font = font;
            }

            if (TopiaForgeFonts.UseFauxDisplay)
            {
                label.fontStyle = FontStyles.Bold;
            }

            var driver = Go.AddComponent<TopiaForgeBannerDriver>();
            driver.Banner = this;
            Go.SetActive(false);
            ApplyTheme(Theme);
        }

        /// <summary>Sets this element's semantic color tone.</summary>
        public TopiaForgeBanner Tone(TopiaForgeTone value)
        {
            SetTone(value);
            return this;
        }

        /// <summary>Dirty-checked runtime semantic color role.</summary>
        public void SetTone(TopiaForgeTone value)
        {
            if (!hasCustomColor && tone == value)
            {
                return;
            }

            tone = value;
            hasCustomColor = false;
            ApplyTheme(Theme);
        }

        /// <summary>
        /// Custom banner color (the Zombies ShowBanner(text, color) pattern); the fade
        /// timeline keeps driving alpha. High-contrast emphasis is applied by the theme.
        /// </summary>
        public void SetColor(Color value)
        {
            hasCustomColor = true;
            customColor = value;
            ApplyTheme(Theme);
        }

        /// <summary>Shows (or re-punches) the banner.</summary>
        public void Show(string text)
        {
            label.text = text;
            shownAt = Time.unscaledTime;
            Go.SetActive(true);
            TopiaForgeMotion.Punch(this);
        }

        /// <summary>Hides this element without playing an exit animation.</summary>
        public void HideImmediate()
        {
            shownAt = -999f;
            Go.SetActive(false);
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            var color = hasCustomColor ? theme.Emphasize(customColor) : theme.ToneColor(tone);
            color.a = label.color.a;
            label.color = color;
        }

        internal void Tick()
        {
            var age = Time.unscaledTime - shownAt;
            if (age > PunchSeconds + HoldSeconds + FadeSeconds)
            {
                if (Go.activeSelf)
                {
                    Go.SetActive(false);
                }

                return;
            }

            var alpha = 1f;
            if (age > PunchSeconds + HoldSeconds)
            {
                alpha = 1f - ((age - PunchSeconds - HoldSeconds) / FadeSeconds);
            }

            var color = label.color;
            if (!Mathf.Approximately(color.a, alpha))
            {
                color.a = alpha;
                label.color = color;
            }
        }
    }

    internal sealed class TopiaForgeBannerDriver : MonoBehaviour
    {
        public TopiaForgeBanner? Banner;

        private void Update()
        {
            Banner?.Tick();
        }
    }
}
