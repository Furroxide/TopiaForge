using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.UiGallery.Pages
{
    internal enum ControlGalleryPage { Buttons, Fields, Selections, Bindings, HelpAndTiles }

    internal static class ControlGalleryPages
    {
        internal const string ExpeditionAction = "gallery-expedition";
        internal const string LedgerAction = "gallery-ledger";

        internal static InputActionDefinition Expedition() => new InputActionDefinition(ExpeditionAction, "Open expedition notes",
            new[] { InputBinding.Key("J"), InputBinding.GamepadButton(InputGamepadButton.West) });

        internal static InputActionDefinition Ledger() => new InputActionDefinition(LedgerAction, "Open supply ledger",
            new[] { InputBinding.Key("K"), InputBinding.GamepadButton(InputGamepadButton.LeftShoulder) });

        internal static IReadOnlyList<InputBinding>? DefaultBindings(string action) => action switch
        {
            ExpeditionAction => Expedition().DefaultBindings,
            LedgerAction => Ledger().DefaultBindings,
            _ => null,
        };

        internal static UiSurfaceRequest Create(ControlGalleryPage page, UiThemeRef? theme = null, Action<string>? report = null)
        {
            report ??= Ignore;
            var (id, title, content) = page switch
            {
                ControlGalleryPage.Buttons => ("gallery-controls-buttons", "Buttons", Buttons(report)),
                ControlGalleryPage.Fields => ("gallery-controls-fields", "Text and choice inputs", Fields()),
                ControlGalleryPage.Selections => ("gallery-controls-selections", "Toggles, checkboxes and tabs", Selections(report)),
                ControlGalleryPage.Bindings => ("gallery-controls-keys", "Key binding fields", Bindings()),
                ControlGalleryPage.HelpAndTiles => ("gallery-controls-help", "Tooltips and list tiles", HelpAndTiles(report)),
                _ => throw new ArgumentOutOfRangeException(nameof(page)),
            };
            var request = new UiSurfaceRequest(id, title, string.Empty, UiSurfaceKind.Window, 880, 480,
                new UiPadding(content, new UiInsets(UiSpace.Sm)));
            return theme == null ? request : request.WithTheme(theme);
        }

        private static UiNode Buttons(Action<string> report)
        {
            return new UiColumn(
                Pair("primary", "Start survey", UiButtonStyle.Primary), Pair("secondary", "Save coordinates", UiButtonStyle.Secondary),
                Pair("ghost", "Preview route", UiButtonStyle.Ghost), Pair("danger", "Discard route", UiButtonStyle.Danger));

            UiNode Pair(string id, string label, UiButtonStyle style) => new UiRow(
                new UiButton("gallery-" + id, label, () => report(label), style),
                new UiButton("gallery-" + id + "-disabled", "Unavailable", static () => { }, style, enabled: false));
        }

        private static UiNode Fields() => new UiSplitPane(
            new UiColumn(
                new UiTextInput("gallery-callsign", "Call sign", "Ranger", static _ => { }, "Enter call sign"),
                UiTextInput.WithDecoration("gallery-route", "Route name", "", static _ => { },
                    new UiInputDecoration("Enter route name", "A route name is required")),
                new UiDropdown("gallery-sector", "Sector", new[] { new UiChoice("west", "West ridge"), new UiChoice("east", "East ridge") },
                    "west", static _ => { })),
            new UiColumn(
                new UiSlider("gallery-rate", "Scan rate", 0, 10, 7, static _ => { }),
                new UiSlider("gallery-percent", "Sensor gain", 0, 100, 60, static _ => { }, 10, UiValueFormat.Percent()),
                new UiSlider("gallery-number", "Altitude offset", -2, 2, 0.5f, static _ => { }, 8, UiValueFormat.Number(1)),
                new UiSlider("gallery-multiplier", "Map zoom", 0.75f, 2f, 1.25f, static _ => { }, 5, UiValueFormat.Multiplier())));

        private static UiNode Selections(Action<string> report) => new UiColumn(
            new UiRow(new UiCheckbox("gallery-grid-lines", "Show grid", true, static _ => { }),
                new UiCheckbox("gallery-labels", "Show labels", false, static _ => { }, enabled: false)),
            new UiToggle("gallery-annotations", "Annotations", false, static _ => { }),
            new UiTabs("gallery-tab-bar", new[]
            {
                new UiTab("summary", "Summary", new UiCheckbox("gallery-tag", "Mark this site", false, static _ => { })),
                new UiTab("maps", "Maps", new UiText("West ridge map")),
            }, "summary", report),
            new UiTabs("gallery-tab-rail", new[]
            {
                new UiTab("weather", "Weather", new UiText("Clear overnight")),
                new UiTab("arrivals", "Arrivals", new UiText("North post at dusk")),
            }, "weather", report, UiTabsStyle.Rail),
            new UiTabs("gallery-disabled-tabs", new[]
            {
                new UiTab("archive", "Archive", new UiText("Archived report")),
                new UiTab("draft", "Draft", new UiText("Work in progress")),
            }, "archive", style: UiTabsStyle.Bar, enabled: false));

        private static UiNode Bindings() => new UiColumn(
            new UiKeyBindingField("gallery-bind-keyboard", ExpeditionAction, "Expedition keyboard shortcut"),
            new UiKeyBindingField("gallery-bind-gamepad", ExpeditionAction, "Expedition controller shortcut", UiInputDevice.Gamepad),
            new UiText("The supply ledger uses K and the left bumper. Try either binding to see the conflict warning.", UiTextStyle.Caption));

        private static UiNode HelpAndTiles(Action<string> report) => new UiColumn(
            new UiTooltip(new UiText("Weather station"), "Temperature and wind refresh once a minute.", "gallery-tooltip-description"),
            new UiTooltip(new UiButton("gallery-tooltip-show", "Open trail guide", () => report("Trail guide opened")),
                "Includes routes approved for night travel.", "gallery-tooltip-button"),
            UiPatterns.ListTile("West ridge", "Routed around the bridge", new UiIcon(UiGlyph.Map),
                new UiBadge("Selected", UiPaintRole.Accent), selected: true, id: "gallery-tile-selected"),
            UiPatterns.ListTile("East ridge", "Survey pending", new UiIcon(UiGlyph.Warning), id: "gallery-tile-ordinary"));

        private static void Ignore(string _) { }
    }
}
