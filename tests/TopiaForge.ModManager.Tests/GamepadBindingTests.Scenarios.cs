namespace TopiaForge.ModManager.Tests
{
    internal static partial class GamepadBindingTests
    {
        private const string Scenarios = """
            using System;
            using TopiaForge.Mods;
            using TopiaForge.ModManager;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.InputSystem;
            public static class GamepadScenarios
            {
                static Gamepad Reset()
                {
                    TopiaForgeInputOwnership.Gate.ForgetAll();
                    TopiaForgeDismissStack.Claims = TopiaForgeInputClaim.None;
                    TopiaForgeDismissStack.HoldsFocus = false;
                    TopiaForgeGameTextFocus.Current.Active = false;
                    TopiaForgeNavigateAction.Bindings.Clear();
                    TopiaForgeNavigateAction.Bindings.Listening = false;
                    TopiaForgeNavigateAction.Bindings.Gameplay = false;
                    Input.Movement = 0; Input.Key = false;
                    return Gamepad.current = new Gamepad { deviceId = 42 };
                }
                static UnityInputRegistry.UnityInputAction Action(UnityInputRegistry registry, InputGamepadAxis axis, float scale = 1, bool yields = true)
                    => registry.Register("example.input", new InputActionDefinition(axis.ToString(), "Axis", new[] { InputBinding.GamepadAxis(axis, scale) }, yields)).Value;
                static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
                static void Equal(float expected, float actual) => Check(Math.Abs(expected - actual) < .00001f, "Expected " + expected + ", got " + actual);
                public static void Axes()
                {
                    var pad = Reset();
                    pad.leftStick.Value = new Vector2(-.25f, .5f); pad.rightStick.Value = new Vector2(.75f, -.6f);
                    pad.leftTrigger.Value = .4f; pad.rightTrigger.Value = .9f;
                    using var registry = new UnityInputRegistry();
                    var axes = new[] { InputGamepadAxis.LeftX, InputGamepadAxis.LeftY, InputGamepadAxis.RightX, InputGamepadAxis.RightY, InputGamepadAxis.LeftTrigger, InputGamepadAxis.RightTrigger };
                    var expected = new[] { -.25f, .5f, .75f, -.6f, .4f, .9f };
                    for (int i = 0; i < axes.Length; i++)
                    {
                        using var action = Action(registry, axes[i]); registry.Sample();
                        Equal(expected[i], action.Value); Check(action.WasPressed && action.IsHeld, "axis begins held");
                        registry.Sample(); Check(!action.WasPressed && action.IsHeld, "held axis does not repeat a press");
                    }
                    Equal(0, UnityGamepadAxes.Read((InputGamepadAxis)999));
                }
                public static void Keyboard()
                {
                    Reset(); Input.Movement = 1; Input.Key = true;
                    using var registry = new UnityInputRegistry();
                    foreach (InputGamepadAxis axis in Enum.GetValues<InputGamepadAxis>())
                    {
                        using var action = Action(registry, axis); registry.Sample(); Equal(0, action.Value);
                    }
                    using var movement = registry.Register("example.input", new InputActionDefinition("move", "Movement", new[] { InputBinding.Axis(InputAxis.Horizontal) })).Value;
                    registry.Sample(); Equal(1, movement.Value);
                }
                public static void ScaleAndRebind()
                {
                    var pad = Reset(); pad.rightStick.Value = new Vector2(.75f, 0); pad.leftTrigger.Value = .3f;
                    using var registry = new UnityInputRegistry(); using var action = Action(registry, InputGamepadAxis.RightX, -2);
                    registry.Sample(); Equal(-1, action.Value);
                    action.Rebind(new[] { InputBinding.GamepadAxis(InputGamepadAxis.LeftTrigger, 2) }); registry.Sample(); Equal(.6f, action.Value);
                    action.ResetBindings(); registry.Sample(); Equal(-1, action.Value);
                }
                public static void MissingAndChangingPad()
                {
                    var pad = Reset(); using var registry = new UnityInputRegistry(); using var action = Action(registry, InputGamepadAxis.RightTrigger);
                    // With no controller connected, every axis reads zero and yielding stick bindings still sample.
                    using var left = Action(registry, InputGamepadAxis.LeftX); using var right = Action(registry, InputGamepadAxis.RightY);
                    Gamepad.current = null; registry.Sample(); Equal(0, action.Value); Equal(0, left.Value); Equal(0, right.Value);
                    foreach (InputGamepadAxis axis in Enum.GetValues<InputGamepadAxis>()) Equal(0, UnityGamepadAxes.Read(axis));
                    Check(!TopiaForgeInputOwnership.StickTaken(false) && !TopiaForgeInputOwnership.StickTaken(true), "no controller, no taken stick");
                    Gamepad.current = pad; pad.rightTrigger.Value = .8f; registry.Sample(); Equal(.8f, action.Value); Check(action.WasPressed, "hotplug press");
                    Gamepad.current = null; registry.Sample(); Check(action.WasReleased && !action.IsHeld, "disconnect releases");
                    Gamepad.current = new Gamepad { deviceId = 43 }; Gamepad.current.rightTrigger.Value = .2f;
                    registry.Sample(); Equal(.2f, action.Value); Check(action.WasPressed, "replacement controller");
                }
                public static void YieldAndRelease()
                {
                    foreach (InputGamepadAxis axis in Enum.GetValues<InputGamepadAxis>())
                    {
                        var pad = Reset(); using var registry = new UnityInputRegistry(); using var action = Action(registry, axis);
                        pad.leftStick.Value = pad.rightStick.Value = new Vector2(.5f, .5f); pad.leftTrigger.Value = pad.rightTrigger.Value = .5f;
                        TopiaForgeGameTextFocus.Current.Active = true; registry.Sample(); Equal(0, action.Value);
                        TopiaForgeGameTextFocus.Current.Active = false; registry.Sample(); Equal(0, action.Value);
                        pad.leftStick.Value = pad.rightStick.Value = default; pad.leftTrigger.Value = pad.rightTrigger.Value = 0; registry.Sample();
                        pad.leftStick.Value = pad.rightStick.Value = new Vector2(.5f, .5f); pad.leftTrigger.Value = pad.rightTrigger.Value = .5f;
                        registry.Sample(); Equal(.5f, action.Value); Check(action.WasPressed, "fresh press reaches action");
                    }
                }
                public static void WindowBindings()
                {
                    var pad = Reset(); pad.leftStick.Value = pad.rightStick.Value = new Vector2(.5f, .5f); pad.rightTrigger.Value = .25f;
                    TopiaForgeDismissStack.Claims = TopiaForgeInputClaim.Navigation;
                    using var registry = new UnityInputRegistry();
                    using var left = Action(registry, InputGamepadAxis.LeftX);
                    using var right = Action(registry, InputGamepadAxis.RightX);
                    using var trigger = Action(registry, InputGamepadAxis.RightTrigger);
                    using var mixed = registry.Register("example.input", new InputActionDefinition("mixed", "Mixed",
                        new[] { InputBinding.GamepadAxis(InputGamepadAxis.LeftY), InputBinding.GamepadAxis(InputGamepadAxis.RightTrigger) })).Value;
                    registry.Sample(); Equal(0, left.Value); Equal(.5f, right.Value); Equal(.25f, trigger.Value); Equal(.25f, mixed.Value);
                    pad.rightStick.Value = default; registry.Sample();
                    TopiaForgeNavigateAction.Bindings.BindRightStick(); TopiaForgeNavigateAction.Bindings.Listening = true;
                    pad.rightStick.Value = new Vector2(.5f, 0); registry.Sample(); Equal(0, right.Value);
                    pad.rightStick.Value = default; registry.Sample();
                    TopiaForgeNavigateAction.Bindings.Gameplay = true;
                    pad.rightStick.Value = new Vector2(.5f, 0); registry.Sample(); Equal(.5f, right.Value);
                }
                static void Take(Gamepad pad, int stick, float x, float y)
                {
                    Check(TopiaForgeInputOwnership.Gate.Take(pad.deviceId, TopiaForgeInputDevice.Gamepad, stick, TopiaForgeInputRole.Direction,
                        Math.Max(Math.Abs(x), Math.Abs(y)), 0, x, y, TopiaForgeInputClaim.Navigation, 1), "stick taken");
                }
                public static void TakenSticks()
                {
                    foreach (var pair in new[] { (InputGamepadAxis.LeftX, 3), (InputGamepadAxis.LeftY, 3), (InputGamepadAxis.RightX, 4), (InputGamepadAxis.RightY, 4) })
                    {
                        var pad = Reset(); Take(pad, pair.Item2, .6f, .6f);
                        // The surface closed before sampling; only the taken press remembers that this was UI input.
                        using var registry = new UnityInputRegistry(); using var action = Action(registry, pair.Item1);
                        registry.Sample(); Equal(0, action.Value); Check(!action.WasPressed, "taken stick stays with UI");
                        // Only that stick stays with UI: the other stick and the triggers still reach yielding actions.
                        using var otherStick = Action(registry, pair.Item2 == 3 ? InputGamepadAxis.RightX : InputGamepadAxis.LeftX);
                        using var trigger = Action(registry, pair.Item2 == 3 ? InputGamepadAxis.LeftTrigger : InputGamepadAxis.RightTrigger);
                        (pair.Item2 == 3 ? pad.rightStick : pad.leftStick).Value = new Vector2(.4f, 0); pad.leftTrigger.Value = pad.rightTrigger.Value = .3f;
                        registry.Sample(); Equal(.4f, otherStick.Value); Equal(.3f, trigger.Value); Equal(0, action.Value);
                        using var rawRegistry = new UnityInputRegistry(); using var raw = Action(rawRegistry, pair.Item1, yields: false);
                        rawRegistry.Sample(); Equal(.6f, raw.Value);
                        Take(pad, pair.Item2, -.8f, -.8f); rawRegistry.Sample(); Equal(-.8f, raw.Value); registry.Sample(); Equal(0, action.Value);
                        TopiaForgeInputOwnership.Gate.Forget(pad.deviceId); rawRegistry.Sample(); registry.Sample(); Equal(0, raw.Value);
                        pad.leftStick.Value = pad.rightStick.Value = new Vector2(.3f, .3f); registry.Sample(); Equal(.3f, action.Value);
                    }
                }
                public static void IsolationAndReset()
                {
                    var pad = Reset(); Take(pad, 3, -.8f, .5f); Take(pad, 4, .9f, -.7f);
                    Equal(-.8f, UnityGamepadAxes.Read(InputGamepadAxis.LeftX)); Equal(.5f, UnityGamepadAxes.Read(InputGamepadAxis.LeftY));
                    Equal(.9f, UnityGamepadAxes.Read(InputGamepadAxis.RightX)); Equal(-.7f, UnityGamepadAxes.Read(InputGamepadAxis.RightY));
                    var other = new Gamepad { deviceId = 43 }; Gamepad.current = other;
                    Equal(0, UnityGamepadAxes.Read(InputGamepadAxis.LeftX)); Check(!TopiaForgeInputOwnership.StickTaken(false), "other pad is free");
                    // The left stick let go while the right stays taken: it reads the controller again, never its old direction.
                    Gamepad.current = pad; TopiaForgeInputOwnership.Gate.Take(pad.deviceId, TopiaForgeInputDevice.Gamepad, 3, TopiaForgeInputRole.Direction, 0, 0, 0, 0, TopiaForgeInputClaim.None, 2);
                    pad.leftStick.Value = new Vector2(.2f, 0); Equal(.2f, UnityGamepadAxes.Read(InputGamepadAxis.LeftX)); Equal(.9f, UnityGamepadAxes.Read(InputGamepadAxis.RightX));
                    Check(!TopiaForgeInputOwnership.StickTaken(false) && TopiaForgeInputOwnership.StickTaken(true), "only the held stick stays taken"); pad.leftStick.Value = default;
                    TopiaForgeInputOwnership.Gate.ForgetAll();
                    Equal(0, UnityGamepadAxes.Read(InputGamepadAxis.LeftX)); Check(!TopiaForgeInputOwnership.StickTaken(false), "reset frees stick");
                    var gate = TopiaForgeInputOwnership.Gate;
                    gate.Take(42, TopiaForgeInputDevice.Keyboard, 3, TopiaForgeInputRole.Submit, 1, 0, 1, 1, TopiaForgeInputClaim.Navigation, 1);
                    Check(!gate.TryGetDirection(42, 3, out _, out _), "a button is not a direction");
                    Check(!gate.TryGetDirection(42, -1, out _, out _) && !gate.TryGetDirection(42, int.MaxValue, out _, out _), "control bounds");
                }
                public static void NoAllocation()
                {
                    var pad = Reset(); Take(pad, 3, .6f, .7f); pad.rightTrigger.Value = .5f;
                    float total = 0;
                    for (int i = 0; i < 1000; i++) total += UnityGamepadAxes.Read(InputGamepadAxis.LeftX);
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10000; i++)
                    {
                        total += UnityGamepadAxes.Read(InputGamepadAxis.LeftX) + UnityGamepadAxes.Read(InputGamepadAxis.RightTrigger);
                        Check(TopiaForgeInputOwnership.StickTaken(false), "still taken");
                    }
                    var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    Check(bytes == 0 && total > 0, "gamepad reads allocate " + bytes + " bytes");
                }
            }
            """;
    }
}
