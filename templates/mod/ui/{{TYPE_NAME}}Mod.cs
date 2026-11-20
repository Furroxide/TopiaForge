using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>A configurable window rendered through the safe TopiaForge UI service.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        internal const string ToggleActionName = "toggle-mod-window";
        internal const string GreetingInputId = "greeting";
        internal const string NotificationsToggleId = "notifications";
        internal const string UiScaleSliderId = "ui-scale";
        internal const string ToneDropdownId = "tone";
        internal const string TopicListId = "topics";
        internal const string NotifyButtonId = "notify";
        internal const string PaletteHeadingId = "palette-heading";
        internal const string ThemePreviewToggleId = "theme-preview";
        internal const string SettingsGridId = "settings-categories";
        internal const string SettingsSummaryId = "settings-summary";
        internal const string ContainImageId = "preview-contain";
        internal const string CoverImageId = "preview-cover";
        internal const string PreviewMarkId = "preview-mark";
        internal const string NotificationBadgeId = "notification-status";

        private static readonly UiThemeRef PreviewTheme = new UiThemeRef("controls-preview");
        private static readonly UiImageRef PreviewCard = new UiImageRef("preview-art", "card");
        private static readonly UiImageRef PreviewMark = new UiImageRef("preview-art", "mark");
        private IUiSurface? window;
        private string greeting = "Hello from {{DISPLAY_NAME}}.";
        private string selectedTone = "neutral";
        private string selectedTopic = "getting-started";
        private string toggleKeyLabel = string.Empty;
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

            toggleKeyLabel = config.ToggleKey.ToString();
            var registration = Context.Input.RegisterAction(new InputActionDefinition(
                ToggleActionName,
                "Toggle {{DISPLAY_NAME}} window",
                new[] { InputBinding.Key(config.ToggleKey) }));
            if (!registration.TryGetValue(out var toggleAction))
            {
                Context.Logger.Error(
                    "Input registration failed (" + registration.ErrorCode + "): " + registration.ErrorMessage);
                return;
            }

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
                    "{{DISPLAY_NAME}}",
                    "Hello from {{DISPLAY_NAME}}. Edit this text and add your own actions next.",
                    UiSurfaceKind.Window,
                    width: 560f,
                    height: 520f,
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
            var appearance = new UiColumn(
                new UiText("Appearance", UiTextStyle.Heading),
                new UiToggle(ThemePreviewToggleId, "Preview alternate palette", themePreview, ApplyThemePreview),
                new UiSlider(UiScaleSliderId, "UI scale", 0.75f, 2f, Context.Ui.Accessibility.UiScale, ApplyUiScale));
            var notifications = new UiColumn(
                new UiText("Notifications", UiTextStyle.Heading),
                new UiTextInput(GreetingInputId, "Greeting", greeting, value =>
                {
                    greeting = value;
                    UpdateStatus();
                }, placeholder: "Write a greeting", maximumLength: 120),
                new UiToggle(NotificationsToggleId, "Show notifications", notificationsEnabled, ApplyNotifications),
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
                new UiExpanded(new UiButton(NotifyButtonId, "Show notification", ShowNotification), flex: 2),
                new UiExpanded(new UiButton("hide", "Hide window", () => window?.Hide(), UiButtonStyle.Secondary)),
            }, new UiFlexLayout(spacing: UiSpace.Lg), id: "window-actions");

            return new UiColumn(
                new UiText("Window settings", UiTextStyle.Title, UiTone.Primary, PaletteHeadingId),
                BuildNotificationPreview(),
                new UiGrid(new UiNode[] { appearance, notifications }, minimumCellWidth: 220f,
                    maximumColumns: 2, spacing: UiSpace.Lg, id: SettingsGridId),
                new UiText("Topics", UiTextStyle.Heading),
                topics,
                actions,
                new UiDivider(),
                new UiText("Window toggle  " + toggleKeyLabel, UiTextStyle.Mono, UiTone.Muted, SettingsSummaryId));
        }

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
            var updated = window?.SetContent(BuildContent());
            if (updated is { Succeeded: false })
                Context.Logger.Warn("The notification preview could not update: " + updated.ErrorMessage);
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
            window?.SetContent(BuildContent());
        }

        private void ApplyUiScale(float scale)
        {
            var current = Context.Ui.Accessibility;
            var applied = Context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(
                current.HighContrast,
                scale,
                current.ReducedMotion,
                current.MotionIntensity));
            if (!applied.Succeeded)
            {
                Context.Logger.Warn(
                    "UI scale could not be applied (" + applied.ErrorCode + "): " + applied.ErrorMessage);
            }

            UpdateStatus();
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
