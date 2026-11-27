using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private string? announcement;
        private float? scrimOpacity;

        internal int? PresentationOrder { get; private set; }
        internal bool? PresentationTakesInput { get; private set; }
        internal bool? OverlayCovered { get; private set; }
        internal bool? OverlayDismissible { get; private set; }

        internal bool TryCaptureOverlay(UiSurfaceRequest fixture, TopiaForgeOverlay view, RectTransform composition,
            TMP_Text? body, out UiTreeCapture? result)
        {
            result = null;
            if (fixture.Kind != UiSurfaceKind.Overlay || fixture.Dismissible != view.Dismissible ||
                !view.Rect.IsChildOf(canvas.transform) || !composition.IsChildOf(view.Content.Rect) ||
                view.Rect.Find("Overlay panel") is not RectTransform panel ||
                view.Rect.Find("Overlay scrim")?.GetComponent<Image>() is not { isActiveAndEnabled: true } scrim ||
                panel.Find("Surface")?.GetComponent<Image>() is not { isActiveAndEnabled: true } fill ||
                !SingleChild(composition, out var native) || fixture.Content == null ||
                !Mathf.Approximately(panel.anchoredPosition.y, 0f) ||
                scrim.GetComponent<CanvasGroup>() is not { alpha: >= 0.999f }) return false;

            PresentationOrder = canvas.sortingOrder;
            PresentationTakesInput = canvas.GetComponent<GraphicRaycaster>() is { isActiveAndEnabled: true };
            OverlayCovered = TopiaForgeOverlayStack.IsCovered(view);
            OverlayDismissible = view.Dismissible;
            var scrimColor = EffectiveColor(scrim);
            scrimOpacity = scrimColor.a;
            Add("/scrim", null, "OverlayScrim", scrim.rectTransform, State(UiCaptureNodeFlags.Decorative),
                new UiCaptureNodePaint(null, ToRgb(scrimColor)));
            var backdrop = Panel("/", fixture.Id, "Overlay", panel, fill, "Boundary",
                view.HasNavigationFocus ? UiCaptureNodeFlags.Selected : UiCaptureNodeFlags.None);
            if (!Text("/title", null, "SurfaceTitle", view.TitleLabel.Go.GetComponent<TMP_Text>(), fixture.Title,
                UiTextStyle.Title, backdrop) ||
                !Text("/body", null, "SurfaceBody", body, fixture.Body, UiTextStyle.Body, backdrop) ||
                !Visit(fixture.Content, native!, "/0", backdrop)) return false;
            result = CompleteCapture();
            return true;
        }

        private bool? usesDeclaredStingerFaces;

        internal bool TryCaptureStinger(string id, UiStingerRequest fixture, TopiaForgeStinger view,
            double remainingSeconds, out UiTreeCapture? result)
        {
            result = null;
            var root = view.Rect;
            var authored = fixture.Subtitle.Length == 0 ? fixture.Title : fixture.Title + ". " + fixture.Subtitle;
            if (!root.IsChildOf(canvas.transform) || !root.gameObject.activeInHierarchy || view.Announcement != authored ||
                root.Find("Surface")?.GetComponent<Image>() is not { isActiveAndEnabled: true } fill) return false;
            PresentationOrder = canvas.sortingOrder;
            PresentationTakesInput = canvas.GetComponent<GraphicRaycaster>() is { isActiveAndEnabled: true };
            announcement = authored;
            usesDeclaredStingerFaces = view.UsesDeclaredFaces;
            var backdrop = Panel("/", id, "Stinger", root, fill, "Boundary", remaining: remainingSeconds);
            var flags = fixture.Glyph is { } icon
                ? CapturePresentationGlyph(root, "Glyph", icon, backdrop) : UiCaptureNodeFlags.None;
            if (!Text("/title", null, "StingerTitle", root.Find("Title")?.GetComponent<TMP_Text>(), fixture.Title,
                UiTextStyle.Banner, backdrop, flags, textTheme: view.Typography) ||
                fixture.Subtitle.Length != 0 && !Text("/subtitle", null, "StingerSubtitle",
                    root.Find("Subtitle")?.GetComponent<TMP_Text>(), fixture.Subtitle, UiTextStyle.Title, backdrop,
                    textTheme: view.Typography)) return false;
            result = CompleteCapture();
            return true;
        }

        internal bool TryCaptureToast(string id, string authored, UiTone tone, TopiaForgeContainer view, out UiTreeCapture? result)
        {
            result = null;
            var root = view.Rect;
            if (!UiGalleryCaptureFixture.MatchesToast(authored, tone) || !root.IsChildOf(canvas.transform) ||
                !root.gameObject.activeInHierarchy ||
                root.Find("Fill")?.GetComponent<Image>() is not { isActiveAndEnabled: true } fill ||
                root.Find("Label")?.GetComponent<TMP_Text>() is not { richText: false } label) return false;

            PresentationOrder = canvas.sortingOrder;
            PresentationTakesInput = canvas.GetComponent<GraphicRaycaster>() is { isActiveAndEnabled: true };
            var backdrop = Panel("/", id, "Toast", root, fill, "Ring", state: tone.ToString().ToLowerInvariant());
            if (root.Find("Ring")?.GetComponent<Image>() is { isActiveAndEnabled: true } ring)
                Add("/boundary", null, "ToastBoundary", ring.rectTransform, State(UiCaptureNodeFlags.Decorative),
                    new UiCaptureNodePaint(ToRgb(EffectiveColor(ring)), backdrop));
            var cue = tone switch
            {
                UiTone.Success => UiGlyph.Check,
                UiTone.Warning => UiGlyph.Warning,
                UiTone.Danger => UiGlyph.Cross,
                _ => (UiGlyph?)null,
            };
            var flags = cue is { } expected ? CapturePresentationGlyph(root, "Tone glyph", expected, backdrop) : UiCaptureNodeFlags.None;
            if (!Text("/label", null, "ToastLabel", label, authored, UiTextStyle.Label, backdrop, flags)) return false;
            result = CompleteCapture();
            return true;
        }

        private UiThemeColor? Panel(string path, string id, string kind, RectTransform root, Image fill, string borderName,
            UiCaptureNodeFlags flags = UiCaptureNodeFlags.None, double? remaining = null, string state = "shown")
        {
            var color = EffectiveColor(fill);
            SurfaceAlpha = color.a;
            UiThemeColor? backdrop = color.a >= 0.999f ? ToRgb(color) : null;
            var decoration = Decoration(root, fill, borderName);
            Add(path, id, kind, fill.rectTransform, new UiCaptureNodeContent(string.Empty, string.Empty,
                0, 0, flags, state: state, remainingSeconds: remaining), new UiCaptureNodePaint(null, backdrop,
                decoration.CornerRadius, decoration.BorderWidth, decoration.ShadowX, decoration.ShadowY));
            return backdrop;
        }

        private UiCaptureNodeFlags CapturePresentationGlyph(RectTransform root, string name, UiGlyph expected, UiThemeColor? backdrop)
        {
            if (root.Find(name)?.GetComponent<Image>() is not { isActiveAndEnabled: true } glyph ||
                glyph.canvasRenderer.cull || glyph.sprite != TopiaForgeSprites.Glyph(expected) ||
                EffectiveColor(glyph).a < 0.999f) return UiCaptureNodeFlags.RequiresNonColorCue;
            Add("/glyph", null, "Glyph", glyph.rectTransform, State(UiCaptureNodeFlags.SemanticMark, expected.ToString()),
                new UiCaptureNodePaint(ToRgb(EffectiveColor(glyph)), backdrop));
            return UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
        }
    }
}
