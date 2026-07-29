using System;
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
            TopiaForgeTween.FadeTo(window, 0f, 1f, TopiaForgeTokens.DurationBase);
            TopiaForgeTween.ScaleTo(window, 0.96f, 1f, TopiaForgeTokens.DurationBase, TopiaForgeEase.OutCubic);
        }

        /// <summary>Window exit: quick fade; onDone deactivates.</summary>
        public static void WindowOut(TopiaForgeWidget window, Action onDone)
        {
            TopiaForgeTween.FadeTo(window, 1f, 0f, TopiaForgeTokens.DurationFast, TopiaForgeEase.OutQuad, onDone);
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
        public static void ToastIn(TopiaForgeWidget toast, float restingX)
        {
            TopiaForgeTween.FadeTo(toast, 0f, 1f, TopiaForgeTokens.DurationBase);
            TopiaForgeTween.MoveX(toast, restingX + 40f, restingX, TopiaForgeTokens.DurationBase, TopiaForgeEase.OutCubic);
        }

        /// <summary>Gets the toast exit-motion preset.</summary>
        public static void ToastOut(TopiaForgeWidget toast, float restingX, Action onDone)
        {
            TopiaForgeTween.FadeTo(toast, 1f, 0f, TopiaForgeTokens.DurationBase, TopiaForgeEase.OutQuad, onDone);
            TopiaForgeTween.MoveX(toast, restingX, restingX + 40f, TopiaForgeTokens.DurationBase, TopiaForgeEase.InQuad);
        }

        /// <summary>Banner punch: scale 1.35 → 1 (HUD wave banners).</summary>
        public static void Punch(TopiaForgeWidget widget, float intensity = 1.35f)
        {
            var scaled = 1f + ((intensity - 1f) * widget.Host.EffectiveMotion);
            TopiaForgeTween.ScaleTo(widget, scaled, 1f, TopiaForgeTokens.DurationSlow, TopiaForgeEase.OutCubic);
        }

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
