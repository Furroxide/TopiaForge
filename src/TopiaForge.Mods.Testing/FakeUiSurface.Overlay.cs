using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        private static readonly OperationResult<UiOverlayContentKind> OverlayLimit =
            OperationResult<UiOverlayContentKind>.Failure(ModErrorCode.RateLimited, "Four overlays are already live in this scope.");
        private readonly List<FakeUiSurface> exitingOverlays = new List<FakeUiSurface>(4);
        private bool trackingOverlayExits;

        internal bool IsStopping => lifetime.IsStopping;

        private OperationResult<UiOverlayContentKind> CheckOverlay(UiSurfaceRequest request, bool received)
        {
            PruneOverlayExits();
            if (!received)
            {
                var count = 0;
                foreach (var surface in exitingOverlays)
                    if (surface.Receiver == null) count++;
                foreach (var surface in surfaces)
                    if (surface.Receiver == null && surface.OverlayKind.HasValue) count++;
                if (count >= 4) return OverlayLimit;
            }

            return UiOverlayAdmission.Check(request.Content, request.Dismissible);
        }

        private FakeUiSurface? TopOverlay()
        {
            for (var index = surfaces.Count - 1; index >= 0; index--)
            {
                var surface = surfaces[index];
                if (surface.OverlayKind.HasValue && surface.IsVisible) return surface;
            }
            return null;
        }

        private bool IsOverlayFocused() => TopOverlay()?.OverlayKind == UiOverlayContentKind.Interactive;

        internal bool AllowsOverlayInput(FakeUiSurface surface)
        {
            var top = TopOverlay();
            if (top?.OverlayKind != UiOverlayContentKind.Interactive) return !surface.OverlayKind.HasValue;
            return ReferenceEquals(top, surface) && modals.Count == 0;
        }

        private void RememberOverlayExit(FakeUiSurface surface)
        {
            if (!surface.IsOverlayExiting) return;
            if (!trackingOverlayExits)
            {
                trackingOverlayExits = true;
                lifetime.StoppingToken.Register(exitingOverlays.Clear);
            }
            PruneOverlayExits();
            exitingOverlays.Add(surface);
        }

        private void PruneOverlayExits()
        {
            for (var index = exitingOverlays.Count - 1; index >= 0; index--)
                if (!exitingOverlays[index].IsOverlayExiting) exitingOverlays.RemoveAt(index);
        }
    }

    public sealed partial class FakeUiSurface
    {
        private double? overlayChangedAt;
        private double overlayDuration;

        internal UiOverlayContentKind? OverlayKind { get; private set; }

        internal bool IsOverlayExiting => OverlayKind.HasValue && !IsShown && OverlayMotionProgress < 1d;

        internal string? OverlayVisualState => IsShown ? OverlayMotionProgress < 1d ? "entering" : "shown" :
            IsOverlayExiting ? "exiting" : null;

        internal double OverlayMotionProgress =>
            owner.IsStopping || owner.Accessibility.ReducedMotion || owner.Accessibility.MotionIntensity == 0f ||
            overlayDuration == 0d || overlayChangedAt == null
                ? 1d : Math.Clamp((widgetClock() - overlayChangedAt.Value) / overlayDuration, 0d, 1d);

        internal void InitializeOverlay(UiOverlayContentKind kind)
        {
            OverlayKind = kind;
            StartOverlayTransition(entering: true);
        }

        private void StartOverlayTransition(bool entering)
        {
            if (!OverlayKind.HasValue) return;
            var motion = owner.ResolveTheme(Request.Theme, Receiver).Descriptor.Motion;
            overlayChangedAt = widgetClock();
            overlayDuration = owner.MotionSeconds(entering ? motion.BaseMs : motion.FastMs);
        }

        private void ChangeOverlayBody(string body)
        {
            if (string.Equals(body, Body, StringComparison.Ordinal)) return;
            var previous = Body;
            Body = body;
            try
            {
                FakeUiTreeCapture.VerifyComposition(owner, this, Content, Images);
                wireTarget?.Measure(owner, revision + 1, Request, body, IsVisible, Images.Presentation, widgetEpoch(), retained: true);
                revision++;
            }
            catch
            {
                Body = previous;
                throw;
            }
        }
    }
}
