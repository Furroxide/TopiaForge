using TopiaForge.Mods;

namespace TopiaForge.UiGallery.Pages
{
    internal static class FoundationLayoutPages
    {
        internal static UiSurfaceRequest LayoutNotice(UiThemeRef? theme = null)
        {
            var content = new UiStack(new UiNode[]
            {
                new UiPositioned(new UiText("Ready", id: "positioned-status"), left: 920f, top: 12f, width: 64f),
            }, width: 1000f, height: 56f);
            var request = new UiSurfaceRequest("gallery-layout-notice", "Readiness", "", UiSurfaceKind.Hud,
                1080f, 220f, content).WithHudAnchor(UiHudAnchor.TopLeft).WithHudVisibility(UiHudVisibility.Always);
            return theme == null ? request : request.WithTheme(theme);
        }

        internal static UiNode FlexAndPadding() => new UiPadding(
            new UiColumn(
                new UiText("Padded row with a flexible center", UiTextStyle.Heading),
                new UiRow(new UiNode[]
                {
                    new UiSizedBox(new UiText("Fixed", UiTextStyle.Label), width: 84f, id: "flex-fixed"),
                    new UiExpanded(new UiText("This label takes two shares of the remaining space.", id: "flex-copy"), flex: 2),
                    new UiSpacer(flex: 1, id: "flex-spacer"),
                }, new UiFlexLayout(UiSpace.Md, new UiInsets(UiSpace.Sm, UiSpace.Xs),
                    crossAxisAlignment: UiCrossAxisAlignment.Start), id: "flex-row"),
                new UiGrid(new UiNode[]
                {
                    Alignment("Start", UiMainAxisAlignment.Start, UiCrossAxisAlignment.Start, "start"),
                    Alignment("Center", UiMainAxisAlignment.Center, UiCrossAxisAlignment.Center, "center"),
                    Alignment("End", UiMainAxisAlignment.End, UiCrossAxisAlignment.End, "end"),
                    Alignment("Space between", UiMainAxisAlignment.SpaceBetween, UiCrossAxisAlignment.Stretch, "between"),
                    Alignment("Space around", UiMainAxisAlignment.SpaceAround, UiCrossAxisAlignment.Center, "around"),
                    Alignment("Space evenly", UiMainAxisAlignment.SpaceEvenly, UiCrossAxisAlignment.Center, "evenly"),
                }, minimumCellWidth: 160f, maximumColumns: 3, runSpacing: UiSpace.Sm)),
            new UiInsets(UiSpace.Md), id: "flex-padding");

        private static UiNode Alignment(string label, UiMainAxisAlignment main, UiCrossAxisAlignment cross, string id) =>
            new UiColumn(new UiNode[]
            {
                new UiText(label, UiTextStyle.Caption),
                new UiSizedBox(new UiRow(new UiNode[]
                {
                    new UiBox(22f, 10f, UiPaintRole.Accent),
                    new UiBox(22f, 22f, UiPaintRole.Primary),
                }, new UiFlexLayout(UiSpace.Sm, mainAxisAlignment: main, crossAxisAlignment: cross),
                    id: "align-" + id), width: 144f, height: 28f),
            }, new UiFlexLayout(UiSpace.Xs, crossAxisAlignment: UiCrossAxisAlignment.Start));

        internal static UiNode WrapAndGrid() => new UiColumn(
            new UiText("Weather stations", UiTextStyle.Heading),
            new UiWrap(new UiNode[]
            {
                new UiText("North ridge", id: "wrap-north"),
                new UiText("East basin", id: "wrap-east"),
                new UiText("Lower crossing", id: "wrap-south"),
                new UiText("Sheltered lookout", id: "wrap-west"),
            }, spacing: UiSpace.Lg, runSpacing: UiSpace.Sm, id: "layout-wrap"),
            new UiText("Readings by station", UiTextStyle.Heading),
            new UiGrid(new UiNode[]
            {
                new UiText("North  ·  12 °C", UiTextStyle.Label, id: "grid-north"),
                new UiText("East  ·  14 °C", UiTextStyle.Label, id: "grid-east"),
                new UiText("South  ·  16 °C", UiTextStyle.Label, id: "grid-south"),
                new UiText("West  ·  13 °C", UiTextStyle.Label, id: "grid-west"),
            }, minimumCellWidth: 295f, maximumColumns: 4, spacing: UiSpace.Md, id: "layout-grid"));

        internal static UiNode StackAndBounds() => new UiColumn(
            new UiText("Route panel", UiTextStyle.Heading),
            new UiStack(new UiNode[]
            {
                new UiBox(320f, 128f, UiPaintRole.Selected, id: "stack-background"),
                new UiPositioned(new UiBox(84f, 12f, UiPaintRole.Accent), left: 16f, top: 14f),
                new UiPositioned(new UiText("North ridge open", UiTextStyle.Label, UiTone.Primary, "stack-label"),
                    left: 16f, top: 40f, width: 240f),
                new UiPositioned(new UiText("Station 04", UiTextStyle.Mono, UiTone.Muted), left: 16f, top: 76f),
            }, width: 320f, height: 128f, id: "layout-stack"));

        internal static UiNode CardsAndDividers(bool useSurvivalStyle) => new UiColumn(
            new UiCard(new UiText("Check the trail log."), id: "card-plain"),
            new UiCard(new UiText("Shelter supplies are ready."),
                header: new UiText("Riverside", UiTextStyle.Label),
                style: useSurvivalStyle ? new UiStyleRef("panel.flat") : null,
                selected: true, id: "card-selected"),
            new UiDivider(UiLineStyle.Dashed, id: "divider-dashed"),
            new UiCard(new UiText("This station is unavailable.", tone: UiTone.Muted),
                header: new UiText("West ridge", UiTextStyle.Label), disabled: true, id: "card-disabled"),
            new UiDivider(UiLineStyle.Solid, id: "divider-solid"),
            new UiRow(new UiBox(72f, 18f, UiPaintRole.Accent,
                    useSurvivalStyle ? new UiStyleRef("panel.rule") : null, "card-box"),
                new UiText("Box style", UiTextStyle.Caption)));
    }
}
