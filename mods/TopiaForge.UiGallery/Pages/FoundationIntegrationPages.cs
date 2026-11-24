using TopiaForge.Mods;

namespace TopiaForge.UiGallery.Pages
{
    internal static class FoundationIntegrationPages
    {
        internal static UiNode Integration(bool transport, bool survival)
        {
            var card = transport ? FoundationArtPages.TransportCard : FoundationArtPages.Card;
            return new UiColumn(
                new UiText("North ridge station", UiTextStyle.Title, id: "station-title"),
                new UiCard(new UiRow(new UiNode[]
                {
                    new UiImage(card, "Supply card", 144, 96, fit: UiImageFit.Cover, id: "station-card"),
                    new UiExpanded(new UiColumn(new UiNode[]
                    {
                        new UiBadge("Open", UiPaintRole.Success, icon: UiGlyph.Check, id: "station-open"),
                        new UiPips(4, 3, "Supplies", role: UiPaintRole.Success, id: "station-supplies"),
                    }, new UiFlexLayout(UiSpace.Sm, crossAxisAlignment: UiCrossAxisAlignment.Start))),
                }, new UiFlexLayout(UiSpace.Md, crossAxisAlignment: UiCrossAxisAlignment.Start)),
                    header: new UiText("Supply cache", UiTextStyle.Label), selected: true,
                    style: survival ? new UiStyleRef("panel.flat") : null, id: "station-cache"),
                new UiDivider(UiLineStyle.Solid),
                new UiGrid(new UiNode[]
                {
                    new UiText("Temperature  ·  12 °C", UiTextStyle.Label, id: "station-temperature"),
                    new UiText("Next check  ·  08:00", UiTextStyle.Mono, id: "station-check"),
                }, minimumCellWidth: 235, maximumColumns: 2, spacing: UiSpace.Md, id: "station-readings"));
        }

        internal static UiNode Diagnostics(bool transport)
        {
            var card = transport ? FoundationArtPages.TransportCard : FoundationArtPages.Card;
            var mark = transport ? FoundationArtPages.TransportMark : FoundationArtPages.Mark;
            return new UiColumn(
                new UiText("Gallery appearance", UiTextStyle.Title, id: "diagnostic-title"),
                new UiCard(new UiColumn(
                    new UiText("A missing picture keeps its space and label.", id: "diagnostic-copy"),
                    new UiRow(
                        new UiImage(card, "Supply card", 160, 96, id: "diagnostic-card"),
                        new UiImage(mark, string.Empty, 48, 48, tint: UiTone.Accent, decorative: true, id: "diagnostic-mark"))),
                    header: new UiText("Artwork fallback", UiTextStyle.Label), id: "diagnostic-fallback"),
                new UiText("Compare this panel beside a loader prompt.", UiTextStyle.Caption, id: "diagnostic-trust"));
        }
    }
}
