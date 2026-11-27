using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        private static readonly InputBinding KeyA = InputBinding.Key("A");
        private static readonly InputBinding PadRightY = InputBinding.GamepadAxis(InputGamepadAxis.RightY);

        /// <summary>
        /// In a level the game keeps its menu map enabled next to its gameplay map from the level's start until its first
        /// pause, so its navigate action listens to W, A, S and D while they walk the player. A window over gameplay with one
        /// of its controls selected took them, and the player could not walk: a window never takes the keys the game's menus
        /// share with its gameplay while that gameplay listens, while the overlay, a modal and a fullscreen tool still do.
        /// </summary>
        private static void AWindowOverGameplayLeavesTheSharedKeysToThePlayer()
        {
            var rig = new Rig();
            BindTheGamesNavigation(rig.Navigation);
            rig.Navigation.Listening = true;
            rig.Navigation.Gameplay = true;
            var windowRoot = new Root();
            rig.Order.Push(new Surface(TopiaForgeLayerBand.Window), TopiaForgeInputClaim.NavigationWhileSelected, windowRoot);
            rig.Selection = windowRoot;
            AssertSame(rig.Order.FocusRoot(), windowRoot, "the window's selected control is where the kit navigates");
            foreach (var key in new[] { W, A, S, D })
            {
                Assert(rig.Key(key, true), "over gameplay, a window with its control selected leaves " + key + " to the player, who walks");
                Assert(!rig.Gate.IsTaken(KeyboardId, key), "and takes nothing of it (key " + key + ")");
                rig.Key(key, false);
            }

            Assert(!rig.Key(DownArrow, true), "the arrows still navigate the window");
            Assert(!rig.Key(Enter, true) && !rig.Key(Space, true) && !rig.Key(Tab, true), "and so do Enter, Space and Tab");
            Assert(!rig.Stick(LeftStick, 0f, 1f) && !rig.Stick(Dpad, 0f, -1f) && !rig.Button(South, true),
                "and the controller's left stick, D-pad and A");
            foreach (var key in new[] { DownArrow, Enter, Space, Tab })
            {
                rig.Key(key, false);
            }

            rig.Stick(LeftStick, 0f, 0f);
            rig.Stick(Dpad, 0f, 0f);
            rig.Button(South, false);
            Assert(rig.RightStick(0f, 1f), "the right stick, which the game's navigation binds, keeps turning the camera");
            rig.RightStick(0f, 0f);

            var focus = new UiInputFocus(rig.Claims, rig.Order.HoldsFocus(), false, false, rig.Navigation);
            Assert(!InputActionYield.Yields(KeyW, focus) && !InputActionYield.Yields(KeyA, focus),
                "a mod's W and A actions fire while the player walks");
            Assert(!InputActionYield.Yields(PadRightX, focus) && !InputActionYield.Yields(PadRightY, focus), "and so do its right-stick bindings");
            Assert(InputActionYield.Yields(KeyUp, focus) && InputActionYield.Yields(KeyReturn, focus) && InputActionYield.Yields(PadLeftX, focus),
                "while its arrow, Enter and left-stick bindings yield to the window");

            foreach (var (name, band) in new[] { ("the manager overlay", TopiaForgeLayerBand.Window), ("a kit modal", TopiaForgeLayerBand.Modal), ("a fullscreen tool", TopiaForgeLayerBand.Window) })
            {
                var surface = new Surface(band);
                rig.Order.Push(surface, TopiaForgeInputClaim.Navigation, new Root());
                foreach (var (key, x, y) in new[] { (W, 0f, 1f), (A, -1f, 0f), (S, 0f, -1f), (D, 1f, 0f) })
                {
                    Assert(!rig.Key(key, true), name + " takes " + key + " over gameplay too");
                    rig.Gate.Direction(out var movedX, out var movedY);
                    Assert(movedX == x && movedY == y, name + " moves the way the game's binding of " + key + " points (" + movedX + ", " + movedY + ")");
                    rig.Key(key, false);
                }

                Assert(!rig.RightStick(0f, 1f), name + " takes the right stick the game's navigation binds");
                rig.RightStick(0f, 0f);
                rig.Order.Remove(surface);
            }

            AssertEqual(rig.GamePresses(KeyboardId, W) + rig.GamePresses(KeyboardId, A) + rig.GamePresses(KeyboardId, S) + rig.GamePresses(KeyboardId, D), 4,
                "the game saw W, A, S and D only while the window alone was open");
        }

        /// <summary>
        /// Whether the game's menu map is on in a level changes nothing for the player: it stays on from the level's start
        /// until the first pause, and is off after it. Either way W walks under a window, and a mod's W action fires.
        /// </summary>
        private static void TheGamesMenuMapInALevelChangesNothingForThePlayer()
        {
            foreach (var menuMapOn in new[] { true, false })
            {
                var rig = new Rig();
                BindTheGamesNavigation(rig.Navigation);
                rig.Navigation.Listening = menuMapOn;
                rig.Navigation.Gameplay = true;
                var windowRoot = new Root();
                rig.Order.Push(new Surface(TopiaForgeLayerBand.Window), TopiaForgeInputClaim.NavigationWhileSelected, windowRoot);
                rig.Selection = windowRoot;
                var state = menuMapOn ? "before the first pause" : "after it";
                for (var press = 0; press < 3; press++)
                {
                    Assert(rig.Key(W, true), state + ": W walks under the window (press " + press + ")");
                    rig.Key(W, false);
                }

                var focus = new UiInputFocus(rig.Claims, rig.Order.HoldsFocus(), false, false, rig.Navigation);
                Assert(!InputActionYield.Yields(KeyW, focus), state + ": a mod's W action fires");
                Assert(InputActionYield.Yields(KeyUp, focus), state + ": its Up arrow action still yields to the window");
                AssertEqual(rig.GamePresses(KeyboardId, W), 3, state + ": the game saw every W");
            }
        }

        /// <summary>
        /// At the game's menus, where its gameplay input is off (the main menu, the pause menu), W, A, S and D only navigate,
        /// so a window with its control selected takes them as it takes the arrows, and a key taken there stays TopiaForge's
        /// until it is released, though the game's gameplay comes back meanwhile.
        /// </summary>
        private static void AtTheGamesMenusAWindowTakesTheSharedKeys()
        {
            var rig = new Rig();
            BindTheGamesNavigation(rig.Navigation);
            rig.Navigation.Listening = true;
            var windowRoot = new Root();
            rig.Order.Push(new Surface(TopiaForgeLayerBand.Window), TopiaForgeInputClaim.NavigationWhileSelected, windowRoot);
            rig.Selection = windowRoot;
            Assert(!rig.Key(W, true), "at the menus, W moves the window's selection and not the game's menu beneath");
            rig.Gate.Direction(out var x, out var y);
            Assert(x == 0f && y == 1f, "up, as the game binds it");
            var focus = new UiInputFocus(rig.Claims, rig.Order.HoldsFocus(), false, false, rig.Navigation);
            Assert(InputActionYield.Yields(KeyW, focus), "and a mod's W action yields to the window navigating with it");
            Assert(!rig.RightStick(0f, 1f), "the right stick the game's navigation binds moves the window too");
            rig.RightStick(0f, 0f);

            rig.Navigation.Gameplay = true;
            Assert(!rig.Key(W, true), "a W taken at the menus stays TopiaForge's while held, as the game's gameplay comes back");
            rig.Key(W, false);
            Assert(rig.Key(W, true), "and once released, the next W walks");
            rig.Key(W, false);
            AssertEqual(rig.GamePresses(KeyboardId, W), 1, "the game saw only that W");
        }
    }
}
