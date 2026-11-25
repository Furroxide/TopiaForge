using System;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    public static partial class TopiaForgeToasts
    {
        private static TopiaForgeEffectiveAccessibility ViewerSettings(ToastView view) =>
            (view.Content.Viewer ?? layer!.Host).EffectiveAccessibility;

        private static void ApplyAppearance(ToastView view, TopiaForgeEffectiveAccessibility settings)
        {
            var host = layer!.Host;
            var theme = host.Theme(TopiaForgeScheme.Hud);
            if (settings.HighContrast && !theme.HighContrast)
                theme = contrastTheme ??= new TopiaForgeResolvedTheme(TopiaForgeScheme.Hud, null, true, TopiaForgeTheme.Version);
            view.PaintedTheme = theme;
            view.UiScale = settings.UiScale;
            view.HighContrast = settings.HighContrast;
            view.Scale = settings.UiScale / host.EffectiveUiScale;
            view.Root!.Rect.localScale = new Vector3(view.Scale, view.Scale, 1f);
            var label = view.Label!;
            var glyph = view.Glyph!;
            var tone = view.Content.Tone;
            (UiGlyph? Mark, UiTone Ink) cue = tone switch
            {
                TopiaForgeTone.Success => (UiGlyph.Check, UiTone.Success),
                TopiaForgeTone.Warning => (UiGlyph.Warning, UiTone.Warning),
                TopiaForgeTone.Danger => (UiGlyph.Cross, UiTone.Danger),
                _ => (null, UiTone.Neutral),
            };
            var mark = cue.Mark;
            var readability = Math.Max(1f, settings.UiScale) / settings.UiScale;
            var edge = GlyphSize * readability;
            glyph.enabled = mark.HasValue;
            glyph.sprite = mark is { } icon ? TopiaForgeSprites.Glyph(icon) : null;
            glyph.rectTransform.sizeDelta = new Vector2(edge, edge);
            glyph.color = theme.ForegroundOn(UiPaintRole.Raised, cue.Ink);

            TopiaForgeTypography.ApplyControlText(label, theme, TopiaForgeTextStyle.Label, settings.UiScale);
            var leading = mark.HasValue ? edge + StackGap * readability : 0f;
            TopiaForgeAnchors.Stretch(label.rectTransform, TextInsetX + leading, TextInsetY, TextInsetX, TextInsetY);
            var width = ToastWidth - (2f * TextInsetX) - leading;
            var textHeight = Math.Min(label.GetPreferredValues(view.Content.Text, width, 0f).y,
                label.GetPreferredValues(ThreeLines, width, 0f).y);
            var height = Math.Max(ToastHeight, Math.Max(textHeight, mark.HasValue ? edge : 0f) + (2f * TextInsetY));
            view.Root!.Rect.sizeDelta = new Vector2(ToastWidth, height);

            view.Fill!.color = theme.SurfaceAlt;
            var stroke = theme.HighContrast ? TopiaForgeTokens.BorderStrong : TopiaForgeTokens.BorderStandard;
            view.Ring!.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Control, stroke);
            view.Ring.color = mark.HasValue ? glyph.color : tone == TopiaForgeTone.Neutral
                ? theme.BoundaryOn(UiPaintRole.Raised) : theme.ToneColor(tone);
            label.color = theme.ForegroundOn(UiPaintRole.Raised);
        }
    }
}
