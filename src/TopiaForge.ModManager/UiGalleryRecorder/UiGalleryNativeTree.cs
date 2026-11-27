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
        private readonly List<(UiCaptureRect Bounds, bool Protected)> groups = new List<(UiCaptureRect, bool)>();
        private readonly List<UiGalleryFontFact> fonts = new List<UiGalleryFontFact>();
        private bool onlyUnavailableImages;
        private string? dropdownChoicesPath;

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

        internal bool TryCapture(UiSurfaceRequest fixture, RectTransform surface, RectTransform composition,
            RectTransform? imageNotice, out UiTreeCapture? result, TopiaForgeGraphConnections? connections = null,
            string? queuedNotice = null)
        {
            result = null;
            if (!surface.IsChildOf(canvas.transform) || !composition.IsChildOf(surface) || fixture.Content == null ||
                !SingleChild(composition, out var native)) return false;
            onlyUnavailableImages = imageNotice != null;
            var fill = AncestorFill(composition);
            if (imageNotice == null || fixture.Kind != UiSurfaceKind.Hud)
                Add("/", fixture.Id, fixture.Kind.ToString(), composition, EmptyContent, new UiCaptureNodePaint(null, fill));
            if (fixture.Kind == UiSurfaceKind.Window && !WindowTitle(fixture, surface, fill)) return false;
            if (connections != null && !GraphConnectionsLauncher(connections)) return false;
            if (connections?.IsOpen == true)
            {
                if (!GraphConnectionsList(fixture.Content, connections, fill)) return false;
            }
            else if (!Visit(fixture.Content, native!, "/0", fill)) return false;
            if (imageNotice != null && (!imageNotice.IsChildOf(surface) || !CaptureImageNotice(fixture, imageNotice, queuedNotice)))
                return false;
            result = CompleteCapture();
            return true;
        }

        private UiTreeCapture CompleteCapture()
        {
            var covered = new bool[groups.Count];
            for (var later = 1; later < groups.Count; later++)
                for (var earlier = 0; earlier < later; earlier++)
                    covered[earlier] |= groups[earlier].Protected && groups[later].Bounds.Overlaps(groups[earlier].Bounds);

            var nodes = new List<UiCapturedNode>(observations.Count);
            foreach (var item in observations)
            {
                var content = item.Content;
                if (dropdownChoicesPath != null && (content.Flags & UiCaptureNodeFlags.HitTarget) != 0 &&
                    !item.Path.StartsWith(dropdownChoicesPath, StringComparison.Ordinal))
                    content = content.WithFlags((content.Flags & ~UiCaptureNodeFlags.HitTarget) | UiCaptureNodeFlags.Disabled);
                nodes.Add(new UiCapturedNode(item.Path, item.Id, item.Kind, item.Bounds, content, item.Paint,
                    item.KitPaint ? UiThemePackParser.Default.Id : Descriptor.Id,
                    fallback || item.LocalFallback, item.Rendered, item.Clipped, item.Occluded || (item.Group >= 0 && covered[item.Group])));
            }
            return new UiTreeCapture(nodes, theme.HighContrast, 40f * PixelsPerUnit);
        }

        private bool WindowTitle(UiSurfaceRequest fixture, RectTransform surface, UiThemeColor? background)
        {
            var bar = surface.Find("TitleBar")?.GetComponent<Image>();
            var label = bar?.transform.Find("Title")?.GetComponent<TMP_Text>();
            return bar != null && Text("/title", null, "SurfaceTitle", label, fixture.Title, UiTextStyle.Heading,
                Over(bar, background));
        }

        private bool VisitLeaf(UiNode fixture, RectTransform native, string path, UiThemeColor? backdrop)
        {
            if (onlyUnavailableImages) return true;
            var start = observations.Count;
            var captured = fixture is UiText text
                ? Text(path, fixture.Id, nameof(UiText), native.GetComponent<TMP_Text>(), text.Text, text.Style, backdrop)
                : Control(fixture, native, path, backdrop);
            if (!captured) return false;
            RegisterGroup(start);
            return true;
        }

        private bool Text(string path, string? id, string kind, TMP_Text? label, string authored, UiTextStyle style,
            UiThemeColor? fill, UiCaptureNodeFlags flags = UiCaptureNodeFlags.None, UiCaptureNodePaint paint = default,
            bool localFallback = false, bool kitPaint = false, string? nativeInputValue = null, string? semanticLabel = null,
            string? state = null, string? keyLabel = null)
        {
            if (label == null || label.font == null) return false;
            var role = (kitPaint ? UiThemePackParser.Default : Descriptor).GetText(style);
            var text = nativeInputValue != null
                ? UiGalleryCaptureFixture.SafeAuthoredFieldValue(nativeInputValue, label.text, authored)
                : UiGalleryCaptureFixture.SafeDisplayText(label.text, authored, role.IsUppercase);
            if (text != null && (label.fontStyle & FontStyles.UpperCase) != 0) text = text.ToUpperInvariant();
            if (text == null)
            {
                Add(path, id, kind, label.rectTransform,
                    new UiCaptureNodeContent(string.Empty, semanticLabel ?? authored, 0, 0, flags | UiCaptureNodeFlags.InvalidLayout),
                    new UiCaptureNodePaint(null, fill), visible: label.isActiveAndEnabled, localFallback: localFallback, kitPaint: kitPaint);
                return true;
            }

            var height = 0f;
            var scale = VerticalScale(label.rectTransform);
            var bounds = Bounds(label.rectTransform);
            var intended = label.isActiveAndEnabled;
            var clipped = false;
            var noCharacters = false;
            if (intended && text.Length != 0)
            {
                label.ForceMeshUpdate();
                var info = label.textInfo;
                for (var line = 0; line < info.lineCount; line++)
                    height = Mathf.Max(height, (info.lineInfo[line].ascender - info.lineInfo[line].descender) * scale);
                noCharacters = info.characterCount == 0;
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

            var readable = role.ReferenceSize * PixelsPerUnit *
                (Mathf.Max(1f, Accessibility.UiScale) / Accessibility.UiScale);
            var clip = Clip(label.rectTransform, bounds, kind == nameof(UiText) ? Mathf.Max(height, readable) : null);
            if (noCharacters && clip.HasVisibleArea) return false;
            var content = new UiCaptureNodeContent(text, semanticLabel ?? authored, height, readable, flags, state, keyLabel);
            Observe(path, id, kind, bounds, clip, content, new UiCaptureNodePaint(
                ToRgb(EffectiveColor(label)), fill, paint.CornerRadius, paint.BorderWidth, paint.ShadowX, paint.ShadowY),
                visible: intended && !label.canvasRenderer.cull, intended: intended, contentClipped: clipped,
                localFallback: localFallback, kitPaint: kitPaint);
            return true;
        }

        private void Add(string path, string? id, string kind, RectTransform rect, UiCaptureNodeContent content,
            UiCaptureNodePaint paint, bool visible = true, bool contentClipped = false, bool localFallback = false,
            bool kitPaint = false, RectTransform? authoredFrame = null)
        {
            var bounds = Bounds(rect);
            if (authoredFrame != null) bounds = bounds.Intersect(Bounds(authoredFrame));
            Observe(path, id, kind, bounds, Clip(rect, bounds), content, paint, visible,
                rect.gameObject.activeInHierarchy && visible, contentClipped, localFallback, kitPaint);
        }

        private void Observe(string path, string? id, string kind, UiCaptureRect bounds, UiGalleryCaptureClip clip,
            UiCaptureNodeContent content, UiCaptureNodePaint paint, bool visible, bool intended, bool contentClipped = false,
            bool localFallback = false, bool kitPaint = false)
        {
            if (clip.ScrollClipped && !clip.FixedClipped && !contentClipped && (content.Flags & UiCaptureNodeFlags.FocusIndicator) == 0)
                content = content.WithFlags(content.Flags | UiCaptureNodeFlags.ScrollableClip);
            var rendered = visible && intended && clip.HasVisibleArea;
            observations.Add(new Observation(path, id, kind, bounds, clip.Visible, content, paint, rendered,
                intended && (clip.AnyClipping || contentClipped), localFallback, kitPaint));
        }

        private void RegisterGroup(int start, bool protect = true)
        {
            var footprint = default(UiCaptureRect);
            for (var index = start; index < observations.Count; index++)
            {
                var sample = observations[index];
                sample.Group = groups.Count;
                observations[index] = sample;
                if (sample.Rendered) footprint = Union(footprint, sample.VisibleBounds);
            }
            groups.Add((footprint, protect));
        }

        private static UiCaptureRect Union(UiCaptureRect first, UiCaptureRect second)
        {
            if (first.Width <= 0f || first.Height <= 0f) return second;
            if (second.Width <= 0f || second.Height <= 0f) return first;
            var x = Mathf.Min(first.X, second.X);
            var y = Mathf.Min(first.Y, second.Y);
            return new UiCaptureRect(x, y, Mathf.Max(first.Right, second.Right) - x, Mathf.Max(first.Bottom, second.Bottom) - y);
        }

        private UiGalleryCaptureClip Clip(RectTransform item, UiCaptureRect bounds, float? scrollLineHeight = null)
        {
            var clip = new UiGalleryCaptureClip(bounds, scrollLineHeight);
            for (var ancestor = item.parent; ancestor != null && ancestor != canvas.transform; ancestor = ancestor.parent)
            {
                if (ancestor is not RectTransform rect || !rect.TryGetComponent<RectMask2D>(out var mask) || !mask.isActiveAndEnabled) continue;
                var viewport = Bounds(rect);
                var scroll = rect.parent != null ? rect.parent.GetComponent<ScrollRect>() : null;
                UiCaptureRect? scrollContent = null;
                if (scroll != null && scroll.isActiveAndEnabled && scroll.vertical && scroll.viewport == rect &&
                    scroll.content != null && item.IsChildOf(scroll.content))
                {
                    scrollContent = Bounds(scroll.content);
                }
                var padding = mask.padding;
                var aperture = padding == Vector4.zero ? viewport : PaddedMaskBounds(rect, padding);
                clip.Apply(aperture, viewport, scrollContent);
            }
            clip.Apply(screen);
            return clip;
        }

        private UiCaptureRect PaddedMaskBounds(RectTransform rect, Vector4 padding)
        {
            var local = rect.rect;
            var lower = ScreenPoint(rect.TransformPoint(new Vector3(local.xMin + padding.x, local.yMin + padding.y)));
            var upper = ScreenPoint(rect.TransformPoint(new Vector3(local.xMax - padding.z, local.yMax - padding.w)));
            return new UiCaptureRect(lower.x, upper.y, Mathf.Max(0, upper.x - lower.x), Mathf.Max(0, lower.y - upper.y));
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
                UiCaptureNodeContent content, UiCaptureNodePaint paint, bool rendered, bool clipped,
                bool localFallback = false, bool kitPaint = false, bool occluded = false)
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
                Occluded = occluded;
                LocalFallback = localFallback;
                KitPaint = kitPaint;
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
            internal bool Occluded { get; }
            internal bool LocalFallback { get; }
            internal bool KitPaint { get; }
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
