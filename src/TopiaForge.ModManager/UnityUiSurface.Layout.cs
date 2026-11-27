using System;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiSurface
        {
            private bool observingLayout;
            private TopiaForgeLabel? hudTitle;
            private string? hudTitleText;
            private TopiaForgeLabel? fallbackTitle;
            private TopiaForgeLabel? fallbackMessage;
            private UiLayoutException? layoutProblem;
            private int noticeDockVersion;
            private UiHudOverflowEpisode overflowEpisode;
            private string? overflowToast;
            private bool hudNoticeOverflowing;

            internal void SetHudNoticeSource(string? packageDisplayName, UiHudAnchor anchor, UiHudVisibility visibility) =>
                overflowToast = UiHudOverflowNotice.Text(packageDisplayName, anchor, visibility);

            private static void PrepareStagedTheme(TopiaForgeContainer stage, UiThemeDescriptor? descriptor,
                Func<UiTextStyle, TMP_FontAsset?>? fonts = null)
            {
                var slot = stage.Go.AddComponent<TopiaForgeLayerSlot>();
                slot.SelectTheme(descriptor ?? UiThemePackParser.Default, fonts);
            }

            private void ObserveLayout()
            {
                if (observingLayout) return;
                CanvasRoot.AddComponent<UiSurfaceLayoutFrame>().Initialize(RefreshChangedLayout);
                observingLayout = true;
            }

            private void RefreshChangedLayout()
            {
                var view = widget;
                var root = compositionRoot;
                if (view == null || (root == null && placement == null) || !presence.IsLive || lifetime.IsStopping) return;
                var stamp = new ContentStamp(view, callbacks.ClockEpoch(), bodyVersion, artworkVersion, placement?.DockVersion ?? 0, OuterScrollViewport);
                var occupancyChanged = layoutFallback != null && placement is { } dock && dock.Version != noticeDockVersion;
                if (committedStamp.Matches(stamp) && !occupancyChanged) return;

                try
                {
                    if (root != null) RefreshRetainedComposition(root, stamp.ClockEpoch);
                    else if (!RefreshHudChrome()) return;
                }
                catch (Exception error)
                {
                    try { logger.Error(error, "The UI surface '" + Id + "' could not refresh its layout."); }
                    catch { }
                }
                committedStamp = stamp;
            }

            private bool RefreshHudChrome()
            {
                var parent = compositionParent;
                if (parent == null || placement is not { } dock || !dock.TryGetViewportSizes(dockHandle, out var frame, out _))
                    return false; // A new canvas can still be waiting for its first viewport.
                var group = parent.Go.GetComponent<VerticalLayoutGroup>();
                try
                {
                    MeasureHudContent(frame, group?.padding.horizontal ?? 0, group?.padding.vertical ?? 0,
                        group?.spacing ?? 0f, includesComposition: false);
                }
                catch (UiLayoutException error)
                {
                    ShowLayoutFallback(parent, null, error);
                    return true;
                }
                RemoveLayoutFallback();
                return true;
            }

            private UiLayoutConstraints MeasureContentArea(TopiaForgeContainer parent, TopiaForgeWidget view)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(view.Rect);
                var rect = parent.Rect.rect;
                var group = parent.Go.GetComponent<VerticalLayoutGroup>();
                var padding = group == null ? null : group.padding;
                if (placement is { } dock)
                {
                    if (!dock.TryGetViewportSizes(dockHandle, out var physical, out _))
                        throw new UiLayoutException(UiLayoutFailure.InvalidLayout, "/0", "The HUD viewport is unavailable.");
                    return MeasureHudContent(physical, padding?.horizontal ?? 0, padding?.vertical ?? 0,
                        group?.spacing ?? 0f, includesComposition: true);
                }
                var width = Math.Max(0, rect.width - (padding?.horizontal ?? 0));
                if (window != null || fullscreen != null)
                    return MeasureScrollingContent(parent, width, group);

                var available = rect.height - (padding?.vertical ?? 0);
                for (var index = 0; index < parent.Rect.childCount; index++)
                {
                    if (parent.Rect.GetChild(index) is not RectTransform child || child == compositionRoot?.Rect ||
                        child == layoutFallback?.Rect || (!child.gameObject.activeSelf && child != body?.Rect)) continue;
                    available -= LayoutUtility.GetPreferredHeight(child) + (group == null ? 0f : group.spacing);
                }
                return new UiLayoutConstraints(width, width, 0, Math.Max(0f, available));
            }

            private UiLayoutConstraints MeasureHudContent(UiLayoutExtent frame, int paddingX, int paddingY, float spacing,
                bool includesComposition)
            {
                var width = frame.Width - paddingX;
                var available = frame.Height - paddingY;
                if (width <= 0d || available <= 0d)
                    throw new UiLayoutException(UiLayoutFailure.InvalidLayout, "/0", "The HUD has no usable physical content area.");
                available -= PreferredSize(hudTitle, width).y;
                if (body != null && body.Go.GetComponent<TMP_Text>().text.Length != 0)
                {
                    var bodySize = PreferredSize(body, width);
                    if (bodySize.x > width + 0.001d)
                        throw new UiLayoutException(UiLayoutFailure.ClippedContent, "/body", "The HUD body exceeds its physical viewport.");
                    available -= spacing + bodySize.y;
                }
                if (includesComposition) available -= spacing;
                if (available < 0d)
                    throw new UiLayoutException(UiLayoutFailure.ClippedContent, "/0", "The HUD chrome exceeds its physical viewport.");
                return new UiLayoutConstraints(width, width, 0, available);
            }

            private static Vector2 PreferredSize(TopiaForgeLabel? label, double width)
            {
                if (label == null) return Vector2.zero;
                var text = label.Go.GetComponent<TMP_Text>();
                return text.GetPreferredValues(text.text, (float)width, 0f);
            }

            private void UpdateRetainedLayout(TopiaForgeContainer root)
            {
                var parent = compositionParent;
                var view = widget;
                if (layoutRenderer == null || parent == null || view == null) return;
                UiLayoutConstraints area;
                try
                {
                    area = MeasureContentArea(parent, view);
                    layoutRenderer.Rearrange(area,
                        declaredTheme ?? (remoteComposition ? null : UiThemePackParser.Default));
                }
                catch (UiLayoutException error)
                {
                    if (placement != null && layoutRenderer.HasRetainedImageNotice)
                    {
                        ShowCompositionLayout(root, needsNoticeRoom: true, allowAlternateImageNotice: true);
                        return;
                    }

                    RememberOuterScrollOffset();
                    SuspendGraphConnections();
                    ShowLayoutFallback(parent, root, error);
                    return;
                }
                var needsNoticeRoom = layoutRenderer.UnavailableImagePath != null &&
                    !CanShowImageNotice(root, area);
                ShowCompositionLayout(root, needsNoticeRoom, allowAlternateImageNotice: true);
            }

            private void ShowCompositionLayout(TopiaForgeContainer root, bool needsNoticeRoom, bool allowAlternateImageNotice)
            {
                if (layoutRenderer?.UnavailableImagePath is string imagePath && compositionParent is { } parent)
                {
                    RememberOuterScrollOffset();
                    SuspendGraphConnections();
                    ShowLayoutNotice(parent, root, UiImageNotice.Text, imagePath,
                        preferContentViewport: !needsNoticeRoom, canMoveImageNotice: allowAlternateImageNotice);
                    return;
                }
                var recovering = layoutFallback != null;
                RemoveLayoutFallback();
                root.SetShown(true);
                RestoreOuterScrollOffset();
                if (recovering) SyncGraphConnections();
            }

            private bool CanShowImageNotice(TopiaForgeContainer stage, UiLayoutConstraints areas)
            {
                if (areas.MaxWidth <= 0) return false;
                var probe = stage.Column(TopiaForgeGap.None, TopiaForgeGap.None);
                try
                {
                    PrepareStagedTheme(probe, null);
                    var label = probe.Label(UiImageNotice.Text, TopiaForgeTextStyle.Body);
                    var text = label.Go.GetComponent<TMP_Text>();
                    var measured = text.GetPreferredValues(text.text, (float)areas.MaxWidth, 0f);
                    var reclaimed = body == null ? 0f : LayoutUtility.GetPreferredHeight(body.Rect);
                    return measured.x <= areas.MaxWidth && measured.y <= areas.MaxHeight + reclaimed;
                }
                finally { probe.Destroy(); }
            }

            private void ShowLayoutFallback(TopiaForgeContainer parent, TopiaForgeContainer? root, UiLayoutException error) =>
                ShowLayoutNotice(parent, root, UiHudLayoutNotice.Text, error.Path, error);

            private void ShowLayoutNotice(TopiaForgeContainer parent, TopiaForgeContainer? root, string message, string path,
                UiLayoutException? error = null, bool preferContentViewport = false, bool canMoveImageNotice = false)
            {
                root?.SetShown(false);
                var noticeChanged = layoutFallback == null || (layoutProblem == null) != (error == null);
                layoutProblem = error;
                if (layoutFallback == null)
                {
                    body?.SetShown(false);
                    hudTitle?.SetShown(false);
                    var fallback = parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                    PrepareStagedTheme(fallback, null);
                    var background = fallback.FreeImage("Layout fallback background");
                    background.Rect.SetAsFirstSibling();
                    TopiaForgeAnchors.Stretch(background.Rect);
                    background.SetColor(background.ResolvedTheme.Surface);
                    if (hudTitleText != null)
                    {
                        fallbackTitle = fallback.Label(hudTitleText, TopiaForgeTextStyle.Heading).PlainText();
                        fallbackTitle.Go.name = "LayoutNoticeTitle";
                    }
                    fallbackMessage = fallback.Label(message, TopiaForgeTextStyle.Body).PlainText();
                    layoutFallback = fallback;
                }
                else if (noticeChanged)
                {
                    fallbackMessage!.SetText(message);
                }
                if (noticeChanged)
                {
                    fallbackMessage!.Go.name = error == null ? "Label" : "LayoutNoticeMessage";
                    try { logger.Warn("UI surface '" + Id + "' is showing '" + message + "' at " + path + "."); }
                    catch { }
                }

                if (placement is not { } dock) return;
                var group = parent.Go.GetComponent<VerticalLayoutGroup>();
                var marginX = group?.padding.horizontal ?? 0;
                var marginY = group?.padding.vertical ?? 0;
                bool fits;
                if (error != null)
                {
                    fits = dock.SetNoticeRoom(dockHandle, UiHudLayoutNotice.PreferredWidth, NoticeHeight);
                }
                else
                {
                    var preferredWidth = canMoveImageNotice ? UiImageNotice.RemoteHudWidth : 0d;
                    fits = dock.SetNoticeRoom(dockHandle, preferredWidth, NoticeHeight,
                        useContentViewport: preferContentViewport, preferredHeight: UiImageNotice.RemoteHudHeight);
                }
                layoutFallback.SetShown(fits);
                dock.SetPanelPaintShown(dockHandle, fits);
                hudNoticeOverflowing = !fits;
                if (hudNoticeOverflowing && IsVisible && overflowToast != null && overflowEpisode.TryQueue())
                    TopiaForgeToasts.ShowPlain(overflowToast, TopiaForgeTone.Neutral);
                noticeDockVersion = dock.Version;

                double NoticeHeight(double outerWidth)
                {
                    var usableWidth = outerWidth - marginX;
                    if (usableWidth <= 0d) return double.PositiveInfinity;
                    var title = PreferredSize(fallbackTitle, usableWidth);
                    var message = PreferredSize(fallbackMessage, usableWidth);
                    if (Math.Max(title.x, message.x) > usableWidth + 0.001d) return double.PositiveInfinity;
                    return marginY + message.y + title.y + (fallbackTitle == null ? 0d : (float)TopiaForgeGap.Sm);
                }
            }

            private void RemoveLayoutFallback()
            {
                placement?.ApplyContentViewport(dockHandle);
                var fallback = layoutFallback;
                if (fallback == null) return;
                placement?.SetPanelPaintShown(dockHandle, true);
                ClearLayoutNotice();
                hudTitle?.SetShown(true);
                if (body is { } label)
                    label.SetShown(placement == null || label.Go.GetComponent<TMP_Text>().text.Length != 0);
                try { fallback.Destroy(); }
                catch (Exception error)
                {
                    try { fallback.SetShown(false); }
                    catch { }
                    try { logger.Error(error, "TopiaForgeUi could not remove a recovered layout notice."); }
                    catch { }
                }
            }

            private void ClearLayoutNotice()
            {
                layoutFallback = null;
                layoutProblem = null;
                fallbackTitle = null;
                fallbackMessage = null;
                hudNoticeOverflowing = false;
                overflowEpisode.Recover();
            }
        }
    }

    internal sealed class UiSurfaceLayoutFrame : MonoBehaviour
    {
        private Action? refresh;
        internal void Initialize(Action refreshLayout) => refresh = refreshLayout;
        private void LateUpdate() => refresh?.Invoke();
        private void OnDestroy() => refresh = null;
    }
}
