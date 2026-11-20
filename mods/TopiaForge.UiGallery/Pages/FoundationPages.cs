using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.UiGallery.Pages
{
    internal enum FoundationPage
    {
        ThemePacks,
        Typography,
        InteractionStates
    }

    /// <summary>Safe composition requests shared by the Gallery and the headless capture harness.</summary>
    internal static class FoundationPages
    {
        internal static readonly UiThemeRef SurvivalTheme = new UiThemeRef("survival");
        internal static readonly UiThemeRef TransportTheme = new UiThemeRef("transport-preview");

        internal static UiSurfaceRequest CreateRequest(
            FoundationPage page,
            UiSurfaceKind kind,
            UiThemeRef? theme = null,
            Action<string>? report = null)
        {
            string id;
            string title;
            UiNode content;
            switch (page)
            {
                case FoundationPage.ThemePacks:
                    id = "gallery-theme-packs";
                    title = "Theme packs";
                    content = ThemePacks();
                    break;
                case FoundationPage.Typography:
                    id = "gallery-typography";
                    title = "Typography";
                    content = Typography();
                    break;
                case FoundationPage.InteractionStates:
                    id = "gallery-interaction-states";
                    title = "Interaction states";
                    content = InteractionStates(report ?? IgnoreReport);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(page));
            }

            var body = page == FoundationPage.InteractionStates
                ? "Hover, focus, select or press a control. Changes appear here."
                : string.Empty;
            var request = new UiSurfaceRequest(id, title, body, kind, 840f, 460f, content);
            if (kind == UiSurfaceKind.Hud)
            {
                request = request.WithHudAnchor(UiHudAnchor.Left).WithHudVisibility(UiHudVisibility.Always);
            }

            return theme == null ? request : request.WithTheme(theme);
        }

        private static UiNode ThemePacks() =>
            new UiColumn(
                new UiText("Expedition supplies", UiTextStyle.Title, id: "theme-title"),
                new UiText("Inventory", UiTextStyle.Label, UiTone.Muted, "theme-muted"),
                new UiText("Primary route marked", UiTextStyle.Body, UiTone.Primary, "theme-primary"),
                new UiText("Alternate route available", UiTextStyle.Body, UiTone.Accent, "theme-accent"),
                new UiText("Ready · Shelter stocked", UiTextStyle.Body, UiTone.Success, "theme-success"),
                new UiText("Warning · Water low", UiTextStyle.Body, UiTone.Warning, "theme-warning"),
                new UiText("Danger · Bridge closed", UiTextStyle.Body, UiTone.Danger, "theme-danger"));

        private static UiNode Typography() =>
            new UiColumn(
                new UiText("Trail open", UiTextStyle.Banner, id: "type-banner"),
                new UiText("Field station", UiTextStyle.Display, id: "type-display"),
                new UiText("Supply ledger", UiTextStyle.Title, id: "type-title"),
                new UiText("Equipment", UiTextStyle.Heading, id: "type-heading"),
                new UiText("Count the crates before departure.", UiTextStyle.Body, id: "type-body"),
                new UiText("Storage bay", UiTextStyle.Label, id: "type-label"),
                new UiText("Last checked at noon", UiTextStyle.Caption, UiTone.Muted, "type-caption"),
                new UiText("0123456789", UiTextStyle.Numeral, id: "type-numeral"),
                new UiText("Rack <B-07> · 12:05", UiTextStyle.Mono, id: "type-mono"));

        private static UiNode InteractionStates(Action<string> report)
        {
            var choices = new[] { new UiChoice("trail", "Trail"), new UiChoice("ridge", "Ridge") };
            var listItems = new[]
            {
                new UiListItem("shelter", "Shelter", "Supplies"),
                new UiListItem("lookout", "Lookout", "Signal"),
            };
            return new UiColumn(
                new UiSplitPane(
                    new UiColumn(
                        new UiText("Buttons", UiTextStyle.Heading),
                        new UiRow(
                            new UiButton("button-primary", "Primary", () => report("Primary accepted.")),
                            new UiButton("button-primary-disabled", "Disabled", () => { }, enabled: false)),
                        new UiRow(
                            new UiButton("button-secondary", "Secondary", () => report("Secondary accepted."),
                                UiButtonStyle.Secondary),
                            new UiButton("button-secondary-disabled", "Disabled", () => { },
                                UiButtonStyle.Secondary, enabled: false)),
                        new UiRow(
                            new UiButton("button-ghost", "Ghost", () => report("Ghost accepted."), UiButtonStyle.Ghost),
                            new UiButton("button-ghost-disabled", "Disabled", () => { },
                                UiButtonStyle.Ghost, enabled: false)),
                        new UiRow(
                            new UiButton("button-danger", "Danger", () => report("Danger accepted."),
                                UiButtonStyle.Danger),
                            new UiButton("button-danger-disabled", "Disabled", () => { },
                                UiButtonStyle.Danger, enabled: false)),
                        new UiText("Destinations", UiTextStyle.Heading),
                        new UiVirtualList("control-list", listItems, value => report("Selected " + value + "."),
                            selectedItemId: "shelter", visibleRows: 2),
                        new UiVirtualList("control-list-disabled", listItems, _ => { },
                            selectedItemId: "lookout", visibleRows: 1, enabled: false)),
                    new UiColumn(
                        new UiText("Inputs", UiTextStyle.Heading),
                        new UiToggle("control-toggle-on", "Tracking on", true,
                            enabled => report(enabled ? "Tracking on." : "Tracking off.")),
                        new UiToggle("control-toggle-off", "Signal off", false,
                            enabled => report(enabled ? "Signal on." : "Signal off.")),
                        new UiToggle("control-toggle-disabled", "Tracking locked", false, _ => { }, enabled: false),
                        new UiSlider("control-slider", "Range", 0f, 10f, 4f,
                            value => report("Range " + value.ToString("0.0", CultureInfo.InvariantCulture) + ".")),
                        new UiSlider("control-slider-disabled", "Range locked", 0f, 10f, 4f, _ => { }, enabled: false),
                        new UiTextInput("control-text", "Field note", "", value => report(value),
                            placeholder: "Write a note", maximumLength: 80),
                        new UiTextInput("control-text-disabled", "Field note locked", "Awaiting check-in", _ => { },
                            enabled: false),
                        new UiDropdown("control-dropdown", "Route", choices, "trail",
                            value => report("Route " + value + ".")),
                        new UiDropdown("control-dropdown-disabled", "Route locked", choices, "ridge", _ => { },
                            enabled: false))),
                new UiText("Route graph", UiTextStyle.Heading),
                new UiGraphCanvas("control-graph",
                    new[]
                    {
                        new UiGraphNode("camp", "stop", "Camp", new Vec2(-232f, 0f), new[]
                        {
                            new UiGraphPort("receive", "Run", UiGraphPortDirection.Input, required: true),
                            new UiGraphPort("send", "Send", UiGraphPortDirection.Output),
                        }),
                        new UiGraphNode("trail", "stop", "Trail", new Vec2(0f, 0f), new[]
                        {
                            new UiGraphPort("receive", "Run", UiGraphPortDirection.Input),
                        }),
                        new UiGraphNode("ridge", "stop", "Ridge closed", new Vec2(232f, 0f), new[]
                        {
                            new UiGraphPort("receive", "Run", UiGraphPortDirection.Input),
                        }, enabled: false),
                    },
                    Array.Empty<UiGraphEdge>(),
                    value => report(value == null ? "No stop selected." : "Selected " + value + "."),
                    selectedNodeId: "camp", height: 160f));
        }

        private static void IgnoreReport(string _) { }
    }
}
