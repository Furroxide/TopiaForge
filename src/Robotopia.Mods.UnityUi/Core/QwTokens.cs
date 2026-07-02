namespace Robotopia.Mods.UnityUi
{
    /// <summary>Text roles mapped to size + font family (Arista display, Quicksand body).</summary>
    public enum QwTextStyle
    {
        Display,
        Title,
        Heading,
        Body,
        Label,
        Caption,
        Numeral,
        Banner,
    }

    /// <summary>Spacing steps used for gaps and padding.</summary>
    public enum QwGap
    {
        None = 0,
        Xs = 4,
        Sm = 8,
        Md = 12,
        Lg = 16,
        Xl = 24,
        Xxl = 32,
    }

    /// <summary>Corner radius roles from the brand shape language.</summary>
    public enum QwRadius
    {
        Bar = 6,
        Chip = 10,
        Tip = 14,
        Control = 18,
        Card = 26,
        Dialog = 28,
    }

    /// <summary>
    /// Non-color design tokens: type scale, control sizes, borders, shadows, motion
    /// durations. Values mirror the launcher design system (hard offset shadows are the
    /// Flutter offsets with Y flipped for Unity's Y-up UI space).
    /// </summary>
    public static class QwTokens
    {
        // Type scale (font size per QwTextStyle).
        public const int DisplaySize = 26;
        public const int TitleSize = 22;
        public const int HeadingSize = 16;
        public const int BodySize = 14;
        public const int LabelSize = 13;
        public const int CaptionSize = 12;
        public const int NumeralSize = 28;
        public const int BannerSize = 42;

        // Layout.
        public const float SafeMargin = 18f;
        public const float ControlSmHeight = 30f;
        public const float ControlHeight = 38f;
        public const float ControlLgHeight = 46f;
        public const float TitleBarHeight = 42f;
        public const float ListRowHeight = 38f;
        public const float MaxContentWidth = 1600f;

        // Borders.
        public const float BorderHairline = 1f;
        public const float BorderStandard = 2f;
        public const float BorderStrong = 3f;

        // Hard offset shadows (no blur — the brand's sticker look).
        public const float ShadowSmallX = -3f;
        public const float ShadowSmallY = -4f;
        public const float ShadowCardX = -4f;
        public const float ShadowCardY = -8f;

        // Motion durations (seconds).
        public const float DurationFast = 0.09f;
        public const float DurationBase = 0.16f;
        public const float DurationSlow = 0.24f;

        // Canvas scaling.
        public const float ReferenceWidth = 1920f;
        public const float ReferenceHeight = 1080f;

        public static int SizeOf(QwTextStyle style)
        {
            return style switch
            {
                QwTextStyle.Display => DisplaySize,
                QwTextStyle.Title => TitleSize,
                QwTextStyle.Heading => HeadingSize,
                QwTextStyle.Body => BodySize,
                QwTextStyle.Label => LabelSize,
                QwTextStyle.Caption => CaptionSize,
                QwTextStyle.Numeral => NumeralSize,
                QwTextStyle.Banner => BannerSize,
                _ => BodySize,
            };
        }

        /// <summary>True for styles rendered with the Arista display face.</summary>
        public static bool IsDisplay(QwTextStyle style)
        {
            return style == QwTextStyle.Display || style == QwTextStyle.Title || style == QwTextStyle.Banner;
        }

        /// <summary>True for styles rendered with the bold Quicksand face.</summary>
        public static bool IsBold(QwTextStyle style)
        {
            return style == QwTextStyle.Heading || style == QwTextStyle.Label || style == QwTextStyle.Numeral;
        }
    }
}
