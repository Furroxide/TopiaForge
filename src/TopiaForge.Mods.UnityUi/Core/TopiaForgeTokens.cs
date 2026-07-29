namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Text roles mapped to size + font family (Audiowide display, Quicksand body).</summary>
    public enum TopiaForgeTextStyle
    {
        /// <summary>Selects the display option.</summary>
        Display,
        /// <summary>Selects the title option.</summary>
        Title,
        /// <summary>Selects the heading option.</summary>
        Heading,
        /// <summary>Selects the body option.</summary>
        Body,
        /// <summary>Selects the label option.</summary>
        Label,
        /// <summary>Selects the caption option.</summary>
        Caption,
        /// <summary>Selects the numeral option.</summary>
        Numeral,
        /// <summary>Selects the banner option.</summary>
        Banner,
    }

    /// <summary>Spacing steps used for gaps and padding.</summary>
    public enum TopiaForgeGap
    {
        /// <summary>Selects the none option.</summary>
        None = 0,
        /// <summary>Selects the xs option.</summary>
        Xs = 4,
        /// <summary>Selects the sm option.</summary>
        Sm = 8,
        /// <summary>Selects the md option.</summary>
        Md = 12,
        /// <summary>Selects the lg option.</summary>
        Lg = 16,
        /// <summary>Selects the xl option.</summary>
        Xl = 24,
        /// <summary>Selects the xxl option.</summary>
        Xxl = 32,
    }

    /// <summary>Corner radius roles from the brand shape language.</summary>
    public enum TopiaForgeRadius
    {
        /// <summary>Selects the bar option.</summary>
        Bar = 6,
        /// <summary>Selects the chip option.</summary>
        Chip = 10,
        /// <summary>Selects the tip option.</summary>
        Tip = 14,
        /// <summary>Selects the control option.</summary>
        Control = 18,
        /// <summary>Selects the card option.</summary>
        Card = 26,
        /// <summary>Selects the dialog option.</summary>
        Dialog = 28,
    }

    /// <summary>
    /// Non-color design tokens: type scale, control sizes, borders, shadows, motion
    /// durations. Values mirror the launcher design system (hard offset shadows are the
    /// Flutter offsets with Y flipped for Unity's Y-up UI space).
    /// </summary>
    public static class TopiaForgeTokens
    {
        // Type scale (font size per TopiaForgeTextStyle).
        /// <summary>The display size design-token value.</summary>
        public const int DisplaySize = 26;
        /// <summary>The title size design-token value.</summary>
        public const int TitleSize = 22;
        /// <summary>The heading size design-token value.</summary>
        public const int HeadingSize = 16;
        /// <summary>The body size design-token value.</summary>
        public const int BodySize = 14;
        /// <summary>The label size design-token value.</summary>
        public const int LabelSize = 13;
        /// <summary>The caption size design-token value.</summary>
        public const int CaptionSize = 12;
        /// <summary>The numeral size design-token value.</summary>
        public const int NumeralSize = 28;
        /// <summary>The banner size design-token value.</summary>
        public const int BannerSize = 42;

        // Layout.
        /// <summary>The safe margin design-token value.</summary>
        public const float SafeMargin = 18f;
        /// <summary>The control sm height design-token value.</summary>
        public const float ControlSmHeight = 30f;
        /// <summary>The control height design-token value.</summary>
        public const float ControlHeight = 38f;
        /// <summary>The control lg height design-token value.</summary>
        public const float ControlLgHeight = 46f;
        /// <summary>The title bar height design-token value.</summary>
        public const float TitleBarHeight = 42f;
        /// <summary>The list row height design-token value.</summary>
        public const float ListRowHeight = 38f;
        /// <summary>The max content width design-token value.</summary>
        public const float MaxContentWidth = 1600f;

        // Borders.
        /// <summary>The border hairline design-token value.</summary>
        public const float BorderHairline = 1f;
        /// <summary>The border standard design-token value.</summary>
        public const float BorderStandard = 2f;
        /// <summary>The border strong design-token value.</summary>
        public const float BorderStrong = 3f;

        // Hard offset shadows (no blur — the brand's sticker look).
        /// <summary>The shadow small x design-token value.</summary>
        public const float ShadowSmallX = -3f;
        /// <summary>The shadow small y design-token value.</summary>
        public const float ShadowSmallY = -4f;
        /// <summary>The shadow card x design-token value.</summary>
        public const float ShadowCardX = -4f;
        /// <summary>The shadow card y design-token value.</summary>
        public const float ShadowCardY = -8f;

        // Motion durations (seconds).
        /// <summary>The duration fast design-token value.</summary>
        public const float DurationFast = 0.09f;
        /// <summary>The duration base design-token value.</summary>
        public const float DurationBase = 0.16f;
        /// <summary>The duration slow design-token value.</summary>
        public const float DurationSlow = 0.24f;

        // Canvas scaling.
        /// <summary>The reference width design-token value.</summary>
        public const float ReferenceWidth = 1920f;
        /// <summary>The reference height design-token value.</summary>
        public const float ReferenceHeight = 1080f;

        /// <summary>Gets the font size assigned to a text style.</summary>
        public static int SizeOf(TopiaForgeTextStyle style)
        {
            return style switch
            {
                TopiaForgeTextStyle.Display => DisplaySize,
                TopiaForgeTextStyle.Title => TitleSize,
                TopiaForgeTextStyle.Heading => HeadingSize,
                TopiaForgeTextStyle.Body => BodySize,
                TopiaForgeTextStyle.Label => LabelSize,
                TopiaForgeTextStyle.Caption => CaptionSize,
                TopiaForgeTextStyle.Numeral => NumeralSize,
                TopiaForgeTextStyle.Banner => BannerSize,
                _ => BodySize,
            };
        }

        /// <summary>True for styles rendered with the Audiowide display face.</summary>
        public static bool IsDisplay(TopiaForgeTextStyle style)
        {
            return style == TopiaForgeTextStyle.Display || style == TopiaForgeTextStyle.Title || style == TopiaForgeTextStyle.Banner;
        }

        /// <summary>True for styles rendered with the bold Quicksand face.</summary>
        public static bool IsBold(TopiaForgeTextStyle style)
        {
            return style == TopiaForgeTextStyle.Heading || style == TopiaForgeTextStyle.Label || style == TopiaForgeTextStyle.Numeral;
        }
    }
}
