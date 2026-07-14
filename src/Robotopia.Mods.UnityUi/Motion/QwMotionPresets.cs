using System;
using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Standard enter/exit transitions built from the token durations. Every preset is
    /// a no-op-to-end-state under ReducedMotion (QwTween handles that).
    /// </summary>
    public static class QwMotion
    {
        /// <summary>Window enter: fade in + scale 0.96 → 1.</summary>
        public static void WindowIn(QwWidget window)
        {
            QwTween.FadeTo(window, 0f, 1f, QwTokens.DurationBase);
            QwTween.ScaleTo(window, 0.96f, 1f, QwTokens.DurationBase, QwEase.OutCubic);
        }

        /// <summary>Window exit: quick fade; onDone deactivates.</summary>
        public static void WindowOut(QwWidget window, Action onDone)
        {
            QwTween.FadeTo(window, 1f, 0f, QwTokens.DurationFast, QwEase.OutQuad, onDone);
        }

        /// <summary>Modal enter: dialog pops with OutBack; pair with a backdrop fade.</summary>
        public static void ModalIn(QwWidget dialog)
        {
            QwTween.FadeTo(dialog, 0f, 1f, QwTokens.DurationBase);
            QwTween.ScaleTo(dialog, 0.94f, 1f, QwTokens.DurationSlow, QwEase.OutBack);
        }

        public static void ModalOut(QwWidget dialog, Action onDone)
        {
            QwTween.FadeTo(dialog, 1f, 0f, QwTokens.DurationFast, QwEase.OutQuad, onDone);
        }

        /// <summary>Toast enter: slide in from the right + fade.</summary>
        public static void ToastIn(QwWidget toast, float restingX)
        {
            QwTween.FadeTo(toast, 0f, 1f, QwTokens.DurationBase);
            QwTween.MoveX(toast, restingX + 40f, restingX, QwTokens.DurationBase, QwEase.OutCubic);
        }

        public static void ToastOut(QwWidget toast, float restingX, Action onDone)
        {
            QwTween.FadeTo(toast, 1f, 0f, QwTokens.DurationBase, QwEase.OutQuad, onDone);
            QwTween.MoveX(toast, restingX, restingX + 40f, QwTokens.DurationBase, QwEase.InQuad);
        }

        /// <summary>Banner punch: scale 1.35 → 1 (HUD wave banners).</summary>
        public static void Punch(QwWidget widget, float intensity = 1.35f)
        {
            var scaled = 1f + ((intensity - 1f) * widget.Host.EffectiveMotion);
            QwTween.ScaleTo(widget, scaled, 1f, QwTokens.DurationSlow, QwEase.OutCubic);
        }

        /// <summary>Attaches a breathing pulse (alpha/scale sine) to a widget.</summary>
        public static QwPulse Pulse(QwWidget widget, float frequency = 2f, float alphaAmplitude = 0.12f, float scaleAmplitude = 0.02f)
        {
            var pulse = QwComponents.GetOrAdd<QwPulse>(widget.Go);
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
    public sealed class QwPulse : MonoBehaviour
    {
        public float Frequency = 2f;
        public float AlphaAmplitude = 0.12f;
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
            var motion = host?.EffectiveMotion ?? QwTheme.EffectiveMotion;
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
