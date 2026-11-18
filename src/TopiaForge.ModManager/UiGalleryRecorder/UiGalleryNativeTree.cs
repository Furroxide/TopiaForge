using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    /// <summary>Reads known native widgets beneath a verified Gallery composition.</summary>
    internal sealed partial class UiGalleryNativeTree
    {
        private readonly Canvas canvas;
        private readonly Camera? camera;
        private readonly TopiaForgeResolvedTheme theme;
        private readonly bool fallback;
        private readonly UiCaptureRect screen;
        private readonly Vector3[] corners = new Vector3[4];
        private readonly List<Observation> observations = new List<Observation>();
        private readonly List<UiCaptureRect> groups = new List<UiCaptureRect>();
        private readonly List<UiGalleryFontFact> fonts = new List<UiGalleryFontFact>();

        internal UiGalleryNativeTree(Canvas canvas, TopiaForgeResolvedTheme theme, TopiaForgeEffectiveAccessibility accessibility, bool fallback)
        {
            this.canvas = canvas;
            camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            this.theme = theme;
            this.fallback = fallback;
            screen = new UiCaptureRect(0, 0, Screen.width, Screen.height);
            Accessibility = accessibility;
        }

        internal int Width => (int)screen.Width;
        internal int Height => (int)screen.Height;
        internal float PixelsPerUnit => canvas.scaleFactor;
        internal TopiaForgeEffectiveAccessibility Accessibility { get; }
        internal TopiaForgeScheme Scheme => theme.Scheme;
        internal UiThemeDescriptor Descriptor => theme.Descriptor ?? UiThemePackParser.Default;
        internal IReadOnlyList<UiGalleryFontFact> Fonts => fonts;
        internal float? SurfaceAlpha { get; private set; }

        internal bool TryCapture(UiSurfaceRequest fixture, RectTransform surface, RectTransform composition, out UiTreeCapture? result)
        {
            result = null;
            if (!surface.IsChildOf(canvas.transform) || !composition.IsChildOf(surface) || fixture.Content == null ||
                !SingleChild(composition, out var native)) return false;
            var fill = AncestorFill(composition);
            Add("/", fixture.Id, fixture.Kind.ToString(), composition, EmptyContent, new UiCaptureNodePaint(null, fill));
            if (fixture.Kind == UiSurfaceKind.Window && !WindowTitle(fixture, surface, fill)) return false;
            if (!Visit(fixture.Content, native!, "/0", fill)) return false;
            var covered = new bool[groups.Count];
            for (var later = 1; later < groups.Count; later++)
                for (var earlier = 0; earlier < later; earlier++)
                    covered[earlier] |= groups[later].Overlaps(groups[earlier]);

            var nodes = new List<UiCapturedNode>(observations.Count);
            foreach (var item in observations)
                nodes.Add(new UiCapturedNode(item.Path, item.Id, item.Kind, item.Bounds, item.Content, item.Paint,
                    Descriptor.Id, fallback, item.Rendered, item.Clipped, item.Group >= 0 && covered[item.Group]));
            result = new UiTreeCapture(nodes, theme.HighContrast, 40f * PixelsPerUnit);
            return true;
        }

        private bool WindowTitle(UiSurfaceRequest fixture, RectTransform surface, UiThemeColor? background)
        {
            var bar = surface.Find("TitleBar")?.GetComponent<Image>();
            var label = bar?.transform.Find("Title")?.GetComponent<TMP_Text>();
            return bar != null && Text("/title", null, "SurfaceTitle", label, fixture.Title, UiTextStyle.Heading,
                Over(bar, background));
        }

        private bool Visit(UiNode fixture, RectTransform native, string path, UiThemeColor? backdrop)
        {
            path = NodePath(path, fixture.Id);
            switch (fixture)
            {
                case UiLayoutNode linear:
                    Add(path, fixture.Id, fixture.GetType().Name, native, EmptyContent, default);
                    if (native.childCount != linear.Children.Count) return false;
                    for (var index = 0; index < linear.Children.Count; index++)
                        if (native.GetChild(index) is not RectTransform child ||
                            !Visit(linear.Children[index], child, ChildPath(path, index), backdrop)) return false;
                    return true;
                case UiScroll scroll:
                    Add(path, fixture.Id, nameof(UiScroll), native, EmptyContent, default);
                    var viewportContent = native.GetComponent<ScrollRect>()?.content;
                    return viewportContent != null && viewportContent.IsChildOf(native) &&
                        SingleChild(viewportContent, out var inner) && Visit(scroll.Content, inner!, ChildPath(path, 0), backdrop);
                case UiSplitPane split:
                    Add(path, fixture.Id, nameof(UiSplitPane), native, EmptyContent, default);
                    return native.childCount == 2 && SingleChild(native.GetChild(0), out var primary) &&
                        SingleChild(native.GetChild(1), out var secondary) &&
                        Visit(split.Primary, primary!, ChildPath(path, 0), backdrop) &&
                        Visit(split.Secondary, secondary!, ChildPath(path, 1), backdrop);
                case UiText text:
                    var labelStart = observations.Count;
                    if (!Text(path, fixture.Id, nameof(UiText), native.GetComponent<TMP_Text>(), text.Text, text.Style, backdrop))
                        return false;
                    RegisterGroup(labelStart);
                    return true;
                default:
                    var controlStart = observations.Count;
                    if (!Control(fixture, native, path, backdrop)) return false;
                    RegisterGroup(controlStart);
                    return true;
            }
        }

        private bool Text(string path, string? id, string kind, TMP_Text? label, string authored, UiTextStyle style,
            UiThemeColor? fill, UiCaptureNodeFlags flags = UiCaptureNodeFlags.None, UiCaptureNodePaint paint = default,
            string? nativeInputValue = null)
        {
            if (label == null || label.font == null) return false;
            var text = nativeInputValue != null
                ? UiGalleryCaptureFixture.SafeAuthoredFieldValue(nativeInputValue, label.text, authored)
                : UiGalleryCaptureFixture.SafeDisplayText(label.text, authored, Descriptor.GetText(style).IsUppercase);
            if (text != null && (label.fontStyle & FontStyles.UpperCase) != 0) text = text.ToUpperInvariant();
            if (text == null)
            {
                Add(path, id, kind, label.rectTransform,
                    new UiCaptureNodeContent(string.Empty, authored, 0, 0, flags | UiCaptureNodeFlags.InvalidLayout),
                    new UiCaptureNodePaint(null, fill), visible: label.isActiveAndEnabled);
                return true;
            }

            var height = 0f;
            var scale = VerticalScale(label.rectTransform);
            var bounds = Bounds(label.rectTransform);
            var clipped = false;
            if (label.isActiveAndEnabled && text.Length != 0)
            {
                label.ForceMeshUpdate();
                var info = label.textInfo;
                for (var line = 0; line < info.lineCount; line++)
                    height = Mathf.Max(height, (info.lineInfo[line].ascender - info.lineInfo[line].descender) * scale);
                if (info.characterCount == 0) return false;
                for (var index = 0; index < info.characterCount; index++)
                {
                    var character = info.characterInfo[index];
                    if (!character.isVisible) continue;
                    var first = ScreenPoint(label.rectTransform.TransformPoint(character.bottomLeft));
                    var second = ScreenPoint(label.rectTransform.TransformPoint(character.topRight));
                    var ink = AtPoints(first, second);
                    if (!ClipContains(bounds, ink)) clipped = true;
                }
                var face = label.font.faceInfo;
                fonts.Add(new UiGalleryFontFact(path, face.familyName, face.styleName, label.fontSize * scale,
                    face.ascentLine, face.descentLine, height));
            }

            var readable = Descriptor.GetText(style).ReferenceSize * PixelsPerUnit *
                (Mathf.Max(1f, Accessibility.UiScale) / Accessibility.UiScale);
            var content = new UiCaptureNodeContent(text, authored, height, readable, flags);
            Add(path, id, kind, label.rectTransform, content, new UiCaptureNodePaint(
                ToRgb(EffectiveColor(label)), fill, paint.CornerRadius, paint.BorderWidth, paint.ShadowX, paint.ShadowY),
                visible: label.isActiveAndEnabled && !label.canvasRenderer.cull, contentClipped: clipped);
            return true;
        }

        private void Add(string path, string? id, string kind, RectTransform rect, UiCaptureNodeContent content,
            UiCaptureNodePaint paint, bool visible = true, bool contentClipped = false)
        {
            var bounds = Bounds(rect);
            var (clip, scrollable) = Clip(rect);
            if (scrollable && (content.Flags & UiCaptureNodeFlags.FocusIndicator) == 0 &&
                bounds.X >= clip.X - 0.01f && bounds.Right <= clip.Right + 0.01f && !contentClipped)
                content = new UiCaptureNodeContent(content.Text, content.SemanticLabel, content.TextHeight,
                    content.ExpectedTextHeight, content.Flags | UiCaptureNodeFlags.ScrollableClip);
            var rendered = visible && rect.gameObject.activeInHierarchy && bounds.Overlaps(clip);
            observations.Add(new Observation(path, id, kind, bounds, bounds.Intersect(clip), content, paint, rendered,
                rendered && (!ClipContains(clip, bounds) || contentClipped)));
        }

        private void RegisterGroup(int start)
        {
            var footprint = default(UiCaptureRect);
            for (var index = start; index < observations.Count; index++)
            {
                var sample = observations[index];
                sample.Group = groups.Count;
                observations[index] = sample;
                if (sample.Rendered) footprint = Union(footprint, sample.VisibleBounds);
            }
            groups.Add(footprint);
        }

        private static UiCaptureRect Union(UiCaptureRect first, UiCaptureRect second)
        {
            if (first.Width <= 0f || first.Height <= 0f) return second;
            if (second.Width <= 0f || second.Height <= 0f) return first;
            var x = Mathf.Min(first.X, second.X);
            var y = Mathf.Min(first.Y, second.Y);
            return new UiCaptureRect(x, y, Mathf.Max(first.Right, second.Right) - x, Mathf.Max(first.Bottom, second.Bottom) - y);
        }

        private (UiCaptureRect Bounds, bool Scrollable) Clip(RectTransform item)
        {
            var clip = screen;
            var scrollable = false;
            for (var ancestor = item.parent; ancestor != null && ancestor != canvas.transform; ancestor = ancestor.parent)
            {
                if (ancestor is not RectTransform rect || !rect.TryGetComponent<RectMask2D>(out var mask) || !mask.isActiveAndEnabled) continue;
                clip = clip.Intersect(Bounds(rect));
                var scroll = rect.parent != null ? rect.parent.GetComponent<ScrollRect>() : null;
                scrollable |= scroll != null && scroll.vertical && scroll.viewport == rect;
            }
            return (clip, scrollable);
        }

        private UiThemeColor? AncestorFill(RectTransform composition)
        {
            for (var current = composition.parent; current != null && current != canvas.transform; current = current.parent)
            {
                var fill = current.Find("Fill")?.GetComponent<Image>();
                if (fill == null || !fill.isActiveAndEnabled) continue;
                var color = EffectiveColor(fill);
                SurfaceAlpha = color.a;
                return color.a >= 0.999f ? ToRgb(color) : null;
            }
            return null;
        }

        private UiCaptureRect Bounds(RectTransform rect)
        {
            rect.GetWorldCorners(corners);
            var first = ScreenPoint(corners[0]);
            var left = first.x;
            var right = first.x;
            var top = first.y;
            var bottom = first.y;
            for (var index = 1; index < corners.Length; index++)
            {
                var point = ScreenPoint(corners[index]);
                left = Mathf.Min(left, point.x);
                right = Mathf.Max(right, point.x);
                top = Mathf.Min(top, point.y);
                bottom = Mathf.Max(bottom, point.y);
            }
            return new UiCaptureRect(left, top, right - left, bottom - top);
        }

        private Vector2 ScreenPoint(Vector3 world)
        {
            var point = RectTransformUtility.WorldToScreenPoint(camera, world);
            return new Vector2(point.x, Height - point.y);
        }

        private float VerticalScale(RectTransform rect) =>
            Vector2.Distance(ScreenPoint(rect.TransformPoint(Vector3.zero)), ScreenPoint(rect.TransformPoint(Vector3.up)));

        private static UiCaptureRect AtPoints(Vector2 first, Vector2 second) =>
            new UiCaptureRect(Mathf.Min(first.x, second.x), Mathf.Min(first.y, second.y),
                Mathf.Abs(first.x - second.x), Mathf.Abs(first.y - second.y));

        private static bool ClipContains(UiCaptureRect bounds, UiCaptureRect ink)
        {
            const float edge = 1f; // Keep subpixel raster padding outside the hard layout decision.
            return ink.X >= bounds.X - edge && ink.Y >= bounds.Y - edge &&
                ink.Right <= bounds.Right + edge && ink.Bottom <= bounds.Bottom + edge;
        }

        private static bool SingleChild(Transform? root, out RectTransform? child)
        {
            child = root != null && root.childCount == 1 ? root.GetChild(0) as RectTransform : null;
            return child != null;
        }

        private static Color EffectiveColor(Graphic graphic) => graphic.color * graphic.canvasRenderer.GetColor();
        private static UiThemeColor ToRgb(Color color)
        {
            var bytes = (Color32)color;
            return new UiThemeColor(bytes.r, bytes.g, bytes.b);
        }

        private static UiThemeColor? Over(Graphic? graphic, UiThemeColor? background)
        {
            if (graphic == null || !graphic.isActiveAndEnabled) return background;
            var color = EffectiveColor(graphic);
            if (color.a <= 0f) return background;
            if (color.a >= 1f) return ToRgb(color);
            if (background is not { } back) return null;
            return ToRgb(Color.Lerp(new Color32(back.R, back.G, back.B, 255), color, color.a));
        }

        private static string NodePath(string path, string? id) =>
            id == null ? path : path + ":" + Uri.EscapeDataString(id);

        private static string ChildPath(string parent, int index) => parent + "/" + index.ToString(CultureInfo.InvariantCulture);

        private static T? Named<T>(RectTransform native, string name) where T : Component
        {
            foreach (var part in native.GetComponentsInChildren<T>(includeInactive: true))
                if (part.name == name) return part;
            return null;
        }

        private static UiCaptureNodeContent State(UiCaptureNodeFlags flags, string label = "") =>
            new UiCaptureNodeContent(string.Empty, label, 0, 0, flags);

        private static readonly UiCaptureNodeContent EmptyContent = State(UiCaptureNodeFlags.None);

        private struct Observation
        {
            internal Observation(string path, string? id, string kind, UiCaptureRect bounds, UiCaptureRect visibleBounds,
                UiCaptureNodeContent content, UiCaptureNodePaint paint, bool rendered, bool clipped)
            {
                Path = path;
                Id = id;
                Kind = kind;
                Bounds = bounds;
                VisibleBounds = visibleBounds;
                Content = content;
                Paint = paint;
                Rendered = rendered;
                Clipped = clipped;
                Group = -1;
            }

            internal string Path { get; }
            internal string? Id { get; }
            internal string Kind { get; }
            internal UiCaptureRect Bounds { get; }
            internal UiCaptureRect VisibleBounds { get; }
            internal UiCaptureNodeContent Content { get; }
            internal UiCaptureNodePaint Paint { get; }
            internal bool Rendered { get; }
            internal bool Clipped { get; }
            internal int Group { get; set; }
        }
    }

    internal readonly struct UiGalleryFontFact
    {
        internal UiGalleryFontFact(string path, string family, string style, float sizePixels, float ascender, float descender,
            float measuredHeight)
        {
            Path = path;
            Family = family;
            Style = style;
            SizePixels = sizePixels;
            Ascender = ascender;
            Descender = descender;
            MeasuredHeight = measuredHeight;
        }

        internal string Path { get; }
        internal string Family { get; }
        internal string Style { get; }
        internal float SizePixels { get; }
        internal float Ascender { get; }
        internal float Descender { get; }
        internal float MeasuredHeight { get; }
    }
}
