using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        internal bool TryCaptureLayoutNotice(UiSurfaceRequest fixture, RectTransform surface, RectTransform notice,
            UiLayoutException failure, out UiTreeCapture? result, string? queuedNotice = null)
        {
            result = null;
            if (!surface.IsChildOf(canvas.transform) || !notice.IsChildOf(surface) || fixture.Content == null) return false;
            var background = notice.Find("Layout fallback background")?.GetComponent<Graphic>();
            var label = notice.Find("LayoutNoticeMessage")?.GetComponent<TMP_Text>();
            if (background == null || label == null) return false;
            var rendered = label.isActiveAndEnabled;
            var flags = UiCaptureNodeFlags.LayoutDiagnostic;
            if (failure.Failure == UiLayoutFailure.InvalidLayout || (!rendered && queuedNotice == null))
                flags |= UiCaptureNodeFlags.InvalidLayout;
            observations.Add(new Observation(failure.Path, null, "LayoutDiagnostic", default, default,
                new UiCaptureNodeContent(string.Empty, string.Empty, 0f, 0f, flags), default, rendered: false,
                clipped: failure.Failure == UiLayoutFailure.ClippedContent,
                occluded: failure.Failure == UiLayoutFailure.OccludedContent));
            if (queuedNotice != null)
            {
                if (rendered || fixture.Kind != UiSurfaceKind.Hud) return false;
                AddQueuedHudNotice(queuedNotice);
                result = CompleteCapture();
                return true;
            }
            var fill = Over(background, null);
            if (rendered)
            {
                if (label.rectTransform.rect.width <= 0 || label.rectTransform.rect.height <= 0) return false;
                SurfaceAlpha = EffectiveColor(background).a;
                Add("/", fixture.Id, fixture.Kind.ToString(), notice, EmptyContent, new UiCaptureNodePaint(null, fill));
                if (fixture.Kind == UiSurfaceKind.Hud &&
                    !Text("/title", null, "SurfaceTitle", notice.Find("LayoutNoticeTitle")?.GetComponent<TMP_Text>(),
                        fixture.Title, UiTextStyle.Heading, fill, UiCaptureNodeFlags.RequiresLabel)) return false;
            }
            if (!Text("/fallback", null, "LayoutFallback", label, UiHudLayoutNotice.Text, UiTextStyle.Body, fill,
                    UiCaptureNodeFlags.RequiresLabel)) return false;
            result = CompleteCapture();
            return true;
        }

        private void AddQueuedHudNotice(string message) =>
            observations.Add(new Observation("/fallback", null, UiHudOverflowNotice.CaptureKind, default, default,
                new UiCaptureNodeContent(string.Empty, message, 0f, 0f, UiCaptureNodeFlags.LayoutDiagnostic),
                default, rendered: false, clipped: false, localFallback: true, kitPaint: true));
    }
}
