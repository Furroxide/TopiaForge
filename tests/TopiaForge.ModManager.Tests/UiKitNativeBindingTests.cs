using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitNativeBindingTests
    {
        internal static void Run()
        {
            TheViewerActionReplacesOnlyOneDeviceClass();
            BackAndReservedKeysLeaveBindingsIntact();
            TheReadoutFollowsResetsAndDisappearingActions();
            TakingInputExtendsPastCaptureAndRemembersEachDevice();
            TheNativeEventAdapterUsesThoseClaims();
            StableReadoutRefreshAllocatesNothing();
            Console.WriteLine("UiKitNativeBindingTests passed: viewer-owned rebinding, Back and reserved controls, raw event ownership, and 10,000 idle refreshes (0 bytes).");
        }

        private static void TheViewerActionReplacesOnlyOneDeviceClass()
        {
            var firstPad = InputBinding.GamepadButton(InputGamepadButton.RightShoulder);
            var secondPad = InputBinding.GamepadAxis(InputGamepadAxis.LeftY, -1);
            var defaults = new[] { firstPad, InputBinding.Key("F"), InputBinding.MouseButton(InputMouseButton.Primary), secondPad };
            using var owner = new TestAction(InputBinding.Key("G"));
            using var viewer = new TestAction(defaults);
            var mouse = InputBinding.MouseButton(InputMouseButton.Secondary);
            var view = new TopiaForgeInputBindingState(Field(UiInputDevice.KeyboardMouse), () => (viewer, "Sprint"),
                (action, candidate) => ReferenceEquals(action, viewer) && candidate.Equals(mouse) ? "Show inventory" : null, true);

            Check(view.DisplayLabel == "Sprint" && view.BindingText == "F" && view.CanEdit,
                "viewer-local effective binding supersedes the transmitted hint");
            Check(view.Begin() && view.StatusText == "Listening: press a key, Esc cancels", "the rebind action starts listening");
            Check(!view.Accept(firstPad) && view.State == TopiaForgeInputBindingFieldState.Listening && viewer.Rebinds == 0,
                "a different device class does not complete capture");
            Check(view.Accept(mouse) && view.BindingText == "Mouse 2" && view.StatusText == "Also bound to Show inventory"
                && view.State == TopiaForgeInputBindingFieldState.Conflict, "a deliberate conflict commits and names the other action");
            Check(viewer.Bindings.SequenceEqual(new[] { firstPad, mouse, secondPad }) && viewer.Rebinds == 1,
                "rebinding replaces the selected class and preserves every other binding's value and order");
            Check(owner.Bindings[0].Equals(InputBinding.Key("G")) && owner.Rebinds == 0, "viewer action alone is edited");

            var pad = new TopiaForgeInputBindingState(Field(UiInputDevice.Gamepad, "Controller binding"), () => (viewer, "Sprint"),
                (_, _) => null, true);
            Check(pad.DisplayLabel == "Controller binding" && pad.BindingText == "RB", "authored label and gamepad key cap use the configured class");
            var trigger = InputBinding.GamepadAxis(InputGamepadAxis.RightTrigger);
            Check(pad.Begin() && pad.Accept(trigger) && pad.BindingText == "RT"
                && viewer.Bindings.SequenceEqual(new[] { trigger, mouse }), "analog controls can replace the gamepad class");
        }

        private static void BackAndReservedKeysLeaveBindingsIntact()
        {
            using var viewer = new TestAction(InputBinding.Key("F"));
            var view = new TopiaForgeInputBindingState(Field(UiInputDevice.KeyboardMouse), () => (viewer, "Sprint"), (_, _) => null, true);
            foreach (var (reserved, owner) in new[] { ("F5", "TopiaForge Workbench"), ("F8", "TopiaForge Gallery"),
                ("F10", "TopiaForge Manager"), ("Escape", "Back") })
            {
                Check(view.Begin() && view.Accept(InputBinding.Key(reserved)) && view.State == TopiaForgeInputBindingFieldState.Reserved
                    && view.StatusText == "Reserved by " + owner && view.BindingText == "F" && viewer.Rebinds == 0,
                    "reserved control " + reserved + " ends capture without any edit and names its owner");
            }

            Check(view.Begin() && view.Accept(InputBinding.GamepadButton(InputGamepadButton.East)) && viewer.Rebinds == 0
                && view.StatusText == "Reserved by Back", "East cancels a keyboard or mouse capture as well");
            Check(view.Begin(), "a subsequent capture can start");
            view.SetEnabled(false);
            Check(!view.CanEdit && !view.Begin() && !view.Accept(InputBinding.Key("W")) && !view.Accept(InputBinding.Key("F8")),
                "inherited disablement revokes listening and rejects edits");
            var disabled = new TopiaForgeInputBindingState(Field(UiInputDevice.KeyboardMouse, enabled: false), () => (viewer, "Sprint"), (_, _) => null, true);
            Check(!disabled.CanEdit && !disabled.Begin() && disabled.BindingText == "F", "authored disablement keeps the live value read-only");
        }

        private static void TheReadoutFollowsResetsAndDisappearingActions()
        {
            using var originallyRegistered = new TestAction(InputBinding.Key("W"));
            using var replacement = new TestAction(InputBinding.GamepadButton(InputGamepadButton.South));
            TestAction? local = null;
            var view = new TopiaForgeInputBindingState(Field(UiInputDevice.KeyboardMouse),
                () => local == null ? null : (local, "Viewer action"), (_, _) => null, true);
            Check(!view.CanEdit && !view.Begin() && view.BindingText == "F2" && view.DisplayLabel == "sprint"
                && view.State == TopiaForgeInputBindingFieldState.Unavailable && view.StatusText == "Not available here",
                "a missing viewer registration shows the authenticated same-device default only as a hint");

            local = originallyRegistered;
            view.Refresh();
            Check(view.BindingText == "W" && view.CanEdit && view.DisplayLabel == "Viewer action", "late local registration supersedes the hint");
            originallyRegistered.Rebind(new[] { InputBinding.Key("E") });
            view.Refresh();
            Check(view.BindingText == "E", "a different screen can change the effective binding");
            originallyRegistered.ResetBindings();
            view.Refresh();
            Check(view.BindingText == "W", "the readout changes after reset without a new UI node");

            Check(view.Begin(), "first registration begins capture");
            local = replacement;
            Check(!view.Accept(InputBinding.Key("L")) && view.CanEdit && view.BindingText == "Unbound"
                && replacement.Rebinds == 0 && view.State == TopiaForgeInputBindingFieldState.Idle,
                "a replaced registration ends the earlier capture and an unbound local action never shows the wire hint");
            Check(view.Begin(), "new registration can listen");
            replacement.RejectNext = true;
            Check(view.Accept(InputBinding.Key("L")) && view.BindingText == "Unbound" && view.State == TopiaForgeInputBindingFieldState.Failed,
                "a failed Rebind leaves the effective binding intact");

            local = null;
            view.Refresh();
            Check(!view.CanEdit && view.BindingText == "F2" && view.StatusText == "Not available here", "disposal restores only the read-only hint");
        }

        private static void TakingInputExtendsPastCaptureAndRemembersEachDevice()
        {
            var gate = new TopiaForgeInputGate();
            var mouse = TopiaForgeInputDevice.Other;
            var pad = TopiaForgeInputDevice.Gamepad;
            Check(!gate.TakeForBindingCapture(1, mouse, 2, 1, 1, true, out var began) && !began,
                "an already gameplay-owned press stays with its owner");
            Check(!gate.TakeForBindingCapture(1, mouse, 2, 0, 1, true, out began) && !began,
                "its release can pass through");
            Check(gate.TakeForBindingCapture(1, mouse, 2, 1, 0, true, out began) && began,
                "the next mouse button press is consumed before gameplay receives it");
            Check(gate.TakeForBindingCapture(2, pad, 2, 1, 0, true, out began) && began,
                "the same control index on a different device has independent ownership");
            Check(gate.TakeForBindingCapture(1, mouse, 2, 1, 0, false, out began) && !began,
                "release of the capture claim does not return held input to gameplay");
            Check(gate.TakeForBindingCapture(2, pad, 2, 0.3f, 0, true, out began) && !began,
                "a settling analog control remains owned without starting another capture");
            Check(!gate.TakeForBindingCapture(1, mouse, 2, 0, 0, false, out began) && !began
                && !gate.HoldsOn(1) && gate.HoldsOn(2), "only the released device control is forgotten");
            gate.Forget(2);
            Check(!gate.HoldsAny && !gate.TakeForBindingCapture(2, pad, 2, 1, 0, false, out began),
                "device reset forgets ownership and ordinary input passes once capture is over");
        }

        private static void StableReadoutRefreshAllocatesNothing()
        {
            using var action = new TestAction(InputBinding.Key("W"));
            var local = new TopiaForgeInputBindingState(Field(UiInputDevice.KeyboardMouse), () => (action, "Sprint"), (_, _) => null, true);
            var absent = new TopiaForgeInputBindingState(Field(UiInputDevice.Gamepad), () => null, (_, _) => null, true);
            for (var i = 0; i < 10_000; i++) { local.Refresh(); absent.Refresh(); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++) { local.Refresh(); absent.Refresh(); }
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(bytes == 0 && local.BindingText == "W" && absent.BindingText == "A", $"settled readouts refresh without allocation ({bytes} bytes)");
        }

        private static UiKeyBindingField Field(UiInputDevice device, string? label = null, bool enabled = true) =>
            new UiKeyBindingField("binding", "sprint", label, device, enabled, new[]
            {
                new UiInputBindingHint(InputBindingKind.GamepadButton, "South"),
                new UiInputBindingHint(InputBindingKind.Key, "F2"),
            });

        private static void Check(bool condition, string failure)
        {
            if (!condition) throw new InvalidOperationException(failure);
        }

        private sealed class TestAction : IInputAction
        {
            private readonly InputBinding[] defaults;

            internal TestAction(params InputBinding[] defaults)
            {
                this.defaults = defaults;
                Bindings = defaults;
            }

            internal int Rebinds { get; private set; }
            internal bool RejectNext { get; set; }
            public string Name => "sprint";
            public IReadOnlyList<InputBinding> Bindings { get; private set; }
            public float Value => 0;
            public bool IsHeld => false;
            public bool WasPressed => false;
            public bool WasReleased => false;

            public OperationResult<bool> Rebind(IEnumerable<InputBinding> newBindings)
            {
                if (RejectNext) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The input action is disposed.");
                Rebinds++;
                Bindings = newBindings.ToArray();
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> ResetBindings()
            {
                Bindings = defaults;
                return OperationResult<bool>.Success(true);
            }

            public void Dispose() { }
        }
    }
}
