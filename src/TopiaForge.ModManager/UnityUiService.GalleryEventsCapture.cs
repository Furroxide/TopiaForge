using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using TopiaForge.UiGallery.Pages;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        partial void PrepareGalleryStinger(UiStingerRequest request, UiThemeDescriptor? declared,
            UiWireEventEnvelope? authenticatedEnvelope, bool remote, ref Action<TopiaForgeStinger, double>? capture)
        {
            if (!GalleryCaptureEnabled || remote != (authenticatedEnvelope != null)) return;
            try
            {
                var id = UiGalleryCaptureFixture.MatchStinger(host.OwnerId, authenticatedEnvelope, request, declared);
                if (id == null) return;
                capture = (view, shownAt) => AttachStingerCapture(id, request, declared, authenticatedEnvelope, view, shownAt);
            }
            catch (Exception error) { ReportCaptureFailure(error); }
        }

        private void AttachStingerCapture(string id, UiStingerRequest fixture, UiThemeDescriptor? declared,
            UiWireEventEnvelope? envelope, TopiaForgeStinger view, double shownAt)
        {
            var canvas = view.Go.GetComponentInParent<Canvas>();
            if (canvas == null || !view.Go.activeInHierarchy) return;
            var driver = view.Go.AddComponent<UnityUiGalleryCaptureDriver>();
            driver.Initialize(Collect, logger.Warn, null);

            bool Collect(bool _)
            {
                var now = Time.unscaledTimeAsDouble;
                var remaining = shownAt + fixture.DurationSeconds - now;
                if (lifetime.IsStopping || remaining <= 0 || !view.Go.activeInHierarchy) return true;
                if (!SettledEvent(canvas, view.Rect)) return false;
                var theme = view.ResolvedTheme;
                var descriptor = theme.Descriptor ?? UiThemePackParser.Default;
                var fallback = envelope?.ThemeId is string themeId
                    ? descriptor.Id != themeId || !envelope.DescriptorDigest.Span.SequenceEqual(descriptor.Digest.Span)
                    : declared != null && !UiGalleryCaptureFixture.SameDescriptor(declared, descriptor);
                var recorder = new UiGalleryNativeTree(canvas, theme, host.EffectiveAccessibility, fallback, now, string.Empty);
                if (!recorder.TryCaptureStinger(id, fixture, view, remaining, out var tree)) return false;
                UiGalleryCaptureJson.SaveEvent(galleryCaptureDirectory, id, UiGalleryCaptureJson.Viewer(galleryViewer, envelope != null),
                    tree!, recorder, envelope);
                logger.Info("Captured the native Gallery fixture '" + id + "'.");
                return true;
            }
        }

        private void PresentGalleryToast(string message, UiTone tone, bool legacyRemote)
        {
            var nativeTone = ToNativeTone(tone);
            UiGalleryToastArm? arm = null;
            if (GalleryCaptureEnabled && UiGalleryCaptureFixture.MatchesToast(message, tone))
            {
                try { arm = UiGalleryToastArm.Find(host.OwnerId, legacyRemote); }
                catch (Exception error) { ReportCaptureFailure(error); }
            }
            if (arm == null)
            {
                TopiaForgeToasts.ShowPlain(message, nativeTone, host);
                return;
            }
            TopiaForgeToasts.ShowPlainObserved(message, nativeTone, host, (view, readPaint) =>
                AttachToastCapture(message, tone, arm, view, readPaint, legacyRemote));
        }

        private Action? AttachToastCapture(string message, UiTone tone, UiGalleryToastArm arm, TopiaForgeContainer view,
            Func<TopiaForgeToasts.ToastPaint?> readPaint, bool legacyRemote)
        {
            var canvas = view.Go.GetComponentInParent<Canvas>();
            if (canvas == null || !arm.Eligible) return null;
            var directory = arm.Directory;
            var driver = view.Go.GetComponent<UnityUiGalleryCaptureDriver>() ?? view.Go.AddComponent<UnityUiGalleryCaptureDriver>();
            var id = host.OwnerId == UiGalleryCaptureFixture.RemoteFixtureOwner ? PresentationPages.ToastStackId :
                "gallery-toast-" + tone.ToString().ToLowerInvariant();
            Vector2? lastPosition = null;
            driver.Initialize(Collect, logger.Warn, null);
            return driver.Release;

            bool Collect(bool _)
            {
                if (!arm.Eligible || !view.Go.activeInHierarchy) return true;
                if (readPaint() is not { } paint || !SettledEvent(canvas, view.Rect, paint.Scale)) return false;
                var position = view.Rect.anchoredPosition;
                var stable = lastPosition == position;
                lastPosition = position;
                if (!stable || !Mathf.Approximately(position.x, paint.RestingX)) return false;
                var recorder = new UiGalleryNativeTree(canvas, paint.Theme, paint.Accessibility,
                    fallback: false, Time.unscaledTimeAsDouble, string.Empty, paint.Scale);
                if (!recorder.TryCaptureToast(id, message, tone, view, out var tree)) return false;
                UiGalleryCaptureJson.SaveEvent(directory, id, UiGalleryCaptureJson.Viewer(arm.Viewer, legacyRemote),
                    tree!, recorder, null, legacyRemote);
                logger.Info("Captured the native Gallery fixture '" + id + "'.");
                return true;
            }
        }

        private static bool SettledEvent(Canvas canvas, RectTransform view, float scale = 1f) =>
            canvas.isActiveAndEnabled && canvas.scaleFactor > 0 && Screen.width > 0 && Screen.height > 0 &&
            scale > 0f && view.rect.width > 0 && view.rect.height > 0 && Mathf.Approximately(view.localScale.x, scale) &&
            Mathf.Approximately(view.localScale.y, scale) && view.GetComponent<CanvasGroup>() is { alpha: >= 0.999f };

        private void ReportCaptureFailure(Exception error)
        {
            try { logger.Warn("Gallery presentation capture could not attach (" + error.GetType().Name + ")."); }
            catch { }
        }
    }
}
