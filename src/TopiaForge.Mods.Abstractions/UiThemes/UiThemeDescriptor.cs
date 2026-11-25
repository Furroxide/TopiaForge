using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TopiaForge.Mods
{
    internal sealed class UiThemeDescriptor
    {
        private readonly UiThemeTextRole[] typography;
        private readonly UiThemeScheme paper;
        private readonly UiThemeScheme hud;
        private readonly UiThemeScheme paperHighContrast;
        private readonly UiThemeScheme hudHighContrast;

        internal UiThemeDescriptor(string id, UiThemeScheme paper, UiThemeScheme hud, UiThemeTextRole[] typography,
            UiThemeShape shape, UiThemeMotion motion, Dictionary<UiThemeTarget, UiThemeComponent> components,
            Dictionary<string, UiThemeNamedStyle> styles, UiThemeImage[] images, UiThemeFont[] fonts, int minimumWireVersion,
            int canonicalComponentCount, ReadOnlyMemory<byte> defaultsFingerprint)
        {
            Id = id;
            this.paper = paper;
            this.hud = hud;
            paperHighContrast = paper.HighContrast();
            hudHighContrast = hud.HighContrast();
            this.typography = typography;
            Shape = shape;
            Motion = motion;
            Components = new ReadOnlyDictionary<UiThemeTarget, UiThemeComponent>(components);
            Styles = new ReadOnlyDictionary<string, UiThemeNamedStyle>(styles);
            Images = Array.AsReadOnly(images);
            Fonts = Array.AsReadOnly(fonts);
            MinimumWireVersion = minimumWireVersion;
            CanonicalComponentCount = canonicalComponentCount;
            Digest = UiThemeCanonical.ComputeDigest(this, defaultsFingerprint.Span);
        }

        internal string Id { get; }
        internal int MinimumWireVersion { get; }
        internal int CanonicalComponentCount { get; }
        internal ReadOnlyMemory<byte> Digest { get; }
        internal UiThemeShape Shape { get; }
        internal UiThemeMotion Motion { get; }
        internal IReadOnlyDictionary<UiThemeTarget, UiThemeComponent> Components { get; }
        internal IReadOnlyDictionary<string, UiThemeNamedStyle> Styles { get; }
        internal IReadOnlyList<UiThemeImage> Images { get; }
        internal IReadOnlyList<UiThemeFont> Fonts { get; }

        internal UiThemeScheme SelectScheme(bool hud, bool highContrast) => hud
            ? (highContrast ? hudHighContrast : this.hud)
            : (highContrast ? paperHighContrast : paper);

        internal UiThemeTextRole GetText(UiTextStyle role) => typography[(int)role];
    }

    internal sealed class UiThemeScheme
    {
        private const int NeutralFillCount = 4;
        private readonly UiThemePair[] pairs;
        private readonly UiThemeColor?[] inks;
        private readonly UiThemeColor[,] foregrounds;
        private readonly double focusMinimum;

        internal UiThemeScheme(UiThemePair[] pairs, UiThemeColor muted, UiThemeColor outline, UiThemeColor focus,
            UiThemeColor?[] inks, double minimum = 4.5d, double focusMinimum = 3d)
        {
            this.pairs = pairs;
            this.inks = inks;
            Muted = muted;
            Outline = outline;
            Focus = focus;
            this.focusMinimum = focusMinimum;
            foregrounds = new UiThemeColor[pairs.Length, inks.Length];
            for (var fill = 0; fill < pairs.Length; fill++)
            {
                for (var tone = 0; tone < inks.Length; tone++)
                {
                    var candidate = Candidate((UiTone)tone, pairs[fill]);
                    foregrounds[fill, tone] = UiThemeColors.RepairForeground(candidate, pairs[fill].Fill, minimum);
                }
            }
        }

        internal UiThemeColor Muted { get; }
        internal UiThemeColor Outline { get; }
        internal UiThemeColor Focus { get; }

        internal UiThemePair GetPair(UiPaintRole role) => pairs[(int)role];
        internal UiThemeColor? AuthoredInk(UiTone tone) => inks[(int)tone];
        internal UiThemeColor ResolveForeground(UiTone tone, UiPaintRole background) => foregrounds[(int)background, (int)tone];
        internal UiThemeColor ResolveFocus(UiPaintRole behindRing) =>
            UiThemeColors.RepairForeground(Focus, GetPair(behindRing).Fill, focusMinimum);
        internal UiThemeColor ResolveBoundary(UiPaintRole background) => UiThemeColors.RepairForeground(Outline, GetPair(background).Fill, 3d);

        private UiThemeColor Candidate(UiTone tone, UiThemePair background) => tone switch
        {
            UiTone.Neutral => background.On,
            UiTone.Muted => Muted,
            _ => inks[(int)tone] ?? GetPair(RoleForTone(tone)).Fill,
        };

        internal static UiPaintRole RoleForTone(UiTone tone) => tone switch
        {
            UiTone.Success => UiPaintRole.Success,
            UiTone.Warning => UiPaintRole.Warning,
            UiTone.Danger => UiPaintRole.Danger,
            UiTone.Primary => UiPaintRole.Primary,
            UiTone.Accent => UiPaintRole.Accent,
            _ => throw new ArgumentOutOfRangeException(nameof(tone)),
        };

        internal static bool ReadsOnNeutralFills(UiThemeColor ink, UiThemePair[] pairs, double minimum)
        {
            for (var index = 0; index < NeutralFillCount; index++)
            {
                if (UiThemeColors.Contrast(ink, pairs[index].Fill) < minimum)
                {
                    return false;
                }
            }

            return true;
        }

        internal UiThemeScheme HighContrast()
        {
            var derivedPairs = Array.ConvertAll(pairs, UiThemeColors.HighContrast);
            var derivedInks = new UiThemeColor?[inks.Length];
            for (var index = 0; index < inks.Length; index++)
            {
                if (inks[index] is UiThemeColor ink && ReadsOnNeutralFills(ink, derivedPairs, 7d))
                {
                    derivedInks[index] = ink;
                }
            }

            return new UiThemeScheme(derivedPairs, Muted, Outline, Focus, derivedInks, minimum: 7d, focusMinimum: 4.5d);
        }
    }

    internal readonly struct UiThemeTextRole
    {
        internal UiThemeTextRole(string font, bool uppercase, float trackingEm, int referenceSize)
        {
            Font = font;
            IsUppercase = uppercase;
            TrackingEm = trackingEm;
            ReferenceSize = referenceSize;
        }

        internal string Font { get; }
        internal bool IsUppercase { get; }
        internal float TrackingEm { get; }
        internal int ReferenceSize { get; }
    }

    internal enum UiThemeRadius { None, Small, Medium, Control, Card, Pill }
    internal enum UiThemeBorder { None, Thin, Standard, Strong }
    internal enum UiThemeShadow { None, Small, Card }
    internal enum UiThemeTarget { Card, Box, Badge, Pips, Button, Input, Tabs, Tooltip, StatBar, KeyGlyph, ActionSlot }
    internal enum UiThemeEase { OutCubic, OutBack }

    internal readonly struct UiThemeShape
    {
        internal UiThemeShape(float small, float medium, float control, float card)
        {
            Small = small;
            Medium = medium;
            Control = control;
            Card = card;
        }

        internal float Small { get; }
        internal float Medium { get; }
        internal float Control { get; }
        internal float Card { get; }

        internal float Resolve(UiThemeRadius radius, float width, float height) => radius switch
        {
            UiThemeRadius.None => 0f,
            UiThemeRadius.Small => Small,
            UiThemeRadius.Medium => Medium,
            UiThemeRadius.Control => Control,
            UiThemeRadius.Card => Card,
            UiThemeRadius.Pill => Math.Min(width, height) / 2f,
            _ => throw new ArgumentOutOfRangeException(nameof(radius)),
        };
    }

    internal readonly struct UiThemeMotion
    {
        internal UiThemeMotion(float fastMs, float baseMs, float slowMs, UiThemeEase ease)
        {
            FastMs = fastMs;
            BaseMs = baseMs;
            SlowMs = slowMs;
            Ease = ease;
        }

        internal float FastMs { get; }
        internal float BaseMs { get; }
        internal float SlowMs { get; }
        internal UiThemeEase Ease { get; }
    }

    internal readonly struct UiThemeComponent
    {
        internal UiThemeComponent(UiThemeRadius radius, UiThemeBorder border, UiThemeShadow shadow)
        {
            Radius = radius;
            Border = border;
            Shadow = shadow;
        }

        internal UiThemeRadius Radius { get; }
        internal UiThemeBorder Border { get; }
        internal UiThemeShadow Shadow { get; }
    }

    internal readonly struct UiThemeNamedStyle
    {
        internal UiThemeNamedStyle(UiThemeTarget target, UiThemeComponent component, UiPaintRole? fill)
        {
            Target = target;
            Component = component;
            Fill = fill;
        }

        internal UiThemeTarget Target { get; }
        internal UiThemeComponent Component { get; }
        internal UiPaintRole? Fill { get; }
    }
}
