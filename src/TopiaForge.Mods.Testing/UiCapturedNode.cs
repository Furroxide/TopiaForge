using System;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Captures one semantic or paint group in a resolved UI tree.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed partial class UiCapturedNode
    {
        internal UiCapturedNode(string path, string? id, string kind, UiCaptureRect bounds,
            UiCaptureNodeContent content, UiCaptureNodePaint paint, string themeId, bool usedFallback,
            bool isRendered, bool clipped, bool occluded)
        {
            Path = path;
            Id = id;
            Kind = kind;
            Text = content.Text;
            SemanticLabel = content.SemanticLabel;
            Decorative = (content.Flags & UiCaptureNodeFlags.Decorative) != 0;
            Disabled = (content.Flags & UiCaptureNodeFlags.Disabled) != 0;
            Selected = (content.Flags & UiCaptureNodeFlags.Selected) != 0;
            X = bounds.X;
            Y = bounds.Y;
            Width = bounds.Width;
            Height = bounds.Height;
            TextHeightPixels = content.TextHeight;
            InkColor = paint.Ink?.ToString();
            FillColor = paint.Fill?.ToString();
            ContrastRatio = paint.Ink.HasValue && paint.Fill.HasValue
                ? (float)UiThemeColors.Contrast(paint.Ink.Value, paint.Fill.Value)
                : 0f;
            ThemeId = themeId;
            StyleId = paint.StyleId;
            CornerRadiusPixels = paint.CornerRadius;
            BorderWidthPixels = paint.BorderWidth;
            ShadowOffsetXPixels = paint.ShadowX;
            ShadowOffsetYPixels = paint.ShadowY;
            UsedFallback = usedFallback;
            IsRendered = isRendered;
            Clipped = clipped;
            Occluded = occluded;
            Flags = content.Flags;
            ExpectedTextHeight = content.ExpectedTextHeight;
        }

        /// <summary>Gets the stable tree index path with the node id, when present.</summary>
        public string Path { get; }
        /// <summary>Gets the authored id, when present.</summary>
        public string? Id { get; }
        /// <summary>Gets the kind of semantic or paint group.</summary>
        public string Kind { get; }
        /// <summary>Gets the resolved visible plain text.</summary>
        public string Text { get; }
        /// <summary>Gets the original accessible label, including its authored case.</summary>
        public string SemanticLabel { get; }
        /// <summary>Gets whether the group is decorative.</summary>
        public bool Decorative { get; }
        /// <summary>Gets whether the group is disabled.</summary>
        public bool Disabled { get; }
        /// <summary>Gets whether the group is selected.</summary>
        public bool Selected { get; }
        /// <summary>Gets the left edge in screen pixels.</summary>
        public float X { get; }
        /// <summary>Gets the top edge in screen pixels.</summary>
        public float Y { get; }
        /// <summary>Gets the width in screen pixels.</summary>
        public float Width { get; }
        /// <summary>Gets the height in screen pixels.</summary>
        public float Height { get; }
        /// <summary>Gets the resolved text height in screen pixels, or zero for groups without text.</summary>
        public float TextHeightPixels { get; }
        /// <summary>Gets the contrast ratio of the resolved ink and fill, or zero without a semantic pair.</summary>
        public float ContrastRatio { get; }
        /// <summary>Gets the applied theme id.</summary>
        public string ThemeId { get; }
        /// <summary>Gets the applied named style id, when present.</summary>
        public string? StyleId { get; }
        /// <summary>Gets the resolved ink as <c>#RRGGBB</c>, when applicable.</summary>
        public string? InkColor { get; }
        /// <summary>Gets the resolved fill as <c>#RRGGBB</c>, when applicable.</summary>
        public string? FillColor { get; }
        /// <summary>Gets the resolved component radius in pixels, when applicable.</summary>
        public float? CornerRadiusPixels { get; }
        /// <summary>Gets the resolved component border width in pixels, when applicable.</summary>
        public float? BorderWidthPixels { get; }
        /// <summary>Gets the horizontal hard-shadow offset in pixels, when applicable.</summary>
        public float? ShadowOffsetXPixels { get; }
        /// <summary>Gets the vertical hard-shadow offset in pixels, when applicable.</summary>
        public float? ShadowOffsetYPixels { get; }
        /// <summary>Gets whether the group used a fallback.</summary>
        public bool UsedFallback { get; }
        /// <summary>Gets whether the group is visible in this capture.</summary>
        public bool IsRendered { get; }
        /// <summary>Gets whether the group crosses its visible bounds.</summary>
        public bool Clipped { get; }
        /// <summary>Gets whether a later paint covers the group.</summary>
        public bool Occluded { get; }

        internal UiCaptureNodeFlags Flags { get; }
        internal float ExpectedTextHeight { get; }
    }

    [Flags]
    internal enum UiCaptureNodeFlags
    {
        None = 0,
        Decorative = 1,
        Disabled = 2,
        Selected = 4,
        RequiresLabel = 8,
        RequiresNonColorCue = 16,
        HasNonColorCue = 32,
        SemanticMark = 64,
        MissingAsset = 128,
        IncorrectTrustTheme = 256,
        InvalidLayout = 512,
        ScrollableClip = 1024,
        HitTarget = 2048,
    }

    internal readonly struct UiCaptureNodeContent
    {
        internal UiCaptureNodeContent(string text, string label, float textHeight, float expectedTextHeight,
            UiCaptureNodeFlags flags)
        {
            Text = text;
            SemanticLabel = label;
            TextHeight = textHeight;
            ExpectedTextHeight = expectedTextHeight;
            Flags = flags;
        }

        internal string Text { get; }
        internal string SemanticLabel { get; }
        internal float TextHeight { get; }
        internal float ExpectedTextHeight { get; }
        internal UiCaptureNodeFlags Flags { get; }
    }

    internal readonly struct UiCaptureNodePaint
    {
        internal UiCaptureNodePaint(UiThemeColor? ink, UiThemeColor? fill, float? cornerRadius = null,
            float? borderWidth = null, float? shadowX = null, float? shadowY = null, string? styleId = null)
        {
            Ink = ink;
            Fill = fill;
            CornerRadius = cornerRadius;
            BorderWidth = borderWidth;
            ShadowX = shadowX;
            ShadowY = shadowY;
            StyleId = styleId;
        }

        internal UiThemeColor? Ink { get; }
        internal UiThemeColor? Fill { get; }
        internal float? CornerRadius { get; }
        internal float? BorderWidth { get; }
        internal float? ShadowX { get; }
        internal float? ShadowY { get; }
        internal string? StyleId { get; }
    }
}
