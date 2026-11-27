using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiControlOptionsTests
    {
        internal static void Run()
        {
            LegacyMembersKeepTheirDefaults();
            SteppedSlidersUseExactBoundedValues();
            CompositionVisitorsSeeAllTabPages();
            FakeTabsAndBindingsAreViewerLocal();
            StandaloneAndTargetedFakesUseTheAuthorInput();
            ExplicitControlPaintAndHoverAreCaptured();
            Console.WriteLine("K2 control options: compatibility, steps, invariant formats and tab traversal passed.");
            UiKitButtonMotionTests.Run();
            UiKitDropdownPopupTests.Run();
            UiKitNativeBindingTests.Run();
            UiKitNativeTooltipTests.Run();
        }

        private static void FakeTabsAndBindingsAreViewerLocal()
        {
            using var context = new FakeModContext();
            var gamepad = InputBinding.GamepadButton(InputGamepadButton.LeftShoulder);
            var action = context.Input.RegisterAction(new InputActionDefinition("open", "Open the panel",
                new[] { InputBinding.Key("J"), gamepad })).Value!;
            context.Input.RegisterAction(new InputActionDefinition("other", "Open the ledger", new[] { InputBinding.Key("K") }));
            var selected = string.Empty;
            UiTabs Pages(string current) => new UiTabs("settings", new[]
            {
                new UiTab("general", "General", new UiCheckbox("enabled", "Enable hints", false, _ => { })),
                new UiTab("controls", "Controls", new UiKeyBindingField("key", "open")),
            }, current, tab => selected = tab, UiTabsStyle.Rail);

            var surface = Create(context, "settings", Pages("general"));
            var viewport = new UiCaptureViewport(1920, 1080);
            Check(surface.ChangeCheckbox("enabled", true).Succeeded && surface.SelectTab("settings", "controls").Succeeded &&
                selected == "controls" && !surface.ChangeCheckbox("enabled", false).Succeeded &&
                !surface.Capture(viewport).Nodes.Any(node => node.Id == "enabled"), "Only the selected page can draw and react.");
            Check(surface.SelectTab("settings", "general").Succeeded && surface.TryGetCheckboxValue("enabled", out var enabled) && enabled,
                "Switching away and back retains the checkbox's local value.");
            Check(surface.SelectTab("settings", "controls").Succeeded && surface.SetContent(Pages("general")).Succeeded &&
                surface.TryGetSelectedTab("settings", out var current) && current == "controls", "A matching authored selection retains the viewer's tab.");
            Check(surface.SetContent(Pages("controls")).Succeeded && surface.SetContent(Pages("general")).Succeeded &&
                surface.TryGetSelectedTab("settings", out current) && current == "general", "A changed authored selection drives the visible page.");
            Check(surface.SelectTab("settings", "controls").Succeeded, "The input page is reachable.");
            Check(surface.CaptureBinding("key", InputBinding.Key("F10")).ErrorCode == ModErrorCode.InvalidArgument &&
                surface.CaptureBinding("key", gamepad).ErrorCode == ModErrorCode.InvalidArgument && action.Bindings[0].Control == "J",
                "Reserved and wrong-device controls cannot replace the current bind.");
            Check(surface.CaptureBinding("key", InputBinding.Key("K")).Succeeded && action.Bindings.SequenceEqual(new[] { InputBinding.Key("K"), gamepad }) &&
                surface.Capture(viewport).Nodes.Any(node => node.Id == "key" && node.KeyLabel == "K" && node.State == "conflict"),
                "Rebinding keeps the other device and shows both the live name and the conflict.");
            action.ResetBindings();
            Check(surface.Capture(viewport).Nodes.Any(node => node.Id == "key" && node.KeyLabel == "J" && node.State == "idle"),
                "A direct action reset redraws the field without a new composition.");
            Check(surface.SetContent(new UiKeyBindingField("typo", "Open")).ErrorCode == ModErrorCode.InvalidArgument &&
                surface.Content is UiTabs, "Registration is an exact package name match and failed admission preserves the old view.");
        }

        private static void StandaloneAndTargetedFakesUseTheAuthorInput()
        {
            using var context = new FakeModContext();
            using var standaloneLifetime = new FakeModLifetime();
            var standalone = new FakeUiService(standaloneLifetime);
            standalone.UseInput(context.Input);
            using var players = new FakePlayerDirectory(context);
            var guestId = new ParticipantId("guest");
            var guest = players.Add(guestId, "Guest");
            var binding = context.Input.RegisterAction(new InputActionDefinition("open", "Open the panel",
                new[] { InputBinding.Key("J") })).Value!;
            Check(binding.Rebind(new[] { InputBinding.Key("K") }).Succeeded, "The author can replace the effective binding.");

            var request = new UiSurfaceRequest("settings", "Settings", "", UiSurfaceKind.Window, 720, 560,
                new UiKeyBindingField("binding", "open"));
            var viewport = new UiCaptureViewport(1920, 1080);
            var local = standalone.CreateSurface(request);
            Check(local.Succeeded && standalone.Capture("settings", viewport).Nodes.Any(node => node.Id == "binding" && node.KeyLabel == "K"),
                "A standalone fake displays the live action registered with UseInput.");
            var remote = guest.Ui.CreateSurface(request);
            Check(remote.Succeeded && players.State(guestId).Ui.Capture("settings", viewport).Nodes.Any(node =>
                node.Id == "binding" && node.KeyLabel == "J"), "A targeted fake sends the author default independently of the author's effective key.");
            Check(remote.Value!.SetContent(new UiKeyBindingField("binding", "missing")).ErrorCode == ModErrorCode.InvalidArgument,
                "A targeted update continues to validate the author's action names.");
        }

        private static void ExplicitControlPaintAndHoverAreCaptured()
        {
            using var context = new FakeModContext();
            float reported = 0;
            var surface = Create(context, "controls", new UiColumn(
                new UiToggle("switch", "Visible boundary", false, _ => { }),
                new UiCheckbox("check", "Show annotations", true, _ => { }),
                new UiSlider("stepped", "Rate", 0f, 10f, 0f, value => reported = value, 2, UiValueFormat.Percent()),
                UiTextInput.WithDecoration("field", "Trail name", "", _ => { }, new UiInputDecoration("Choose a route", "The name is required")),
                new UiTooltip(new UiText("Park details"), "Walkways close after dusk", "help")));
            Check(surface.ChangeSlider("stepped", 3.6f).Succeeded && reported == 5f &&
                surface.TryGetSliderValue("stepped", out var snapped) && snapped == reported, "Fake slider moves and reports the snapped step.");
            var viewport = new UiCaptureViewport(1920, 1080);
            var pressed = UiKitCaptureTests.CaptureForFixture(surface, viewport, "Pressed", 0f, false);
            var edges = pressed.Nodes.Where(node => node.Kind == "ToggleBoundary").ToArray();
            Check(edges.Length == 3 && edges.All(node => node.ContrastRatio >= 3f && node.BorderWidthPixels == 2f) &&
                edges[0].InkColor == edges[1].InkColor, "The off switch track and thumb and the checked box keep their independent edges when pressed.");
            context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast: true));
            var capture = surface.Capture(viewport);
            Check(capture.Nodes.Where(node => node.Kind == "ToggleBoundary").All(node => node.BorderWidthPixels == 3f) &&
                capture.Nodes.Any(node => node.Kind == "SliderValue" && node.Text == "50%") &&
                capture.Nodes.Any(node => node.Id == "field" && node.State == "error") &&
                capture.Nodes.Any(node => node.Kind == "FieldErrorGlyph") && capture.Nodes.Any(node => node.Kind == "FieldErrorOutline") &&
                surface.ChangeText("field", "Trail north").Succeeded, "High contrast thickens the edge, and error paint leaves the field editable.");
            HoverTooltip(surface, "help");
            context.Time.AdvanceFrame(0.449f);
            Check(!surface.Capture(viewport).Nodes.Any(node => node.Kind == "TooltipPopup"), "Hover waits at least 450 ms.");
            context.Time.AdvanceFrame(0.002f);
            Check(surface.Capture(viewport).Nodes.Any(node => node.Kind == "TooltipPopup" && node.IsRendered), "The settled hover opens the tooltip.");
            HoverTooltip(surface, null);
            Check(!surface.Capture(viewport).Nodes.Any(node => node.Kind == "TooltipPopup") &&
                UiKitCaptureTests.CaptureForFixture(surface, viewport, "Focused", 0f, null).Nodes.Any(node => node.Kind == "TooltipPopup"),
                "Leaving dismisses the hover popup, and focus reveals help immediately.");
        }

        private static FakeUiSurface Create(FakeModContext context, string id, UiNode content) =>
            (FakeUiSurface)context.Ui.CreateSurface(new UiSurfaceRequest(id, "Controls", "", UiSurfaceKind.Window, 720, 560, content)).Value!;

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "HoverTooltip")]
        private static extern void HoverTooltip(FakeUiSurface surface, string? id);

        private static void LegacyMembersKeepTheirDefaults()
        {
            var input = new UiTextInput("old", "Name", "Guest", _ => { }, maximumLength: 10);
            var omittedPlaceholder = new UiTextInput("old-default", "Name", "Guest", _ => { }, default!);
            var nullPlaceholder = new UiTextInput("old-null", "Name", "Guest", _ => { }, null!);
            var slider = new UiSlider("old-slider", "Range", -5f, 5f, 1.005f, _ => { });
            Check(input.Error == null && input.MaximumLength == 10 && slider.Divisions == 0 &&
                slider.Format.Kind == UiValueFormatKind.Number && slider.Format.Decimals == 2 && !slider.UsesExplicitFormat,
                "Existing constructors keep their readout and validation defaults.");
            Check(omittedPlaceholder.Placeholder.Length == 0 && nullPlaceholder.Placeholder.Length == 0 &&
                !omittedPlaceholder.UsesExplicitDecoration && !nullPlaceholder.UsesExplicitDecoration,
                "Positional default and null keep the legacy placeholder behavior.");
            var decorated = UiTextInput.WithDecoration("course", "Course", "blue", _ => { },
                decoration: new UiInputDecoration("Enter a course", "Choose a marked route"));
            Check(decorated.Enabled && decorated.Placeholder == "Enter a course" && decorated.Error == "Choose a marked route",
                "Decorated validation feedback preserves the field's input.");
            Reject(() => new UiInputDecoration(error: " "));
        }

        private static void SteppedSlidersUseExactBoundedValues()
        {
            var slider = new UiSlider("scale", "UI scale", 0.75f, 2f, 1.25f, _ => { }, 5, UiValueFormat.Multiplier());
            Check(slider.UsesExplicitFormat && UiSliderSteps.Snap(1.26f, slider.Minimum, slider.Maximum, slider.Divisions) == 1.25f,
                "The stepped control uses the nearest position in its declared range.");
            Reject(() => new UiSlider("invalid", "UI scale", 0.75f, 2f, 1.26f, _ => { }, 5, UiValueFormat.Multiplier()));
            Reject(() => new UiSlider("limit", "Range", 0f, 1f, 0f, _ => { }, 1001, UiValueFormat.Number()));
            Reject(() => UiValueFormat.Multiplier(4));
            Reject(() => UiValueFormat.FromWire(UiValueFormatKind.Percent, 1));
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Check(slider.Format.Readout(slider.Value, slider.Minimum, slider.Maximum) == "1.25×" &&
                    UiValueFormat.Number(1).Readout(0.5f, 0f, 1f) == "0.5" &&
                    UiValueFormat.Percent().Readout(17f, 10f, 20f) == "70%",
                    "The wire and viewer agree on decimal separators, range-relative percent, and multiplier sign.");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        private static void CompositionVisitorsSeeAllTabPages()
        {
            var quiet = new UiTab("display", "Display", new UiText("Readability"));
            var control = new UiCheckbox("notify", "Show notices", true, _ => { });
            var settings = new UiTab("controls", "Controls", new UiTooltip(control, "Receive a notice when status changes."));
            var authored = new[] { quiet, settings };
            var tabs = new UiTabs("settings", authored, quiet.Id, style: UiTabsStyle.Rail);
            authored[1] = quiet;
            Check(tabs.Tabs.Count == 2 && ReferenceEquals(tabs.Tabs[1], settings) &&
                UiNodeChildren.Of(tabs).Count == 2 &&
                UiLayoutTree.TryFind(tabs, "notify", out var hidden, out var disabled) && ReferenceEquals(hidden, control) && !disabled,
                "The immutable tab list and composition visitor retain a hidden page and its controls.");
            Check(UiLayoutTree.ContainsInteractive(tabs) &&
                UiLayoutTree.ContainsInteractive(new UiTooltip(new UiText("Hint"), "Keyboard and pointer help")),
                "Tabs and tooltip triggers remain interactive even when they wrap passive content.");
            Reject(() => new UiTabs("duplicates", authored, quiet.Id));
            Reject(() => new UiTabs("unmatched", tabs.Tabs, "new"));
            Reject(() => new UiTab(new string('x', 65), "Long", control));
        }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("An invalid K2 control option was accepted.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
