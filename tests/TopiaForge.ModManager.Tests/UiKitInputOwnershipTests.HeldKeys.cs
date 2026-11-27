using System;
using System.IO;
using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        // The Input System's number for K.
        private const int K = 25;

        private static readonly InputBinding KeyK = InputBinding.Key("K");

        /// <summary>
        /// The gallery's keybind chip took K, ended its capture in that frame, and K, still held in the next one, counted for
        /// the counter's action bound to it: a press that began while a binding yielded stays the UI's until it is released.
        /// </summary>
        private static void ACapturesKeyHeldIntoTheNextFrameFiresNoBinding()
        {
            var capturing = new UiInputFocus(TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Keyboard, true, false, false);
            var nothing = new UiInputFocus(TopiaForgeInputClaim.None, false, false, false);
            var counter = new ActionSampler(KeyK);
            Assert(!counter.Frame(1f, capturing, taken: true), "the K the capture takes reaches no action in its own frame");
            for (var frame = 0; frame < 20; frame++)
            {
                Assert(!counter.Frame(1f, nothing, taken: true) && counter.Value == 0f,
                    "K held into the frames after the capture ended fires nothing (frame " + frame + ")");
            }

            counter.Frame(0f, nothing);
            Assert(counter.Frame(1f, nothing), "the next press of K counts");

            // The capture can end before the action is read in the frame of its key: TopiaForge took that press from the game,
            // so it stays TopiaForge's for mods too, until its release.
            var early = new ActionSampler(KeyK);
            Assert(!early.Frame(1f, nothing, taken: true), "a press TopiaForge took reaches no action, though its capture ended first");
            Assert(!early.Frame(1f, nothing, taken: true) && early.Value == 0f, "nor while it is held");
            early.Frame(0f, nothing);
            Assert(early.Frame(1f, nothing), "its next press counts");
        }

        /// <summary>
        /// A key held while a window navigates with it, carried across the Escape that closes the window, reported a fresh
        /// press for a mod's action bound to it: the press stays the window's until it is released.
        /// </summary>
        private static void AHeldKeyCarriedAcrossASurfacesCloseFiresNoPress()
        {
            var navigation = new TopiaForgeNavigationBindings { Listening = true };
            BindTheGamesNavigation(navigation);
            var selectedWindow = new UiInputFocus(TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Navigation, false, false, false, navigation);
            var closed = new UiInputFocus(TopiaForgeInputClaim.None, false, false, false, navigation);
            var walk = new ActionSampler(KeyW);
            Assert(!walk.Frame(1f, selectedWindow, taken: true), "W, moving the selected window at the main menu, fires nothing");
            Assert(!walk.Frame(1f, closed, taken: true) && walk.Value == 0f, "nor in the frame after Escape closed the window, W still held");
            for (var frame = 0; frame < 20; frame++)
            {
                Assert(!walk.Frame(1f, closed, taken: true), "nor while it stays held (frame " + frame + ")");
            }

            walk.Frame(0f, closed);
            Assert(walk.Frame(1f, closed), "a fresh W fires");

            // Without TopiaForge holding the press, as with a key pressed before a window claimed it, the binding still waits.
            var enter = new ActionSampler(KeyReturn);
            Assert(!enter.Frame(1f, selectedWindow), "Enter pressed in the selected window yields to it");
            Assert(!enter.Frame(1f, closed), "and stays the window's after it closed, until released");
            enter.Frame(0f, closed);
            Assert(enter.Frame(1f, closed), "then the next Enter reaches the action");
        }

        /// <summary>
        /// A press belongs to whoever held its control as it began, both ways: one a mod's action had keeps reaching it when
        /// UI takes the control meanwhile, and a mouse press that began over UI stays the UI's when the pointer leaves it.
        /// </summary>
        private static void APressKeepsTheOwnerItBeganWith()
        {
            var navigation = new TopiaForgeNavigationBindings { Listening = true };
            BindTheGamesNavigation(navigation);
            var nothing = new UiInputFocus(TopiaForgeInputClaim.None, false, false, false, navigation);
            var selectedWindow = new UiInputFocus(TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Navigation, false, false, false, navigation);
            var walk = new ActionSampler(KeyW);
            Assert(walk.Frame(1f, nothing), "W held for a mod's action fires it");
            for (var frame = 0; frame < 10; frame++)
            {
                walk.Frame(1f, selectedWindow);
                Assert(walk.Value == 1f && !walk.Released, "and keeps it held after a window's control is selected, as the game keeps it (frame " + frame + ")");
            }

            walk.Frame(0f, selectedWindow);
            Assert(walk.Released, "its release is the action's");
            Assert(!walk.Frame(1f, selectedWindow), "the next W, while the window navigates with it, is the window's");

            var overUi = new UiInputFocus(TopiaForgeInputClaim.None, false, false, true);
            var offUi = new UiInputFocus(TopiaForgeInputClaim.None, false, false, false);
            var click = new ActionSampler(MousePrimary);
            Assert(!click.Frame(1f, overUi), "a click over UI is the UI's");
            Assert(!click.Frame(1f, offUi) && click.Value == 0f, "and stays the UI's, held, when the pointer leaves it");
            click.Frame(0f, offUi);
            Assert(click.Frame(1f, offUi), "the next click away from UI fires");

            var always = new ActionSampler(KeyK, yielding: false);
            Assert(always.Frame(1f, new UiInputFocus(TopiaForgeInputClaim.Keyboard, true, false, false), taken: true),
                "an action that never yields reads every press, one TopiaForge took included");
        }

        /// <summary>
        /// Which presses TopiaForge took is told by the kind of device too: a controller's A is the filter's control 1, as the
        /// keyboard's Space is key 1, and neither holds the other's mod binding.
        /// </summary>
        private static void TakenPressesAreToldApartByDevice()
        {
            var gate = new TopiaForgeInputGate();
            Assert(gate.Take(GamepadId, TopiaForgeInputDevice.Gamepad, South, TopiaForgeInputRole.Submit, 1f, 0f, 0f, 0f, TopiaForgeInputClaim.Navigation, 0),
                "a controller's A is taken");
            Assert(gate.HeldOn(TopiaForgeInputDevice.Gamepad, South) && !gate.HeldOn(TopiaForgeInputDevice.Keyboard, Space),
                "it holds a mod's A binding, never its Space binding");
            Assert(gate.HoldsAnyOn(TopiaForgeInputDevice.Gamepad) && !gate.HoldsAnyOn(TopiaForgeInputDevice.Keyboard),
                "and a controller's press is no key held");
            Assert(gate.Take(KeyboardId, TopiaForgeInputDevice.Keyboard, Space, TopiaForgeInputRole.Submit, 1f, 0f, 0f, 0f, TopiaForgeInputClaim.Navigation, 0),
                "the keyboard's Space is taken");
            gate.Take(GamepadId, TopiaForgeInputDevice.Gamepad, South, TopiaForgeInputRole.Submit, 0f, 0f, 0f, 0f, TopiaForgeInputClaim.Navigation, 1);
            Assert(gate.HeldOn(TopiaForgeInputDevice.Keyboard, Space) && !gate.HeldOn(TopiaForgeInputDevice.Gamepad, South),
                "once A is released, only Space is held");
            Assert(!gate.HeldOn(TopiaForgeInputDevice.Keyboard, -1) && !gate.HeldOn(TopiaForgeInputDevice.Keyboard, TopiaForgeInputGate.ControlsPerDevice),
                "an unknown key is never held");
        }

        /// <summary>
        /// A mod's key binding names its key the way Unity's legacy key codes do, while TopiaForge's filter numbers keys as
        /// the game's Input System does: each name the kit maps is a legacy key code, and maps to the Input System key of the
        /// same physical key.
        /// </summary>
        private static void TheSdkKeyNamesMapToTheInputSystemsKeys()
        {
            foreach (var (sdk, inputSystem) in new[]
            {
                ("K", "K"), ("w", "w"), ("Alpha0", "Digit0"), ("alpha9", "Digit9"), ("Keypad5", "Numpad5"), ("KeypadEnter", "NumpadEnter"),
                ("KeypadPlus", "NumpadPlus"), ("Return", "Enter"), ("LeftControl", "LeftCtrl"), ("RightControl", "RightCtrl"),
                ("Print", "PrintScreen"), ("Menu", "ContextMenu"), ("Space", "Space"), ("Escape", "Escape"), ("BackQuote", "BackQuote"),
            })
            {
                AssertEqual(TopiaForgeNavigationBindings.InputSystemName(sdk), inputSystem, "the SDK's " + sdk + " is the Input System's " + inputSystem);
            }

            WithGameAssemblies(type =>
            {
                var legacy = type("UnityEngine.KeyCode, UnityEngine.CoreModule");
                var key = type("UnityEngine.InputSystem.Key, Unity.InputSystem");
                Assert(legacy != null && legacy.IsEnum && key != null && key.IsEnum, "the game has both key enums");
                foreach (var sdk in new[]
                {
                    "A", "K", "W", "Z", "Alpha0", "Alpha9", "Keypad0", "Keypad9", "KeypadEnter", "KeypadPlus", "KeypadPeriod", "Return",
                    "Space", "Escape", "Tab", "UpArrow", "LeftControl", "RightControl", "LeftShift", "LeftAlt", "Print", "Menu",
                    "BackQuote", "Numlock", "F1", "F12", "LeftWindows", "LeftCommand", "LeftApple", "AltGr", "Backspace", "Delete",
                })
                {
                    Assert(Enum.TryParse(legacy!, sdk, ignoreCase: false, out _), sdk + " is one of Unity's legacy key codes");
                    Assert(Enum.TryParse(key!, TopiaForgeNavigationBindings.InputSystemName(sdk), ignoreCase: true, out var mapped)
                        && Convert.ToInt32(mapped) > 0 && Convert.ToInt32(mapped) < TopiaForgeInputGate.ControlsPerDevice,
                        sdk + " maps to one of the game's Input System keys the filter can take");
                }

                AssertEqual(Convert.ToInt32(Enum.Parse(key!, TopiaForgeNavigationBindings.InputSystemName("K"), true)), K, "the SDK's K is the key the filter numbers K");
                AssertEqual(Convert.ToInt32(Enum.Parse(key!, TopiaForgeNavigationBindings.InputSystemName("Return"), true)), Enter, "and its Return the filter's Enter");
                Assert(!Enum.TryParse(key!, TopiaForgeNavigationBindings.InputSystemName("Mouse0"), true, out _), "a mouse button is no key the filter takes");
            });
        }

        /// <summary>
        /// A binding's frames as UnityInputAction samples them: what it reads, through InputActionYield.Read, and the press
        /// and release edges of its action.
        /// </summary>
        private sealed class ActionSampler
        {
            private readonly InputBinding binding;
            private readonly bool yielding;
            private InputPressOwner owner;
            private bool held;

            public ActionSampler(InputBinding binding, bool yielding = true)
            {
                this.binding = binding;
                this.yielding = yielding;
            }

            public float Value { get; private set; }

            public bool Released { get; private set; }

            /// <summary>One frame in which the binding reads <paramref name="read"/>; returns whether the action reports a press.</summary>
            public bool Frame(float read, UiInputFocus focus, bool taken = false)
            {
                var previous = held;
                Value = InputActionYield.Read(ref owner, binding, read, focus, yielding, yielding && taken);
                held = Math.Abs(Value) > InputActionYield.ReleasedBelow;
                Released = !held && previous;
                return held && !previous;
            }
        }
    }
}
