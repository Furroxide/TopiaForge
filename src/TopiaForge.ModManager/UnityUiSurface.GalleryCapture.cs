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
            private void ObserveRemoteGalleryCommit(UiWireEnvelope? envelope)
            {
                if (!GalleryCaptureEnabled || CanvasRoot == null) return;
                try { CanvasRoot.GetComponent<UnityUiGalleryCaptureDriver>()?.ObserveCommit(envelope); }
                catch (Exception error)
                {
                    try { logger.Warn("Gallery capture could not follow the committed revision (" + error.GetType().Name + ")."); }
                    catch { }
                }
            }

            internal void ObserveGalleryFixture(UiSurfaceRequest fixture, UiNode original, string directory, UiThemeDescriptor? declared,
                UiWireEnvelope? envelope, Func<NetRealm>? viewer, IModLogger captureLogger)
            {
                var canvas = CanvasRoot.GetComponent<Canvas>();
                if (canvas == null || CanvasRoot.GetComponent<UnityUiGalleryCaptureDriver>() != null) return;
                var capture = CanvasRoot.AddComponent<UnityUiGalleryCaptureDriver>();
                var observed = original;
                var selected = fixture;
                var selectedEnvelope = envelope;
                var latestGeneration = envelope?.Generation ?? 0;
                var eligible = true;
                var status = fixture.Id is "gallery-countdowns" or "gallery-status";
                var watchRetained = envelope != null && fixture.Id == "gallery-countdowns";
                capture.Initialize(Collect, captureLogger.Warn, watchRetained ? ObserveRetained : null);

                void ObserveRetained(UiWireEnvelope? latest)
                {
                    if (ReferenceEquals(observed, admitted) && ReferenceEquals(selectedEnvelope, latest)) return;
                    var wasEligible = eligible;
                    eligible = false;
                    var next = UiGalleryCaptureFixture.MatchRetainedStatus(fixture, admitted, envelope!, latest,
                        ref latestGeneration, callbacks.ClockEpoch());
                    if (next == null) return;
                    var needsCapture = !wasEligible || ReferenceEquals(selected, fixture) ||
                        selectedEnvelope?.Generation != latestGeneration;
                    selected = next;
                    observed = admitted!;
                    selectedEnvelope = latest;
                    eligible = true;
                    if (needsCapture) capture.Request();
                }

                bool Collect(bool permitFallback)
                {
                    var tree = compositionRoot;
                    var view = widget;
                    if (!eligible || !ReferenceEquals(observed, admitted) || tree == null || view == null || !presence.IsLive ||
                        status && (sentBody ?? string.Empty) != selected.Body)
                        return true;
                    var notice = layoutProblem != null ? layoutFallback : null;
                    var imageNotice = notice == null ? layoutFallback?.Rect : null;
                    if (!IsVisible || (notice == null && imageNotice == null && (!tree.Go.activeInHierarchy ||
                            tree.Rect.rect.width <= 0 || tree.Rect.rect.height <= 0)) || !canvas.isActiveAndEnabled ||
                        Screen.width <= 0 || Screen.height <= 0 || canvas.scaleFactor <= 0 ||
                        !Mathf.Approximately(view.Rect.localScale.x, 1f) || !Mathf.Approximately(view.Rect.localScale.y, 1f))
                        return false;
                    var visibility = view.Go.GetComponent<CanvasGroup>();
                    var scroll = tree.Go.GetComponentInParent<ScrollRect>();
                    if (visibility != null && visibility.alpha < 0.999f ||
                        scroll != null && scroll.velocity.sqrMagnitude > 0.01f)
                        return false;
                    var nativeTheme = (notice ?? tree).ResolvedTheme;
                    var effective = nativeTheme.Descriptor ?? UiThemePackParser.Default;
                    var requestedId = selectedEnvelope?.ThemeId ?? declared?.Id;
                    var requestedDigest = selectedEnvelope?.DescriptorDigest ?? declared?.Digest;
                    var fallback = notice != null || (requestedId != null && (effective.Id != requestedId || !requestedDigest.HasValue ||
                        !requestedDigest.Value.Span.SequenceEqual(effective.Digest.Span)));
                    if (!permitFallback && (fallback || artwork?.HasPendingDemand() == true)) return false;

                    var recorder = new UiGalleryNativeTree(canvas, nativeTheme, tree.Host.EffectiveAccessibility, fallback,
                        callbacks.Clock(), callbacks.ClockEpoch());
                    var queuedNotice = hudNoticeOverflowing ? overflowToast : null;
                    var captured = notice != null
                        ? recorder.TryCaptureLayoutNotice(selected, view.Rect, notice.Rect, layoutProblem!, out var resolved, queuedNotice)
                        : recorder.TryCapture(selected, view.Rect, tree.Rect, imageNotice, out resolved, graphConnections, queuedNotice);
                    if (!captured) return false;
                    var who = selectedEnvelope != null ? "remote-guest" : viewer?.Invoke() switch
                    {
                        NetRealm.ListenHost => "listen-host",
                        NetRealm.Client => "guest-local",
                        NetRealm.Offline => "solo",
                        _ => "local",
                    };
                    UiGalleryCaptureJson.Save(directory, selected.Id, who, resolved!, recorder, selectedEnvelope);
                    captureLogger.Info("Captured the native Gallery fixture '" + selected.Id + "'.");
                    return true;
                }
            }
        }
    }
}
