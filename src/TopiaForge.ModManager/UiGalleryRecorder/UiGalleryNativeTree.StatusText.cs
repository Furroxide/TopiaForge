using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private bool ReadStatusText(string path, TMP_Text? label, string authored, UiTextStyle style,
            UiThemeColor expectedInk, UiThemeColor background, out StatusTextSample sample)
        {
            sample = default;
            if (label == null || label.font == null || !ReadPaint(label, out var drawn) ||
                !drawn.Equals(expectedInk)) return false;
            var role = Descriptor.GetText(style);
            var raw = UiGalleryCaptureFixture.SafeDisplayText(label.text, authored, role.IsUppercase);
            if (raw == null || raw.Length == 0) return false;
            var actual = (label.fontStyle & FontStyles.UpperCase) != 0 ? raw.ToUpperInvariant() : raw;
            var nativeStyle = style switch
            {
                UiTextStyle.Label => TopiaForgeTextStyle.Label,
                UiTextStyle.Mono => TopiaForgeTextStyle.Mono,
                _ => TopiaForgeTextStyle.Body,
            };
            var font = theme.RegisteredFont?.Invoke(style) ?? TopiaForgeFonts.ForThemeRole(role.Font, nativeStyle);
            if (font != null && label.font != font) return false;

            var labelRect = label.rectTransform;
            var scale = VerticalScale(labelRect);
            var height = 0f;
            label.ForceMeshUpdate();
            var info = label.textInfo;
            if (info.characterCount != actual.Length) return false;
            for (var line = 0; line < info.lineCount; line++)
                height = Mathf.Max(height, (info.lineInfo[line].ascender - info.lineInfo[line].descender) * scale);

            var ink = default(UiCaptureRect);
            for (var index = 0; index < info.characterCount; index++)
            {
                var character = info.characterInfo[index];
                if (character.character != actual[index] && character.character != raw[index]) return false;
                if (!character.isVisible) continue;
                var bottomLeft = ScreenPoint(labelRect.TransformPoint(character.bottomLeft));
                var topRight = ScreenPoint(labelRect.TransformPoint(character.topRight));
                ink = Union(ink, AtPoints(bottomLeft, topRight));
            }
            if (ink.Width <= 0f || ink.Height <= 0f || height <= 0f) return false;
            var face = label.font.faceInfo;
            fonts.Add(new UiGalleryFontFact(path, face.familyName, face.styleName, label.fontSize * scale,
                face.ascentLine, face.descentLine, height));
            var readable = role.ReferenceSize * PixelsPerUnit * (Mathf.Max(1f, Accessibility.UiScale) / Accessibility.UiScale);
            sample = new StatusTextSample(label, ink, new UiCaptureNodeContent(actual, authored, height, readable,
                UiCaptureNodeFlags.RequiresLabel), new UiCaptureNodePaint(drawn, background), !ClipContains(Bounds(labelRect), ink));
            return true;
        }

        private bool StoreStatusText(string path, string? id, string kind, StatusTextSample text,
            UiCaptureRect? intentionalMask = null, string? state = null, double? seconds = null, float? fraction = null)
        {
            // The two stat labels share one glyph projection. Only the part painted under each intentional mask
            // is compared with the viewport and ancestor clips.
            var bounds = intentionalMask.HasValue ? text.Bounds.Intersect(intentionalMask.Value) : text.Bounds;
            var content = state == null ? text.Content : text.Content.WithStatus(state, seconds, fraction);
            return AddStatusPaint(path, id, kind, text.Label, bounds, content, text.Paint, text.LayoutClipped);
        }

        private bool AddStatusPaint(string path, string? id, string kind, Graphic graphic, UiCaptureRect bounds,
            UiCaptureNodeContent content, UiCaptureNodePaint paint, bool contentClipped = false)
        {
            var clip = Clip(graphic.rectTransform, bounds, content.ExpectedTextHeight);
            if (clip.HasVisibleArea && (!graphic.isActiveAndEnabled || graphic.canvasRenderer.cull)) return false;
            Observe(path, id, kind, bounds, clip, content, paint, visible: true, intended: true,
                contentClipped: contentClipped);
            return true;
        }

        private readonly struct StatusTextSample
        {
            internal StatusTextSample(TMP_Text label, UiCaptureRect bounds, UiCaptureNodeContent content,
                UiCaptureNodePaint paint, bool layoutClipped)
            {
                Label = label;
                Bounds = bounds;
                Content = content;
                Paint = paint;
                LayoutClipped = layoutClipped;
            }

            internal TMP_Text Label { get; }
            internal UiCaptureRect Bounds { get; }
            internal UiCaptureNodeContent Content { get; }
            internal UiCaptureNodePaint Paint { get; }
            internal bool LayoutClipped { get; }
        }
    }
}
