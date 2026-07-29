using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        private static void TestDeclarativeUiComposition()
        {
            var context = new FakeModContext();
            var successfulButtonSubscriberCalls = 0;
            var toggleValue = false;
            var sliderValue = 0f;
            var textValue = string.Empty;
            var dropdownValue = string.Empty;
            var selectedItemId = string.Empty;
            Action isolatedButtonCallbacks = () => throw new InvalidOperationException("expected callback failure");
            isolatedButtonCallbacks += () => successfulButtonSubscriberCalls++;

            var root = new UiColumn(
                new UiText("Controls", UiTextStyle.Heading),
                new UiRow(
                    new UiButton("apply", "Apply", isolatedButtonCallbacks),
                    new UiButton("disabled", "Unavailable", () => { }, enabled: false)),
                new UiScroll(new UiColumn(
                    new UiToggle("assist", "Assist mode", false, value => toggleValue = value),
                    new UiSlider("scale", "UI scale", 0.75f, 1.5f, 1f, value => sliderValue = value),
                    new UiTextInput(
                        "name",
                        "Robot name",
                        "Topo",
                        value => textValue = value,
                        placeholder: "Name",
                        maximumLength: 5),
                    new UiDropdown(
                        "tone",
                        "Message tone",
                        new[] { new UiChoice("neutral", "Neutral"), new UiChoice("success", "Success") },
                        "neutral",
                        value => dropdownValue = value),
                    new UiVirtualList(
                        "robots",
                        new[]
                        {
                            new UiListItem("atlas", "Atlas", "Builder", "READY"),
                            new UiListItem("ember", "Ember", "Explorer")
                        },
                        value => selectedItemId = value,
                        selectedItemId: "atlas",
                        visibleRows: 2))));

            var accessibility = context.Ui.ApplyAccessibility(
                new UiAccessibilityPreferences(highContrast: true, uiScale: 1.25f, reducedMotion: true, motionIntensity: 0f));
            var creation = context.Ui.CreateSurface(new UiSurfaceRequest(
                "declarative",
                "Declarative UI",
                "Safe controls",
                content: root));
            Assert(creation.TryGetValue(out var created) && created is FakeUiSurface,
                "declarative UI surfaces are captured without a native renderer");
            var surface = (FakeUiSurface)created!;
            Assert(context.Ui.CreateSurface(new UiSurfaceRequest(
                       "declarative",
                       "Duplicate",
                       string.Empty)).ErrorCode == ModErrorCode.Conflict,
                "duplicate surface ids fail with a stable owner-scoped conflict");
            Assert(ReferenceEquals(surface.Content, root) && surface.TryFindNode("robots", out _),
                "the fake UI retains and indexes the immutable composition tree");
            Assert(accessibility.Succeeded && context.Ui.Accessibility.HighContrast &&
                   context.Ui.Accessibility.UiScale == 1.25f && context.Ui.Accessibility.ReducedMotion &&
                   context.Ui.Accessibility.MotionIntensity == 0f,
                "declarative controls retain host accessibility preferences");

            var buttonResult = surface.ActivateButton("apply");
            Assert(!buttonResult.Succeeded && buttonResult.ErrorCode == ModErrorCode.External &&
                   successfulButtonSubscriberCalls == 1 && surface.CallbackErrors.Count == 1,
                "a failing UI callback subscriber is isolated without skipping later subscribers");
            Assert(surface.ChangeToggle("assist", true).Succeeded && toggleValue &&
                   surface.TryGetToggleValue("assist", out var capturedToggle) && capturedToggle,
                "toggle callbacks and state are deterministic");
            Assert(surface.ChangeSlider("scale", 1.4f).Succeeded && sliderValue == 1.4f &&
                   surface.TryGetSliderValue("scale", out var capturedSlider) && capturedSlider == 1.4f,
                "slider callbacks enforce and capture bounded values");
            Assert(surface.ChangeText("name", "Robotopia").Succeeded && textValue == "Robot" &&
                   surface.TryGetTextValue("name", out var capturedText) && capturedText == "Robot",
                "text input applies its maximum length before callback delivery");
            Assert(surface.ChangeDropdown("tone", "success").Succeeded && dropdownValue == "success" &&
                   surface.TryGetDropdownValue("tone", out var capturedChoice) && capturedChoice == "success",
                "dropdown callbacks use stable SDK values");
            Assert(surface.SelectListItem("robots", "ember").Succeeded && selectedItemId == "ember" &&
                   surface.TryGetSelectedListItem("robots", out var capturedItem) && capturedItem == "ember",
                "virtualized-list callbacks use stable item ids");
            Assert(surface.ActivateButton("disabled").ErrorCode == ModErrorCode.InvalidState &&
                   surface.ChangeSlider("scale", 2f).ErrorCode == ModErrorCode.InvalidArgument &&
                   surface.ChangeDropdown("tone", "missing").ErrorCode == ModErrorCode.InvalidArgument,
                "fake controls return stable errors for disabled or invalid interactions");

            surface.SetBody("Updated");
            Assert(surface.Body == "Updated" &&
                   surface.SetContent(new UiColumn(new UiText("Replacement"), new UiButton("close", "Close", () => { }))).Succeeded &&
                   !surface.TryFindNode("apply", out _) && surface.TryFindNode("close", out _),
                "body updates remain compatible and composition replacement drops stale controls");
            var duplicateTree = new UiRow(
                new UiButton("same", "First", () => { }),
                new UiToggle("same", "Second", false, _ => { }));
            Assert(surface.SetContent(duplicateTree).ErrorCode == ModErrorCode.InvalidArgument &&
                   surface.TryFindNode("close", out _),
                "failed composition replacement is atomic and returns a stable validation error");
            AssertThrows<ArgumentException>(() => new UiSurfaceRequest(
                    "duplicates",
                    "Duplicates",
                    string.Empty,
                    content: duplicateTree),
                "composition validation rejects duplicate interactive ids before rendering");
            AssertThrows<ArgumentException>(() => new UiSurfaceRequest(
                    "interactive-hud",
                    "Interactive HUD",
                    string.Empty,
                    UiSurfaceKind.Hud,
                    content: new UiButton("hud-action", "Action", () => { })),
                "presentation-only HUD surfaces reject controls that would silently lack input");

            var successfulModalSubscriberCalls = 0;
            Action<bool> isolatedModalCallbacks = _ => throw new InvalidOperationException("expected modal failure");
            isolatedModalCallbacks += confirmed =>
            {
                if (confirmed) successfulModalSubscriberCalls++;
            };
            var modalResult = context.Ui.ShowModal(
                new UiModalRequest("Confirm", "Exercise isolated completion callbacks."),
                isolatedModalCallbacks);
            Assert(modalResult.TryGetValue(out var createdModal) && createdModal is FakeUiModal,
                "declarative UI tests can capture modal completion");
            var modal = (FakeUiModal)createdModal!;
            modal.Confirm();
            Assert(successfulModalSubscriberCalls == 1 && modal.CallbackErrors.Count == 1 &&
                   context.Ui.Modals.Count == 0,
                "modal completion isolates a failing subscriber and releases exactly once");

            surface.Dispose();
            Assert(context.Ui.CreateSurface(new UiSurfaceRequest(
                       "declarative",
                       "Recreated",
                       string.Empty,
                       content: new UiButton("replacement", "Replacement", () => { }))).Succeeded,
                "early surface release makes its owner-scoped id available for reuse");
            context.Dispose();
            Assert(context.Ui.Surfaces.Count == 0 &&
                   surface.ActivateButton("close").ErrorCode == ModErrorCode.NotFound &&
                   successfulButtonSubscriberCalls == 1,
                "lifetime teardown releases the surface and gates callbacks after disposal");
            Assert(context.Ui.CreateSurface(new UiSurfaceRequest("stopped", "Stopped", string.Empty)).ErrorCode ==
                       ModErrorCode.Cancelled &&
                   context.Ui.ShowModal(new UiModalRequest("Stopped", string.Empty), _ => { }).ErrorCode ==
                       ModErrorCode.Cancelled &&
                   context.Ui.ShowToast("Stopped").ErrorCode == ModErrorCode.Cancelled &&
                   context.Ui.ApplyAccessibility(UiAccessibilityPreferences.Default).ErrorCode ==
                       ModErrorCode.Cancelled,
                "fake UI creation and mutation fail with cancellation after lifetime teardown");
            context.AssertNoLeaks();
        }

    }
}
