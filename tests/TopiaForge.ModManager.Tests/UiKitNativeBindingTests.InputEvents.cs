using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitNativeBindingTests
    {
        private static void TheNativeEventAdapterUsesThoseClaims()
        {
            var kit = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi");
            var manager = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager");
            var ownershipFiles = new[] { "TopiaForgeInputOwnership.cs", "TopiaForgeInputOwnership.Capture.cs",
                "TopiaForgeInputOwnership.PauseEscape.cs" };
            var nativeMethods = ownershipFiles.SelectMany(file => CSharpSyntaxTree.ParseText(
                File.ReadAllText(Path.Combine(kit, "Runtime", file))).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>());
            var routedMethods = nativeMethods.Where(method => method.Identifier.Text is "OnEvent" or "HoldsControl" or "OnDeviceChange" or "ForgetInputDevice");
            var files = new[] { "Core/TopiaForgeInputClaim.cs", "Core/TopiaForgeInputGate.cs",
                "Core/TopiaForgeNavigationBindings.cs", "Runtime/TopiaForgeViewerInput.cs",
                "Widgets/TopiaForgeBindingCaptureInput.cs", "Widgets/TopiaForgeActionKeyCap.cs" };
            var nativeSources = files.Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(kit, file))))
                .Concat(new[] { "UiNativeActionBinding.cs", "UnityUiService.Input.cs" }.Select(
                    file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(manager, file)))))
                .Append(CSharpSyntaxTree.ParseText(InputContract.Replace("/* production routes */", string.Join("\n", routedMethods))))
                .Append(CSharpSyntaxTree.ParseText(EventCases))
                .Append(CSharpSyntaxTree.ParseText(ActionViewCases))
                .Append(CSharpSyntaxTree.ParseText(ActionViewDoubles));
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = trusted.Append(typeof(InputBinding).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager.Tests", nativeSources, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            Check(result.Success, "production capture compiles against the input event boundary: " + string.Join("\n",
                result.Diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));

            var assembly = Assembly.Load(bytes.ToArray());
            try
            {
                assembly.GetType("EventCases")!.GetMethod("Run")!.Invoke(null, null);
                assembly.GetType("ActionViewCases")!.GetMethod("Run")!.Invoke(null, null);
            }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
        }

        private const string EventCases = """
            using System;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.InputSystem;
            using UnityEngine.InputSystem.LowLevel;

            public static class EventCases
            {
                public static void Run()
                {
                    var keyboard = new Keyboard(1);
                    var mouse = new Mouse(2);
                    var pad = new Gamepad(3);
                    var keyField = new TopiaForgeInputBindingField(UiInputDevice.KeyboardMouse);
                    var padField = new TopiaForgeInputBindingField(UiInputDevice.Gamepad);
                    var mouseBinding = InputBinding.MouseButton(InputMouseButton.Secondary);
                    var idle = Event(mouse.rightButton, 1);
                    for (var i = 0; i < 10_000; i++) TopiaForgeInputOwnership.Deliver(idle, mouse);
                    var allocated = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 10_000; i++) TopiaForgeInputOwnership.Deliver(idle, mouse);
                    Check(GC.GetAllocatedBytesForCurrentThread() == allocated && idle.Value(mouse.rightButton) == 1,
                        "uncaptured mouse events pass with no idle allocations");

                    TopiaForgeBindingCaptureInput.Begin(keyField);
                    mouse.rightButton.Current = 1;
                    Check(Send(mouse, mouse.rightButton, 1) == 1 && !TopiaForgeBindingCaptureInput.TryTake(keyField, out _),
                        "a mouse press already held by gameplay keeps its owner");
                    mouse.rightButton.Current = 0;
                    Check(Send(pad, pad.buttonWest, 1) == 0 && !TopiaForgeBindingCaptureInput.TryTake(keyField, out _),
                        "another device class stays out of gameplay without becoming the binding");
                    Check(Send(mouse, mouse.rightButton, 1) == 0 && TopiaForgeBindingCaptureInput.TryTake(keyField, out var next)
                        && next.Equals(mouseBinding) && TopiaForgeBindingCaptureInput.Took(next),
                        "the next mouse press is masked and supplies the SDK binding");
                    TopiaForgeBindingCaptureInput.End(keyField);
                    Check(TopiaForgeBindingCaptureInput.SuppressesHotkeys, "ending capture does not run hotkeys for this input frame");
                    Time.frameCount++;
                    Check(!TopiaForgeBindingCaptureInput.SuppressesHotkeys && Send(mouse, mouse.rightButton, 1) == 0
                        && TopiaForgeBindingCaptureInput.Took(mouseBinding), "held capture input remains out of both input paths after closing");
                    Check(Send(mouse, mouse.rightButton, 0) == 0 && !TopiaForgeBindingCaptureInput.Took(mouseBinding)
                        && Send(mouse, mouse.rightButton, 1) == 1, "release gives the next press back to ordinary input");

                    TopiaForgeBindingCaptureInput.Begin(keyField);
                    TopiaForgeInputOwnership.PrimeSubmit(keyboard);
                    Check(Send(keyboard, keyboard.enter, 1) == 0 && !TopiaForgeBindingCaptureInput.TryTake(keyField, out _),
                        "Submit already taken by the normal filter cannot bind itself");
                    Check(Send(keyboard, keyboard.escape, 1) == 0 && TopiaForgeInputOwnership.SawBack
                        && !TopiaForgeBindingCaptureInput.TryTake(keyField, out _)
                        && TopiaForgeBindingCaptureInput.Took(InputBinding.Key("Escape")), "Escape is tracked for SDK input suppression and reaches only normal Back");
                    TopiaForgeBindingCaptureInput.End(keyField);
                    Send(keyboard, keyboard.escape, 0);
                    TopiaForgeInputOwnership.SawBack = false;
                    Check(Send(pad, pad.buttonEast, 1) == 0 && TopiaForgeInputOwnership.SawBack
                        && !TopiaForgeBindingCaptureInput.Took(InputBinding.GamepadButton(InputGamepadButton.East)),
                        "ordinary Back still works with capture ended and does not acquire raw ownership");
                    Send(pad, pad.buttonEast, 0);

                    TopiaForgeBindingCaptureInput.Begin(keyField);
                    Check(Send(keyboard, keyboard.digit1, 1) == 0 && TopiaForgeBindingCaptureInput.TryTake(keyField, out next)
                        && next.Equals(InputBinding.Key("Alpha1")), "the captured key uses the SDK's legacy-compatible spelling");
                    Check(Send(keyboard, keyboard.gallery, 1) == 0 && TopiaForgeBindingCaptureInput.TryTake(keyField, out next)
                        && next.Equals(InputBinding.Key("F8")) && TopiaForgeBindingCaptureInput.SuppressesHotkeys,
                        "reserved key is delivered for refusal while its hotkey is suppressed");
                    TopiaForgeBindingCaptureInput.End(keyField);

                    TopiaForgeBindingCaptureInput.Begin(padField);
                    var analog = new InputEventPtr(typeof(StateEvent), (pad.leftStick.x, .65f), (pad.leftStick.y, .9f));
                    TopiaForgeInputOwnership.Deliver(analog, pad);
                    Check(analog.Value(pad.leftStick.x) == 0 && analog.Value(pad.leftStick.y) == 0
                        && TopiaForgeBindingCaptureInput.TryTake(padField, out next)
                        && next.Equals(InputBinding.GamepadAxis(InputGamepadAxis.LeftY)), "both stick axes are masked and the stronger becomes the candidate");
                    var delta = new InputEventPtr(typeof(DeltaStateEvent), (pad.rightTrigger, .8f));
                    TopiaForgeInputOwnership.Deliver(delta, pad);
                    Check(delta.Value(pad.rightTrigger) == 0 && TopiaForgeBindingCaptureInput.TryTake(padField, out next)
                        && next.Equals(InputBinding.GamepadAxis(InputGamepadAxis.RightTrigger)), "delta events work for analog capture");
                    Check(Send(pad, pad.dpad.x, 1) == 0 && !TopiaForgeBindingCaptureInput.TryTake(padField, out _),
                        "the D-pad hat is masked without inventing an SDK binding");
                    TopiaForgeBindingCaptureInput.End(padField);
                    Check(Send(pad, pad.dpad.y, 1) == 0 && Send(pad, pad.dpad.x, 0) == 0
                        && Send(pad, pad.dpad.x, 1) == 1, "turning the captured hat keeps its ownership until it centers");

                    TopiaForgeBindingCaptureInput.Begin(keyField);
                    Check(Send(mouse, mouse.delta.x, 4) == 0 && !TopiaForgeBindingCaptureInput.TryTake(keyField, out _),
                        "ordinary pointer motion cannot pick a binding or move the game camera during capture");
                    Check(Send(mouse, mouse.scroll.y, 1) == 0 && TopiaForgeBindingCaptureInput.Took(InputBinding.Axis(InputAxis.Scroll)),
                        "mouse wheel is a capturable transient control");
                    TopiaForgeInputOwnership.Deliver(new InputEventPtr(typeof(DeviceResetEvent)), mouse);
                    Check(!TopiaForgeBindingCaptureInput.TryTake(keyField, out _), "device reset invalidates its pending candidate");
                    TopiaForgeBindingCaptureInput.Reset();
                    Check(!TopiaForgeBindingCaptureInput.Capturing && !keyField.CanReceiveCapture
                        && !TopiaForgeBindingCaptureInput.Took(InputBinding.GamepadAxis(InputGamepadAxis.RightTrigger)),
                        "focus reset cancels the capture and releases all carried holds");
                }

                private static InputEventPtr Event(InputControl control, float value) => new InputEventPtr(typeof(StateEvent), (control, value));
                private static float Send(InputDevice device, InputControl control, float value)
                {
                    var input = Event(control, value);
                    TopiaForgeInputOwnership.Deliver(input, device);
                    return input.Value(control);
                }
                private static void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
            }
            """;

        private const string InputContract = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.InputSystem;
            using UnityEngine.InputSystem.Controls;
            using UnityEngine.InputSystem.LowLevel;

            namespace UnityEngine
            {
                internal static class Time { internal static int frameCount; }
                internal enum KeyCode { None, A, D, W, S, Escape, Return, Alpha1, F8, LeftArrow, RightArrow, UpArrow, DownArrow }
                internal readonly record struct Vector2(float x, float y)
                {
                    internal float magnitude => MathF.Sqrt(sqrMagnitude);
                    internal float sqrMagnitude => x * x + y * y;
                    public static Vector2 operator -(Vector2 left, Vector2 right) => new(left.x - right.x, left.y - right.y);
                }
            }

            namespace UnityEngine.InputSystem
            {
                internal enum Key { None, Enter = 2, A = 15, D, W, S, Digit1 = 40, Escape = 60, LeftArrow, RightArrow, UpArrow, DownArrow, F8 = 95 }
                internal enum InputDeviceChange { Removed, Disconnected, SoftReset, HardReset }
                internal class InputControl { }
                internal class InputDevice { internal int deviceId; }
                internal sealed class Keyboard : InputDevice
                {
                    internal readonly KeyControl escape = new(Key.Escape), enter = new(Key.Enter), digit1 = new(Key.Digit1), gallery = new(Key.F8);
                    internal readonly List<KeyControl> allKeys;
                    internal Keyboard(int id) { deviceId = id; allKeys = new() { escape, enter, digit1, gallery }; }
                }
                internal sealed class Gamepad : InputDevice
                {
                    internal readonly ButtonControl buttonEast = new(), buttonSouth = new(), buttonWest = new(), buttonNorth = new(),
                        leftShoulder = new(), rightShoulder = new(), selectButton = new(), startButton = new(), leftStickButton = new(), rightStickButton = new(),
                        leftTrigger = new(), rightTrigger = new();
                    internal readonly Vector2Control leftStick = new(), rightStick = new();
                    internal readonly DpadControl dpad = new();
                    internal Gamepad(int id) { deviceId = id; }
                }
                internal sealed class Mouse : InputDevice
                {
                    internal readonly ButtonControl leftButton = new(), rightButton = new(), middleButton = new(), backButton = new(), forwardButton = new();
                    internal readonly Vector2Control scroll = new(), delta = new(), position = new();
                    internal Mouse(int id) { deviceId = id; }
                }
            }

            namespace UnityEngine.InputSystem.Controls
            {
                internal class AxisControl : InputControl
                {
                    internal float Current;
                    internal bool Broken;
                    internal bool ReadValueFromEvent(InputEventPtr input, out float value)
                    {
                        if (Broken) throw new InvalidOperationException("Device reader failed.");
                        return input.TryRead(this, out value);
                    }
                    internal float ReadValue() => Current;
                    internal void ResetToDefaultStateInEvent(InputEventPtr input) => input.Reset(this);
                }
                internal class ButtonControl : AxisControl
                {
                    internal bool IsValueConsideredPressed(float value) => value >= .5f;
                    internal bool isPressed => IsValueConsideredPressed(Current);
                }
                internal sealed class KeyControl : ButtonControl
                {
                    internal Key keyCode;
                    internal KeyControl(Key key) { keyCode = key; }
                }
                internal class Vector2Control : InputControl
                {
                    internal readonly AxisControl x = new(), y = new();
                    internal Vector2 ReadValue() => new(x.Current, y.Current);
                    internal bool ReadValueFromEvent(InputEventPtr input, out Vector2 value)
                    {
                        var hasX = x.ReadValueFromEvent(input, out var horizontal);
                        var hasY = y.ReadValueFromEvent(input, out var vertical);
                        value = new(horizontal, vertical);
                        return input.IsA<DeltaStateEvent>() ? hasX && hasY : hasX || hasY;
                    }
                    internal void ResetToDefaultStateInEvent(InputEventPtr input)
                    {
                        if (x.ReadValueFromEvent(input, out _)) x.ResetToDefaultStateInEvent(input);
                        if (y.ReadValueFromEvent(input, out _)) y.ResetToDefaultStateInEvent(input);
                    }
                }
                internal sealed class DpadControl : Vector2Control { }
            }

            namespace UnityEngine.InputSystem.LowLevel
            {
                internal struct StateEvent { }
                internal struct DeltaStateEvent { }
                internal struct DeviceResetEvent { }
                internal readonly struct InputEventPtr
                {
                    private readonly Type type;
                    private readonly Dictionary<InputControl, float> values;
                    internal InputEventPtr(Type type, params (InputControl Control, float Value)[] controls)
                    {
                        this.type = type;
                        values = new();
                        foreach (var control in controls) values[control.Control] = control.Value;
                    }
                    internal bool IsA<T>() => type == typeof(T);
                    internal float Value(InputControl control) => values[control];
                    internal bool TryRead(InputControl control, out float value) => values.TryGetValue(control, out value);
                    internal void Reset(InputControl control) => values[control] = 0f;
                }
            }

            namespace TopiaForge.Mods.UnityUi
            {
                internal sealed class TopiaForgeInputBindingField
                {
                    internal bool CanReceiveCapture = true;
                    internal UiInputDevice Device;
                    internal TopiaForgeInputBindingField(UiInputDevice device) { Device = device; }
                    internal void CancelCapture() => CanReceiveCapture = false;
                    internal void CaptureBackendStopped() => CancelCapture();
                }
                internal static class TopiaForgeDismissStack
                {
                    internal static TopiaForgeInputClaim Claims => TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Keyboard;
                }
                internal static partial class TopiaForgeInputOwnership
                {
                    private const int EastButton = 0, SouthButton = 1, Dpad = 2, LeftStick = 3, RightStick = 4;
                    private static readonly TopiaForgeInputGate Gate = new();
                    private static bool stopped;
                    internal static void ResetViewerForTest() => ResetViewerInput();
                    internal static bool SawBack;
                    internal static void Deliver(InputEventPtr input, InputDevice device) => OnEvent(input, device);
                    internal static void PrimeSubmit(Keyboard keyboard) => Gate.Take(keyboard.deviceId, TopiaForgeInputDevice.Keyboard,
                        (int)Key.Enter, TopiaForgeInputRole.Submit, 1, 0, 0, 0, TopiaForgeInputClaim.Keyboard, Time.frameCount);
                    private static bool KitTextFieldHasKeyboard() => false;
                    private static void Stop(Exception exception) => throw exception;
                    // The full overlay harness supplies the base-pause boundary.
                    private static void FinishPauseEscape(InputDevice device, InputEventPtr input) { }
                    private static void ForgetPauseEscape(int deviceId) { }
                    private static void FilterKeyboard(Keyboard keyboard, InputEventPtr input, TopiaForgeInputClaim claims, int frame)
                    {
                        Observe(keyboard, keyboard.escape, TopiaForgeInputDevice.Keyboard, (int)Key.Escape, TopiaForgeInputRole.Back, input, claims, frame);
                        Observe(keyboard, keyboard.enter, TopiaForgeInputDevice.Keyboard, (int)Key.Enter, TopiaForgeInputRole.Submit, input, claims, frame);
                    }
                    private static void FilterGamepad(Gamepad pad, InputEventPtr input, TopiaForgeInputClaim claims, int frame) =>
                        Observe(pad, pad.buttonEast, TopiaForgeInputDevice.Gamepad, EastButton, TopiaForgeInputRole.Back, input, claims, frame);
                    private static void Observe(InputDevice device, ButtonControl control, TopiaForgeInputDevice kind, int key,
                        TopiaForgeInputRole role, InputEventPtr input, TopiaForgeInputClaim claims, int frame)
                    {
                        if (!control.ReadValueFromEvent(input, out var value)) return;
                        if (value >= .5f && role == TopiaForgeInputRole.Back) SawBack = true;
                        if (Gate.Take(device.deviceId, kind, key, role, value, control.Current, 0, 0, claims, frame))
                            control.ResetToDefaultStateInEvent(input);
                    }

                    /* production routes */
                }
                internal static class TopiaForgeLog { internal static void Error(string message) { } }
            }
            """;
    }
}
