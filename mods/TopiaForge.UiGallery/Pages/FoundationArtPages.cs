using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.UiGallery.Pages
{
    internal static class FoundationArtPages
    {
        internal const string ArtBundle = "ui/art/preview.bundle";
        internal const string ArtThemeDefinition = "ui/themes/preview-art.json";
        internal static readonly UiImageRef Card = new UiImageRef("preview-art", "card");
        internal static readonly UiImageRef Mark = new UiImageRef("preview-art", "mark");
        internal static readonly UiImageRef TransportCard = new UiImageRef(FoundationPages.TransportTheme.Id, "card");
        internal static readonly UiImageRef TransportMark = new UiImageRef(FoundationPages.TransportTheme.Id, "mark");
        private static readonly UiFlexLayout LabelBelow = new UiFlexLayout(UiSpace.Xs,
            crossAxisAlignment: UiCrossAxisAlignment.Start);

        internal static bool IsArt(FoundationPage page) =>
            page is FoundationPage.Icons or FoundationPage.Images or FoundationPage.BadgesAndPips
                or FoundationPage.FoundationIntegration or FoundationPage.TrustAndFallback;

        internal static UiNode Icons()
        {
            var glyphs = (UiGlyph[])Enum.GetValues(typeof(UiGlyph));
            var cells = new UiNode[glyphs.Length];
            for (var index = 0; index < glyphs.Length; index++)
            {
                var glyph = glyphs[index];
                var (size, tone) = glyph switch
                {
                    UiGlyph.Warning => (24f, UiTone.Warning),
                    UiGlyph.Check => (22f, UiTone.Success),
                    _ => (18f, UiTone.Primary),
                };
                var icon = new UiIcon(glyph, size: size, tone: tone,
                    id: "glyph-" + index.ToString(CultureInfo.InvariantCulture));
                var caption = glyph == UiGlyph.MicrophoneOff ? "Mic off" : icon.SemanticLabel;
                UiNode label = new UiText(caption, UiTextStyle.Caption);
                if (glyph == UiGlyph.Map)
                    label = new UiRow(new UiIcon(UiGlyph.Map, size: 18f, decorative: true, id: "glyph-decorative"), label);
                cells[index] = new UiColumn(new[] { icon, label }, LabelBelow);
            }

            return new UiGrid(cells, minimumCellWidth: 126f, maximumColumns: 6, runSpacing: UiSpace.Sm, id: "glyph-grid");
        }

        internal static UiNode Images(bool transport)
        {
            var card = transport ? TransportCard : Card;
            var mark = transport ? TransportMark : Mark;
            return new UiColumn(
                new UiRow(new UiNode[]
                {
                    Picture("Contain", new UiImage(card, "Supply card", 144f, 88f, id: "image-contain")),
                    Picture("Cover", new UiImage(card, "Supply card", 144f, 88f, fit: UiImageFit.Cover, id: "image-cover")),
                    Picture("Natural size", new UiImage(card, "Card", id: "image-natural")),
                }, new UiFlexLayout(UiSpace.Lg, crossAxisAlignment: UiCrossAxisAlignment.Start), id: "image-fits"),
                new UiDivider(UiLineStyle.Solid),
                new UiRow(
                    new UiImage(mark, "Marker", 120f, 58f, tint: UiTone.Primary, id: "image-mask"),
                    new UiText("Primary-tinted mask", UiTextStyle.Label)),
                new UiRow(
                    new UiImage(mark, string.Empty, tint: UiTone.Accent, decorative: true, id: "image-decorative"),
                    new UiText("Supply station", UiTextStyle.Label)));
        }

        private static UiNode Picture(string caption, UiImage image) =>
            new UiColumn(new UiNode[] { new UiText(caption, UiTextStyle.Label), image }, LabelBelow);

        internal static UiNode BadgesAndPips(bool survival) => new UiColumn(
            new UiRow(
                new UiBadge("Ready", UiPaintRole.Success, icon: UiGlyph.Check, id: "badge-ready"),
                new UiBadge("Water low", UiPaintRole.Warning, UiBadgeStyle.Outlined, icon: UiGlyph.Info,
                    style: survival ? new UiStyleRef("status.outline") : null, id: "badge-warning"),
                new UiBadge("Closed", UiPaintRole.Danger, id: "badge-closed")),
            new UiRow(new UiBadge("Active", UiPaintRole.Primary, id: "badge-primary"),
                new UiBadge("Surveyed", UiPaintRole.Accent, id: "badge-accent"),
                new UiBadge("Reserve", UiPaintRole.Surface, UiBadgeStyle.Outlined, id: "badge-surface")),
            new UiDivider(UiLineStyle.Solid),
            new UiRow(new UiPips(5, 3, "Energy", fraction: 0.1f, id: "pips-dots"),
                new UiPips(3, 0, "Empty", id: "pips-dots-empty"),
                new UiPips(3, 3, "Full", id: "pips-dots-full")),
            new UiSizedBox(new UiPips(4, 0, "Shield", presentation: UiPipStyle.Segments,
                role: UiPaintRole.Warning, id: "pips-empty"), width: 280f),
            new UiSizedBox(new UiPips(4, 2, "Repair", fraction: 0.5f, presentation: UiPipStyle.Segments,
                id: "pips-partial"), width: 280f),
            new UiSizedBox(new UiPips(4, 4, "Signal", presentation: UiPipStyle.Segments,
                role: UiPaintRole.Success, id: "pips-full"), width: 280f));
    }
}
