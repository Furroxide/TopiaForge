using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeResolvedTheme
    {
        internal UiThemeDescriptor? Descriptor { get; }

        internal Color DisabledInk => Descriptor is { } descriptor
            ? ToColor(Rgba(descriptor.SelectScheme(Scheme == TopiaForgeScheme.Hud, HighContrast)
                .ResolveForeground(UiTone.Muted, UiPaintRole.Sunken)))
            : TextFaint;

        private static TopiaForgeSchemeColors ApplyPack(TopiaForgeSchemeColors kit, UiThemeScheme scheme, bool highContrast)
        {
            var surface = scheme.GetPair(UiPaintRole.Surface);
            var primary = scheme.GetPair(UiPaintRole.Primary);
            var accent = scheme.GetPair(UiPaintRole.Accent);
            var danger = scheme.GetPair(UiPaintRole.Danger);
            return new TopiaForgeSchemeColors
            {
                Backdrop = kit.Backdrop,
                Surface = Rgba(surface.Fill),
                SurfaceAlt = Rgba(scheme.GetPair(UiPaintRole.Raised).Fill),
                SurfaceSunken = Rgba(scheme.GetPair(UiPaintRole.Sunken).Fill),
                Tint = Rgba(scheme.GetPair(UiPaintRole.Selected).Fill),
                SelectedTint = Rgba(scheme.GetPair(UiPaintRole.Selected).Fill),
                Outline = Rgba(scheme.Outline),
                OutlineStrong = Rgba(scheme.ResolveBoundary(UiPaintRole.Surface)),
                Primary = Rgba(primary.Fill),
                PrimaryPressed = Rgba(primary.Fill.Tinted(0.94f)),
                OnPrimary = Rgba(primary.On),
                Accent = Rgba(accent.Fill),
                AccentPressed = Rgba(accent.Fill.Tinted(0.94f)),
                Text = Rgba(surface.On),
                TextMuted = PackInk(scheme, UiTone.Muted),
                TextFaint = PackInk(scheme, UiTone.Muted),
                Success = Rgba(scheme.GetPair(UiPaintRole.Success).Fill),
                Warning = Rgba(scheme.GetPair(UiPaintRole.Warning).Fill),
                Danger = Rgba(danger.Fill),
                OnDanger = Rgba(danger.On),
                Shadow = highContrast ? kit.Shadow.WithAlpha(0f) : kit.Shadow,
                ShadowStrong = highContrast ? kit.ShadowStrong.WithAlpha(0f) : kit.ShadowStrong,
                FocusRing = Rgba(scheme.ResolveFocus(UiPaintRole.Surface))
            };
        }

        private static TopiaForgeRgba PackInk(UiThemeScheme scheme, UiTone tone) =>
            Rgba(scheme.ResolveForeground(tone, UiPaintRole.Surface));

        private static TopiaForgeFillPaint[] PackFills(UiThemeScheme scheme, bool highContrast)
        {
            var labelRatio = TopiaForgeOnColors.LabelRatio(highContrast);
            var markRatio = TopiaForgeOnColors.MarkRatio(highContrast);
            var primary = scheme.GetPair(UiPaintRole.Primary);
            var danger = scheme.GetPair(UiPaintRole.Danger);
            var selected = scheme.GetPair(UiPaintRole.Selected).Fill;
            var off = new UiThemePair(selected, scheme.ResolveForeground(UiTone.Muted, UiPaintRole.Selected));

            var result = new TopiaForgeFillPaint[(int)TopiaForgeFillRole.StepFailed + 1];
            result[(int)TopiaForgeFillRole.FilledButton] = Button(primary, labelRatio);
            result[(int)TopiaForgeFillRole.DangerButton] = Button(danger, labelRatio);
            result[(int)TopiaForgeFillRole.ControlOn] = Mark(primary, markRatio);
            result[(int)TopiaForgeFillRole.SwitchOff] = Mark(off, markRatio);
            result[(int)TopiaForgeFillRole.StepDone] = Paint(scheme.GetPair(UiPaintRole.Success), new UiThemeStateTints(1, 1, 1));
            result[(int)TopiaForgeFillRole.StepFailed] = Paint(danger, new UiThemeStateTints(1, 1, 1));
            return result;
        }

        private static TopiaForgeFillPaint Button(UiThemePair pair, float ratio) =>
            Paint(pair, UiThemeColors.ResolveTints(pair, ratio, UiThemeColors.ButtonHovered,
                UiThemeColors.ButtonSelected, UiThemeColors.ButtonPressed));

        private static TopiaForgeFillPaint Mark(UiThemePair pair, float ratio) =>
            Paint(pair, UiThemeColors.ResolveTints(pair, ratio, UiThemeColors.ControlHighlighted,
                UiThemeColors.ControlHighlighted, UiThemeColors.ControlPressed));

        private static TopiaForgeFillPaint Paint(UiThemePair pair, UiThemeStateTints tints) =>
            new TopiaForgeFillPaint(ToColor(Rgba(pair.Fill)), ToColor(Rgba(pair.On)),
                StateTint(tints.Hovered), StateTint(tints.Selected), StateTint(tints.Pressed));

        private static TopiaForgeRgba Rgba(UiThemeColor color) =>
            new TopiaForgeRgba(color.R / 255f, color.G / 255f, color.B / 255f, 1f);
    }
}
