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

        private IUiSurface? window;
        private string greeting = "Hello from {{DISPLAY_NAME}}.";
        private string selectedTone = "neutral";
        private string selectedTopic = "getting-started";
        private bool notificationsEnabled = true;

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
            return new UiColumn(
                new UiText("SAFE UI CONTROLS", UiTextStyle.Heading),
                new UiText(
                    "Every control below is engine-free, lifetime-owned, and rendered by TopiaForgeUi.",
                    UiTextStyle.Caption),
                new UiScroll(new UiColumn(
                    new UiTextInput(
                        GreetingInputId,
                        "Greeting",
                        greeting,
                        value =>
                        {
                            greeting = value;
                            UpdateStatus();
                        },
                        placeholder: "Write a greeting",
                        maximumLength: 120),
                    new UiToggle(
                        NotificationsToggleId,
                        "Show notifications",
                        notificationsEnabled,
                        value =>
                        {
                            notificationsEnabled = value;
                            UpdateStatus();
                        }),
                    new UiSlider(
                        UiScaleSliderId,
                        "UI scale",
                        0.75f,
                        1.5f,
                        Context.Ui.Accessibility.UiScale,
                        ApplyUiScale),
                    new UiDropdown(
                        ToneDropdownId,
                        "Notification tone",
                        new[]
                        {
                            new UiChoice("neutral", "Neutral"),
                            new UiChoice("success", "Success"),
                            new UiChoice("warning", "Warning")
                        },
                        selectedTone,
                        value =>
                        {
                            selectedTone = value;
                            UpdateStatus();
                        }),
                    new UiVirtualList(
                        TopicListId,
                        new[]
                        {
                            new UiListItem("getting-started", "Getting started", "Config, input, and UI", "GUIDE"),
                            new UiListItem("gameplay", "Gameplay", "Player, physics, and entities", "SDK"),
                            new UiListItem("testing", "Testing", "Deterministic fakes", "NUNIT")
                        },
                        value =>
                        {
                            selectedTopic = value;
                            UpdateStatus();
                        },
                        selectedItemId: selectedTopic,
                        visibleRows: 3)),
                    height: 340f),
                new UiRow(
                    new UiButton(NotifyButtonId, "Show notification", ShowNotification),
                    new UiButton("hide", "Hide window", () => window?.Hide(), UiButtonStyle.Secondary)));
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
