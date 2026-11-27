using System;

namespace TopiaForge.Mods.UnityUi
{
    public static partial class TopiaForgeToasts
    {
        internal delegate Action? ToastObserver(TopiaForgeContainer view, Func<ToastPaint?> readPaint);

        internal readonly struct ToastPaint
        {
            internal ToastPaint(TopiaForgeResolvedTheme theme, TopiaForgeEffectiveAccessibility accessibility, float scale,
                float restingX)
            {
                Theme = theme;
                Accessibility = accessibility;
                Scale = scale;
                RestingX = restingX;
            }

            internal TopiaForgeResolvedTheme Theme { get; }
            internal TopiaForgeEffectiveAccessibility Accessibility { get; }
            internal float Scale { get; }
            internal float RestingX { get; }
        }

        internal static void ShowPlainObserved(string text, TopiaForgeTone tone, UiHost viewer, ToastObserver observe) =>
            Enqueue(text, tone, DefaultDuration, plain: true, viewer, observe);

        private static ToastPaint? CapturePaint(ToastView view, uint captureGeneration)
        {
            if (view.CaptureGeneration != captureGeneration || view.Root?.Go.activeSelf != true) return null;
            var settings = ViewerSettings(view);
            return view.PaintedTheme != null && settings.UiScale == view.UiScale && settings.HighContrast == view.HighContrast
                ? new ToastPaint(view.PaintedTheme, settings, view.Scale, view.RestingX) : (ToastPaint?)null;
        }

        private static void ObserveCapture(ToastView view, ToastObserver? observe)
        {
            if (observe == null) return;
            var captureGeneration = view.CaptureGeneration;
            try { view.ReleaseCapture = observe(view.Root!, () => CapturePaint(view, captureGeneration)); }
            catch (Exception error) { TopiaForgeLog.Warn("Gallery toast capture could not attach (" + error.GetType().Name + ")."); }
        }

        private static void ReleaseCapture(ToastView view)
        {
            view.CaptureGeneration++;
            var release = view.ReleaseCapture;
            view.ReleaseCapture = null;
            if (release == null) return;
            try { release(); }
            catch (Exception error) { TopiaForgeLog.Warn("Gallery toast capture could not retire (" + error.GetType().Name + ")."); }
        }
    }
}
