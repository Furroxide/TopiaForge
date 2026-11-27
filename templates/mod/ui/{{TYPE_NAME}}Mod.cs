using System;
using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>A configurable window rendered through the safe TopiaForge UI service.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        internal const string ToggleActionName = "toggle-mod-window";
        internal const string SettingsTabsId = "settings-tabs";
        internal const string GeneralTabId = "general";
        internal const string ControlsTabId = "controls";
        internal const string AccessibilityTabId = "accessibility";
        internal const string GreetingInputId = "greeting";
        internal const string NotificationsCheckboxId = "notifications";
        internal const string UiScaleSliderId = "ui-scale";
        internal const string HighContrastToggleId = "high-contrast";
        internal const string ReducedMotionToggleId = "reduced-motion";
        internal const string MotionIntensitySliderId = "motion-intensity";
        internal const string PanelBindingId = "panel-binding";
        internal const string ResetBindingsId = "reset-bindings";
        internal const string DoneButtonId = "done";
        internal const string ToneDropdownId = "tone";
        internal const string TopicListId = "topics";
        internal const string NotifyButtonId = "notify";
        internal const string PaletteHeadingId = "palette-heading";
        internal const string ThemePreviewToggleId = "theme-preview";
        internal const string SettingsGridId = "settings-categories";
        internal const string ContainImageId = "preview-contain";
        internal const string CoverImageId = "preview-cover";
        internal const string PreviewMarkId = "preview-mark";
        internal const string NotificationBadgeId = "notification-status";

        private const float PageHeight = 356f;
        private const float MinimumUiScale = 0.75f;
        private const float MaximumUiScale = 2f;
        private const int UiScaleDivisions = 5;
        private static readonly UiThemeRef PreviewTheme = new UiThemeRef("controls-preview");
        private static readonly UiImageRef PreviewCard = new UiImageRef("preview-art", "card");
        private static readonly UiImageRef PreviewMark = new UiImageRef("preview-art", "mark");
        private IUiSurface? window;
        private IInputAction? panelAction;
        private string selectedTab = GeneralTabId;
        private string greeting = "Hello from {{DISPLAY_NAME}}.";
        private string selectedTone = "neutral";
        private string selectedTopic = "getting-started";
        private bool notificationsEnabled = true;
        private bool themePreview;

        protected override void OnLoad()
        {
            var loaded = Context.Config.Load({{TYPE_NAME}}Config.Definition);
            if (!loaded.TryGetValue(out var config))
            {
                Context.Logger.Error(
                    "Config could not be loaded (" + loaded.ErrorCode + "): " + loaded.ErrorMessage);
                return;
            }

            var registration = Context.Input.RegisterAction(new InputActionDefinition(
                ToggleActionName,
                "Open this panel",
                new[] { InputBinding.Key(config.ToggleKey) }));
            if (!registration.TryGetValue(out var toggleAction))
            {
                Context.Logger.Error(
                    "Input registration failed (" + registration.ErrorCode + "): " + registration.ErrorMessage);
                return;
            }

            panelAction = toggleAction;
            Context.Events.SubscribeUpdate(_ =>
            {
                if (toggleAction.WasPressed)
                {
                    ToggleWindow();
                }
            });

            Context.Logger.Info(
                "{{DISPLAY_NAME}} loaded. Press " + config.ToggleKey + " to toggle its window.");
        }

        private void ToggleWindow()
        {
            if (window == null)
            {
                var created = Context.Ui.CreateSurface(new UiSurfaceRequest(
                    "{{MOD_ID}}.window",
                    "{{DISPLAY_NAME}} settings",
                    string.Empty,
                    UiSurfaceKind.Window,
                    width: 1100f,
                    height: 640f,
                    content: BuildContent()));
                if (!created.TryGetValue(out window))
                {
                    Context.Logger.Error(
                        "Window creation failed (" + created.ErrorCode + "): " + created.ErrorMessage);
                    Context.Ui.ShowToast("{{DISPLAY_NAME}} could not open its window.", UiTone.Danger);
                }

                return;
            }

            if (window.IsVisible)
            {
                window.Hide();
            }
            else
            {
                window.Show();
            }
        }

        private UiNode BuildContent()
        {
            var tabs = new UiTabs(SettingsTabsId, new[]
            {
                new UiTab(GeneralTabId, "General", new UiScroll(BuildGeneral(), PageHeight)),
                new UiTab(ControlsTabId, "Controls", new UiScroll(BuildControls(), PageHeight)),
                new UiTab(AccessibilityTabId, "Accessibility", new UiScroll(BuildAccessibility(), PageHeight)),
            }, selectedTab, selected: id => selectedTab = id, style: UiTabsStyle.Rail);
            var reset = new UiTooltip(
                new UiButton(ResetBindingsId, "Reset to defaults", ResetBindings, UiButtonStyle.Ghost),
                "Restore the default key for Open this panel.");
            var footer = new UiRow(new UiNode[]
            {
                reset,
                new UiSpacer(),
                new UiButton(DoneButtonId, "Done", () => window?.Hide()),
            }, new UiFlexLayout(spacing: UiSpace.Lg));
            return new UiColumn(tabs, new UiDivider(), footer);
        }

        private UiNode BuildGeneral()
        {
            var appearance = new UiCard(
                new UiToggle(ThemePreviewToggleId, "Preview alternate palette", themePreview, ApplyThemePreview),
                header: new UiText("Appearance", UiTextStyle.Heading));
            return new UiColumn(
                new UiText("General", UiTextStyle.Title, UiTone.Primary, PaletteHeadingId),
                new UiGrid(new UiNode[] { appearance, BuildNotificationPreview() }, minimumCellWidth: 300f,
                    maximumColumns: 2, spacing: UiSpace.Lg, id: SettingsGridId));
        }

        private UiNode BuildControls()
        {
            var notifications = new UiColumn(
                new UiText("Notifications", UiTextStyle.Heading),
                UiTextInput.WithDecoration(GreetingInputId, "Greeting", greeting, value =>
                {
                    greeting = value;
                    UpdateStatus();
                }, new UiInputDecoration("Write a greeting"), maximumLength: 120),
                new UiCheckbox(NotificationsCheckboxId, "Show notifications", notificationsEnabled, ApplyNotifications),
                new UiDropdown(ToneDropdownId, "Notification tone", new[]
                {
                    new UiChoice("neutral", "Neutral"),
                    new UiChoice("success", "Success"),
                    new UiChoice("warning", "Warning"),
                }, selectedTone, value =>
                {
                    selectedTone = value;
                    UpdateStatus();
                }));
            var topics = new UiVirtualList(TopicListId, new[]
            {
                new UiListItem("getting-started", "Getting started", "Config, input, and UI", "GUIDE"),
                new UiListItem("gameplay", "Gameplay", "Player, physics, and entities", "SDK"),
                new UiListItem("testing", "Testing", "Deterministic fakes", "NUNIT"),
            }, value =>
            {
                selectedTopic = value;
                UpdateStatus();
            }, selectedItemId: selectedTopic, visibleRows: 3);

            var actions = new UiRow(new UiNode[]
            {
                new UiExpanded(new UiButton(NotifyButtonId, "Show notification", ShowNotification)),
                new UiSpacer(),
            }, new UiFlexLayout(spacing: UiSpace.Lg), id: "window-actions");

            return new UiColumn(
                notifications,
                new UiText("Topics", UiTextStyle.Heading),
                topics,
                actions);
        }

        private UiNode BuildAccessibility()
        {
            var preferences = Context.Ui.Accessibility;
            // Opening the panel must also accept a scale previously set by another control between our steps.
            var stepSize = (MaximumUiScale - MinimumUiScale) / UiScaleDivisions;
            var shownScale = MinimumUiScale +
                (float)Math.Round((preferences.UiScale - MinimumUiScale) / stepSize, MidpointRounding.AwayFromZero) * stepSize;
            var intensity = new UiSlider(MotionIntensitySliderId, "Motion intensity", 0f, 2f, preferences.MotionIntensity,
                value => ApplyAccessibility(motionIntensity: value), divisions: 0, format: UiValueFormat.Number(1),
                enabled: !preferences.ReducedMotion);
            UiNode motion = preferences.ReducedMotion
                ? new UiColumn(intensity, new UiText("Off while reduced motion is on.", UiTextStyle.Caption, UiTone.Muted))
                : intensity;

            return new UiColumn(
                new UiText("Accessibility", UiTextStyle.Heading),
                new UiSlider(UiScaleSliderId, "UI scale", MinimumUiScale, MaximumUiScale, shownScale,
                    value => ApplyAccessibility(uiScale: value), divisions: UiScaleDivisions, format: UiValueFormat.Multiplier()),
                AccessibilityToggle(HighContrastToggleId, "High contrast", preferences.HighContrast,
                    value => ApplyAccessibility(highContrast: value), "Stronger text contrast and thicker outlines."),
                AccessibilityToggle(ReducedMotionToggleId, "Reduced motion", preferences.ReducedMotion,
                    value => ApplyAccessibility(reducedMotion: value), "Changes happen immediately without pulses or punches."),
                motion,
                new UiTooltip(new UiKeyBindingField(PanelBindingId, ToggleActionName, "Open this panel"),
                    "F5, F8 and F10 belong to the workbench, gallery and manager."));
        }

        private static UiCard AccessibilityToggle(string id, string label, bool value,
            Action<bool> changed, string help) =>
            new UiCard(new UiRow(new UiNode[]
            {
                new UiExpanded(new UiToggle(id, label, value, changed)),
                new UiExpanded(new UiText(help, UiTextStyle.Caption, UiTone.Muted)),
            }, new UiFlexLayout(spacing: UiSpace.Sm, crossAxisAlignment: UiCrossAxisAlignment.Center)),
                padding: new UiInsets(UiSpace.Sm));

        private UiNode BuildNotificationPreview()
        {
            var heading = new UiRow(
                new UiImage(PreviewMark, string.Empty, tint: UiTone.Primary, decorative: true, id: PreviewMarkId),
                new UiText("Notification preview", UiTextStyle.Label));
            var banner = new UiStack(new UiNode[]
            {
                new UiBox(280f, 64f, UiPaintRole.Raised),
                new UiPositioned(heading, left: 12f, top: 16f),
            }, width: 280f, height: 64f, id: "notification-preview");
            return new UiColumn(
                banner,
                new UiWrap(new UiNode[]
                {
                    PreviewFit(UiImageFit.Contain, ContainImageId, "Contain"),
                    PreviewFit(UiImageFit.Cover, CoverImageId, "Cover"),
                }, spacing: UiSpace.Lg),
                new UiBadge(notificationsEnabled ? "Notifications on" : "Notifications off",
                    notificationsEnabled ? UiPaintRole.Success : UiPaintRole.Surface,
                    UiBadgeStyle.Outlined, notificationsEnabled ? UiGlyph.Check : UiGlyph.Bell, id: NotificationBadgeId));
        }

        private static UiColumn PreviewFit(UiImageFit fit, string id, string caption) => new UiColumn(
            new UiImage(PreviewCard, "Sample card", width: 112f, height: 76f, fit: fit, id: id),
            new UiText(caption, UiTextStyle.Caption));

        private void ApplyNotifications(bool enabled)
        {
            if (notificationsEnabled == enabled) return;
            notificationsEnabled = enabled;
            UpdateStatus();
            RefreshWindow();
        }

        private void ApplyThemePreview(bool enabled)
        {
            var applied = Context.Ui.Themes.UseTheme(enabled ? PreviewTheme : null);
            if (applied.Succeeded)
            {
                themePreview = enabled;
                return;
            }

            Context.Logger.Warn(
                "Theme preview could not be applied (" + applied.ErrorCode + "): " + applied.ErrorMessage);
            RefreshWindow();
        }

        private void ApplyAccessibility(bool? highContrast = null, float? uiScale = null,
            bool? reducedMotion = null, float? motionIntensity = null)
        {
            var current = Context.Ui.Accessibility;
            var applied = Context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(
                highContrast ?? current.HighContrast,
                uiScale ?? current.UiScale,
                reducedMotion ?? current.ReducedMotion,
                motionIntensity ?? current.MotionIntensity));
            if (!applied.Succeeded)
            {
                Context.Logger.Warn(
                    "Accessibility preferences could not be applied (" + applied.ErrorCode + "): " + applied.ErrorMessage);
            }

            // The active controls retain their values. Rebuild when the motion slider must change enabled state.
            if (!applied.Succeeded || Context.Ui.Accessibility.ReducedMotion != current.ReducedMotion)
                RefreshWindow();
        }

        private void ResetBindings()
        {
            var result = panelAction?.ResetBindings();
            if (result is { Succeeded: false })
                Context.Logger.Warn("The panel shortcut could not be reset: " + result.ErrorMessage);
        }

        private void RefreshWindow()
        {
            var updated = window?.SetContent(BuildContent());
            if (updated is { Succeeded: false })
                Context.Logger.Warn("Window settings could not update: " + updated.ErrorMessage);
        }

        private void ShowNotification()
        {
            if (!notificationsEnabled)
            {
                window?.SetBody("Notifications are disabled. Enable them with the toggle first.");
                return;
            }

            Context.Ui.ShowToast(greeting, ParseTone(selectedTone));
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            window?.SetBody(
                greeting + "\nSelected topic: " + selectedTopic +
                "\nNotifications: " + (notificationsEnabled ? "enabled" : "disabled"));
        }

        private static UiTone ParseTone(string value)
        {
            switch (value)
            {
                case "success": return UiTone.Success;
                case "warning": return UiTone.Warning;
                default: return UiTone.Neutral;
            }
        }
    }
}
