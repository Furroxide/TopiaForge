using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        /// <summary>Captures the specified live surface at the supplied viewport.</summary>
        /// <exception cref="KeyNotFoundException">No live surface has the supplied id.</exception>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public UiTreeCapture Capture(string surfaceId, UiCaptureViewport viewport)
        {
            gameThread.AssertCurrent();
            if (surfaceId == null) throw new ArgumentNullException(nameof(surfaceId));
            if (viewport == null) throw new ArgumentNullException(nameof(viewport));
            foreach (var surface in surfaces)
            {
                if (surface.Id == surfaceId) return surface.Capture(viewport);
            }
            throw new KeyNotFoundException("There is no live fake surface with that id.");
        }

        internal FakeUiTextMetrics ResolveTextMetrics(UiThemeDescriptor descriptor, string font) =>
            themeScope.Package.TextMetrics(descriptor, font);
    }

    public sealed partial class FakeUiSurface
    {
        /// <summary>Captures the resolved fake surface without changing its state or its clock binding.</summary>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public UiTreeCapture Capture(UiCaptureViewport viewport) => Capture(viewport, UiCaptureControlState.Rest, 0f, null);

        internal UiTreeCapture CaptureForFixture(UiCaptureViewport viewport, string state, float scrollOffset, bool? hudScheme) =>
            Capture(viewport, Enum.Parse<UiCaptureControlState>(state), scrollOffset, hudScheme);

        private UiTreeCapture Capture(UiCaptureViewport viewport, UiCaptureControlState state, float scrollOffset, bool? hudScheme)
        {
            if (viewport == null) throw new ArgumentNullException(nameof(viewport));
            if (scrollOffset < 0 || float.IsNaN(scrollOffset) || float.IsInfinity(scrollOffset))
                throw new ArgumentOutOfRangeException(nameof(scrollOffset));
            EnsureActive();
            return new FakeUiTreeCapture(owner, this, viewport, state, hudScheme).Capture(scrollOffset);
        }

        internal double CapturedCountdownRemaining(UiCountdown countdown) => countdown.ClockEpoch == widgetEpoch()
            ? countdown.RemainingTime(widgetClock())
            : 0d;

        internal int CapturedCountdownSeconds(UiCountdown countdown) =>
            UiCountdownFormatting.WholeSeconds(CapturedCountdownRemaining(countdown));
    }

    internal readonly struct FakeUiTextMetrics
    {
        internal FakeUiTextMetrics(float ascender, float descender)
        {
            Ascender = ascender;
            Descender = descender;
        }

        internal float Ascender { get; }
        internal float Descender { get; }

        internal float PredictHeight(float referenceHeight)
        {
            // The calibrated point size uses the fixture's ascender-to-descender span.
            var span = (double)Ascender - Descender;
            return (float)((referenceHeight / span) * span);
        }
    }
}
