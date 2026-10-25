using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        private static readonly InputBinding KeyU = InputBinding.Key("U");
        private static readonly InputBinding KeyEscape = InputBinding.Key("Escape");
        private static readonly InputBinding KeyReturn = InputBinding.Key("Return");
        private static readonly InputBinding KeyUp = InputBinding.Key("UpArrow");
        private static readonly InputBinding KeySpace = InputBinding.Key("Space");
        private static readonly InputBinding KeyTab = InputBinding.Key("Tab");
        private static readonly InputBinding MousePrimary = InputBinding.MouseButton(InputMouseButton.Primary);
        private static readonly InputBinding PadSouth = InputBinding.GamepadButton(InputGamepadButton.South);
        private static readonly InputBinding PadEast = InputBinding.GamepadButton(InputGamepadButton.East);
        private static readonly InputBinding PadNorth = InputBinding.GamepadButton(InputGamepadButton.North);
        private static readonly InputBinding PadLeftX = InputBinding.GamepadAxis(InputGamepadAxis.LeftX);
        private static readonly InputBinding PadRightX = InputBinding.GamepadAxis(InputGamepadAxis.RightX);
        private static readonly InputBinding MoveAxis = InputBinding.Axis(InputAxis.Horizontal);
        private static readonly InputBinding PointerAxis = InputBinding.Axis(InputAxis.PointerX);
        private static readonly InputBinding ScrollAxis = InputBinding.Axis(InputAxis.Scroll);

        /// <summary>
        /// A mod's own key reaches its action wherever the pointer is and whichever kit control is selected, as the ui
        /// template's U toggling its window at the main menu and over the window itself, and a binding yields only to UI that
        /// holds its control.
        /// </summary>
        private static void AModsOwnKeyStillReachesItsAction()
        {
            // The main menu: the pointer over the game's full-screen menu, nothing of TopiaForge's open, nothing selected or a
            // kit menu button selected (a kit control holds nothing by itself).
            var menu = new UiInputFocus(TopiaForgeInputClaim.None, false, false, true);
            Assert(!InputActionYield.Yields(KeyU, menu), "U reaches the template's action at the main menu, over the game's menu");
            Assert(!InputActionYield.Yields(PadSouth, menu), "and so does a controller's A");
            Assert(InputActionYield.Yields(MousePrimary, menu), "while a click over the menu stays the menu's");
            Assert(InputActionYield.Yields(PointerAxis, menu) && InputActionYield.Yields(ScrollAxis, menu), "and so do the pointer's motion and the wheel");

            // The template's window shows, one of its controls is selected and the pointer is over it.
            var window = new UiInputFocus(TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Navigation, false, false, true);
            Assert(!InputActionYield.Yields(KeyU, window), "U reaches the action over the mod's own window, so it closes it");
            Assert(!InputActionYield.Yields(PadNorth, window) && !InputActionYield.Yields(PadRightX, window),
                "and so do controls the window does not handle");
            foreach (var handled in new[] { KeyEscape, KeyReturn, KeyUp, KeySpace, KeyTab, PadSouth, PadEast, PadLeftX, MoveAxis })
            {
                Assert(InputActionYield.Yields(handled, window), "a control the window handles yields to it (" + handled.Control + ")");
            }

            Assert(InputActionYield.Yields(InputBinding.Key("escape"), window), "key names match whatever their case");

            // The window shows with none of its controls selected: only Back is the window's.
            var unfocused = new UiInputFocus(TopiaForgeInputClaim.Back, false, false, false);
            Assert(InputActionYield.Yields(KeyEscape, unfocused) && InputActionYield.Yields(PadEast, unfocused), "Back is the window's");
            Assert(!InputActionYield.Yields(KeyReturn, unfocused) && !InputActionYield.Yields(MoveAxis, unfocused),
                "Enter and movement are gameplay's");
            Assert(!InputActionYield.Yields(MousePrimary, unfocused), "and a click away from UI fires");

            // Typing, a key capture, the manager overlay, a modal or a fullscreen tool hold everything.
            var held = new UiInputFocus(TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Keyboard, true, false, false);
            foreach (var binding in new[] { KeyU, KeyEscape, MousePrimary, PadNorth, PadRightX, PointerAxis })
            {
                Assert(InputActionYield.Yields(binding, held), "a surface holding the focus holds every control (" + binding.Control + ")");
            }

            // One of the game's own text fields has the keyboard, as its reply field to a robot does: keys and controller
            // controls wait.
            var gameText = new UiInputFocus(TopiaForgeInputClaim.None, false, true, false);
            Assert(InputActionYield.Yields(KeyU, gameText) && InputActionYield.Yields(PadSouth, gameText) && InputActionYield.Yields(MoveAxis, gameText),
                "while one of the game's text fields has the keyboard, the keys are the field's");
            Assert(!InputActionYield.Yields(MousePrimary, gameText) && !InputActionYield.Yields(PointerAxis, gameText),
                "but a click away from UI fires");

            // A game control merely selected, as the game's menu panel stays after a click on RESUME GAME, holds nothing.
            var resumed = new UiInputFocus(TopiaForgeInputClaim.None, false, false, false);
            foreach (var binding in new[] { KeyU, KeyEscape, KeyReturn, KeySpace, PadSouth, PadNorth, MoveAxis, PadLeftX })
            {
                Assert(!InputActionYield.Yields(binding, resumed), "back in the level, the mod's own controls work again (" + binding.Control + ")");
            }

            // An action that never yields reads its bindings with no focus at all.
            foreach (var binding in new[] { KeyU, KeyEscape, MousePrimary, PadSouth, MoveAxis, PointerAxis, default(InputBinding) })
            {
                Assert(!InputActionYield.Yields(binding, default), "with no focus nothing yields (" + binding.Control + ")");
            }
        }
    }
}
