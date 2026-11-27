using System;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Standard enter/exit transitions built from the token durations. Every preset is
    /// a no-op-to-end-state under ReducedMotion (TopiaForgeTween handles that).
    /// </summary>
    public static class TopiaForgeMotion
    {
        /// <summary>Window enter: fade in + scale 0.96 → 1.</summary>
        public static void WindowIn(TopiaForgeWidget window)
        {
            var motion = window.ResolvedTheme.Descriptor?.Motion;
            var duration = motion.HasValue ? motion.Value.BaseMs / 1000f : TopiaForgeTokens.DurationBase;
            var ease = ResolvePackEase(motion, TopiaForgeEase.OutCubic);
            TopiaForgeTween.FadeTo(window, 0f, 1f, duration, ease);
            TopiaForgeTween.ScaleTo(window, 0.96f, 1f, duration, ease);
        }

        /// <summary>Window exit: quick fade; onDone deactivates.</summary>
        public static void WindowOut(TopiaForgeWidget window, Action onDone)
        {
            var motion = window.ResolvedTheme.Descriptor?.Motion;
            var duration = motion.HasValue ? motion.Value.FastMs / 1000f : TopiaForgeTokens.DurationFast;
            TopiaForgeTween.FadeTo(window, 1f, 0f, duration, ResolvePackEase(motion, TopiaForgeEase.OutQuad), onDone);
        }

        internal static void OverlayIn(TopiaForgeWidget scrim, TopiaForgeWidget panel, float fromAlpha, float fromY)
        {
            var motion = panel.ResolvedTheme.Descriptor?.Motion;
            var duration = motion.HasValue ? motion.Value.BaseMs / 1000f : TopiaForgeTokens.DurationBase;
            var ease = ResolvePackEase(motion, TopiaForgeEase.OutCubic);
            var position = panel.Rect.anchoredPosition;
            position.y = fromY;
            panel.Rect.anchoredPosition = position;
            TopiaForgeTween.FadeTo(scrim, fromAlpha, 1f, duration, ease, null, skipAtZeroIntensity: true);
            TopiaForgeTween.MoveY(panel, fromY, 0f, duration, ease, null, skipAtZeroIntensity: true);
        }

        internal static void OverlayOut(TopiaForgeWidget overlay, Action onDone)
        {
            var motion = overlay.ResolvedTheme.Descriptor?.Motion;
            var duration = motion.HasValue ? motion.Value.FastMs / 1000f : TopiaForgeTokens.DurationFast;
            var alpha = overlay.EnsureCanvasGroup().alpha;
            TopiaForgeTween.FadeTo(overlay, alpha, 0f, duration, ResolvePackEase(motion, TopiaForgeEase.OutQuad), onDone,
                skipAtZeroIntensity: true);
        }

        internal static void StingerIn(TopiaForgeWidget stinger)
        {
            var motion = stinger.ResolvedTheme.Descriptor?.Motion;
            var duration = motion.HasValue ? motion.Value.BaseMs / 1000f : TopiaForgeTokens.DurationBase;
            TopiaForgeTween.ScaleTo(stinger, 0.92f, 1f, duration, TopiaForgeEase.OutBack, null, skipAtZeroIntensity: true);
        }

        internal static void StingerOut(TopiaForgeWidget stinger, Action onDone)
        {
            var motion = stinger.ResolvedTheme.Descriptor?.Motion;
            var duration = motion.HasValue ? motion.Value.SlowMs / 1000f : TopiaForgeTokens.DurationSlow;
            stinger.Rect.localScale = Vector3.one;
            TopiaForgeTween.FadeTo(stinger, 1f, 0f, duration, TopiaForgeEase.OutQuad, onDone, skipAtZeroIntensity: true);
        }

        /// <summary>Modal enter: dialog pops with OutBack; pair with a backdrop fade.</summary>
        public static void ModalIn(TopiaForgeWidget dialog)
        {
            TopiaForgeTween.FadeTo(dialog, 0f, 1f, TopiaForgeTokens.DurationBase);
            TopiaForgeTween.ScaleTo(dialog, 0.94f, 1f, TopiaForgeTokens.DurationSlow, TopiaForgeEase.OutBack);
        }

        /// <summary>Gets the modal exit-motion preset.</summary>
        public static void ModalOut(TopiaForgeWidget dialog, Action onDone)
        {
            TopiaForgeTween.FadeTo(dialog, 1f, 0f, TopiaForgeTokens.DurationFast, TopiaForgeEase.OutQuad, onDone);
        }

        /// <summary>Toast enter: slide in from the right + fade.</summary>
        public static void ToastIn(TopiaForgeWidget toast, float restingX) => ToastIn(toast, restingX, null, 1f);

        internal static void ToastIn(TopiaForgeWidget toast, float restingX, UiHost? viewer, float scale)
        {
            TopiaForgeTween.FadeTo(toast, 0f, 1f, TopiaForgeTokens.DurationBase, TopiaForgeEase.OutQuad,
                null, skipAtZeroIntensity: true, viewer);
            TopiaForgeTween.MoveX(toast, restingX + 40f * scale, restingX, TopiaForgeTokens.DurationBase, TopiaForgeEase.OutCubic, viewer);
        }

        /// <summary>
        /// Toast exit: slide out to the right + fade. <paramref name="onDone"/> may hand the view to the next toast, and under
        /// reduced motion it runs at once, inside this call, so the slide starts first: started after it, the slide would push
        /// that next toast aside.
        /// </summary>
        public static void ToastOut(TopiaForgeWidget toast, float restingX, Action onDone) => ToastOut(toast, restingX, onDone, null, 1f);

        internal static void ToastOut(TopiaForgeWidget toast, float restingX, Action onDone, UiHost? viewer, float scale)
        {
            TopiaForgeTween.MoveX(toast, restingX, restingX + 40f * scale, TopiaForgeTokens.DurationBase, TopiaForgeEase.InQuad, viewer);
            TopiaForgeTween.FadeTo(toast, 1f, 0f, TopiaForgeTokens.DurationBase, TopiaForgeEase.OutQuad,
                onDone, skipAtZeroIntensity: true, viewer);
        }

        /// <summary>Banner punch: scale 1.35 → 1 (HUD wave banners).</summary>
        public static void Punch(TopiaForgeWidget widget, float intensity = 1.35f)
        {
            var scaled = 1f + ((intensity - 1f) * widget.Host.EffectiveMotion);
            var motion = widget.ResolvedTheme.Descriptor?.Motion;
            var duration = motion.HasValue ? motion.Value.SlowMs / 1000f : TopiaForgeTokens.DurationSlow;
            TopiaForgeTween.ScaleTo(widget, scaled, 1f, duration, ResolvePackEase(motion, TopiaForgeEase.OutCubic));
        }

        private static TopiaForgeEase ResolvePackEase(UiThemeMotion? motion, TopiaForgeEase fallback) =>
            motion?.Ease switch
            {
                UiThemeEase.OutBack => TopiaForgeEase.OutBack,
                UiThemeEase.OutCubic => TopiaForgeEase.OutCubic,
                _ => fallback
            };

        /// <summary>Attaches a breathing pulse (alpha/scale sine) to a widget.</summary>
        public static TopiaForgePulse Pulse(TopiaForgeWidget widget, float frequency = 2f, float alphaAmplitude = 0.12f, float scaleAmplitude = 0.02f)
        {
            var pulse = TopiaForgeComponents.GetOrAdd<TopiaForgePulse>(widget.Go);
            pulse.Frequency = frequency;
            pulse.AlphaAmplitude = alphaAmplitude;
            pulse.ScaleAmplitude = scaleAmplitude;
            pulse.Initialize(widget.Host);
            return pulse;
        }
    }

    /// <summary>
    /// Sine breathing on alpha and scale (NeonPulse's replacement). Amplitudes are
    /// multiplied by the theme MotionScale, so accessibility settings damp or disable
    /// every pulse in the process at once.
    /// </summary>
    public sealed class TopiaForgePulse : MonoBehaviour
    {
        /// <summary>Gets frequency.</summary>
        public float Frequency = 2f;
        /// <summary>Gets alpha amplitude.</summary>
        public float AlphaAmplitude = 0.12f;
        /// <summary>Gets scale amplitude.</summary>
        public float ScaleAmplitude = 0.02f;

        private Graphic? graphic;
        private UiHost? host;
        private float baseAlpha;
        private Vector3 baseScale;

        internal void Initialize(UiHost owner)
        {
            host = owner;
        }

        private void Awake()
        {
            graphic = GetComponent<Graphic>();
            if (graphic != null)
            {
                baseAlpha = graphic.color.a;
            }

            baseScale = transform.localScale;
        }

        private void Update()
        {
            var motion = host?.EffectiveMotion ?? TopiaForgeTheme.EffectiveMotion;
            if (motion <= 0f)
            {
                transform.localScale = baseScale;
                if (graphic != null)
                {
                    var reset = graphic.color;
                    reset.a = baseAlpha;
                    graphic.color = reset;
                }

                return;
            }

            var pulse = 0.5f + (0.5f * Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f * Mathf.Max(0.01f, Frequency)));
            transform.localScale = baseScale * (1f + (ScaleAmplitude * motion * pulse));
            if (graphic != null)
            {
                var color = graphic.color;
                var amplitude = AlphaAmplitude * motion;
                color.a = Mathf.Clamp01(baseAlpha - amplitude + (amplitude * 2f * pulse));
                graphic.color = color;
            }
        }
    }
}
