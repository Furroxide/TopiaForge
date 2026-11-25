using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        internal static bool GalleryCaptureEnabled { get; } =
            string.Equals(Environment.GetEnvironmentVariable("TOPIAFORGE_UI_GALLERY_CAPTURE"), "1", StringComparison.Ordinal);
        private readonly string galleryCaptureDirectory;
        private Func<NetRealm>? galleryViewer;

        internal bool CanRecordLocalGallery => GalleryCaptureEnabled &&
            UiGalleryCaptureFixture.MatchesOwner(host.OwnerId, remote: false, authenticatedOwner: null);

        internal void FollowGalleryViewer(Func<NetRealm> view) => galleryViewer = view;

        private void ObserveGalleryFixture(UnityUiSurface surface, UiSurfaceRequest request, UiRemoteThemeSelection? remote,
            UiThemeDescriptor? declared, GalleryPreviewPalette? preview)
        {
            if (!GalleryCaptureEnabled) return;
            try
            {
                var envelope = remote?.Envelope;
                // The private preview stage is fed only a source-owned fixture. Other callers must pass the matcher.
                var fixture = preview.HasValue ? request :
                    UiGalleryCaptureFixture.Match(host.OwnerId, remote.HasValue, envelope, request, declared);
                if (fixture == null) return;
                surface.ObserveGalleryFixture(fixture, request.Content!, galleryCaptureDirectory, declared, envelope,
                    galleryViewer, logger);
            }
            catch (Exception error)
            {
                try { logger.Warn("Gallery capture could not attach (" + error.GetType().Name + ")."); }
                catch { }
            }
        }

        private sealed partial class UnityUiSurface
        {
            internal void ObserveGalleryFixture(UiSurfaceRequest fixture, UiNode original, string directory, UiThemeDescriptor? declared,
                UiWireEnvelope? envelope, Func<NetRealm>? viewer, IModLogger captureLogger)
            {
                var canvas = CanvasRoot.GetComponent<Canvas>();
                if (canvas == null) return;
                var capture = CanvasRoot.AddComponent<UnityUiGalleryCaptureDriver>();
                capture.Initialize(Collect, captureLogger.Warn);

                bool Collect(bool permitFallback)
                {
                    var tree = compositionRoot;
                    var view = widget;
                    if (!ReferenceEquals(original, admitted) || tree == null || view == null || !presence.IsLive)
                        return true;
                    if (!IsVisible || !tree.Go.activeInHierarchy || !canvas.isActiveAndEnabled ||
                        tree.Rect.rect.width <= 0 || tree.Rect.rect.height <= 0 ||
                        Screen.width <= 0 || Screen.height <= 0 || canvas.scaleFactor <= 0 ||
                        !Mathf.Approximately(view.Rect.localScale.x, 1f) || !Mathf.Approximately(view.Rect.localScale.y, 1f))
                        return false;
                    var visibility = view.Go.GetComponent<CanvasGroup>();
                    var scroll = tree.Go.GetComponentInParent<ScrollRect>();
                    if (visibility != null && visibility.alpha < 0.999f ||
                        scroll != null && scroll.velocity.sqrMagnitude > 0.01f)
                        return false;
                    var nativeTheme = tree.ResolvedTheme;
                    var effective = nativeTheme.Descriptor ?? UiThemePackParser.Default;
                    var requestedId = envelope?.ThemeId ?? declared?.Id;
                    var requestedDigest = envelope?.DescriptorDigest ?? declared?.Digest;
                    var fallback = requestedId != null && (effective.Id != requestedId || !requestedDigest.HasValue ||
                        !requestedDigest.Value.Span.SequenceEqual(effective.Digest.Span));
                    if (fallback && !permitFallback) return false;

                    var recorder = new UiGalleryNativeTree(canvas, nativeTheme, tree.Host.EffectiveAccessibility, fallback);
                    if (!recorder.TryCapture(fixture, view.Rect, tree.Rect, out var resolved)) return false;
                    var who = envelope != null ? "remote-guest" : viewer?.Invoke() switch
                    {
                        NetRealm.ListenHost => "listen-host",
                        NetRealm.Client => "guest-local",
                        NetRealm.Offline => "solo",
                        _ => "local",
                    };
                    UiGalleryCaptureJson.Save(directory, fixture.Id, who, resolved!, recorder, envelope);
                    captureLogger.Info("Captured the native Gallery fixture '" + fixture.Id + "'.");
                    return true;
                }
            }
        }
    }
}
