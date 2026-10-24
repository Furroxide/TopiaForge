using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The interaction contract for what TopiaForge surfaces take from the game, run against the Unity-free core that the
    /// Input System filter, the dismiss stack and the kit's own navigation use: one Back press dismisses one surface and
    /// the game sees none of it, a held key stays TopiaForge's until it is released, the next press reaches the game,
    /// navigation comes back on every way a surface ends, key captures and kit text fields keep every key, a controller
    /// behaves like the keyboard, and nothing is allocated at rest. The Unity side of each is pinned by
    /// UiKitInputOwnershipConventionTests.
    /// </summary>
    internal static partial class UiKitInputOwnershipTests
    {
        // Input System key numbers.
        private const int Space = 1;
        private const int Enter = 2;
        private const int Tab = 3;
        private const int A = 15;
        private const int B = 16;
        private const int E = 19;
        private const int V = 36;
        private const int W = 37;
        private const int Escape = 60;
        private const int LeftArrow = 61;
        private const int RightArrow = 62;
        private const int UpArrow = 63;
        private const int DownArrow = 64;
        private const int NumpadEnter = 77;
        private const int F10 = 103;

        // The filter's numbers for a controller's controls.
        private const int East = 0;
        private const int South = 1;
        private const int Dpad = 2;
        private const int LeftStick = 3;

        private const int KeyboardId = 1;
        private const int GamepadId = 2;

        public static void Run()
        {
            ClaimsCoverTheContractsControls();
            OneBackPressDismissesOneSurfaceAndNothingElse();
            AHeldBackNeverRepeatsIntoTheGame();
            TheNextBackPressReachesTheGame();
            APressHeldBeforeAClaimStaysTheGames();
            TheOverlayGivesNavigationBackOnEveryPath();
            ADeadSurfaceTakesNothingAndIsNeverDismissed();
            WindowsTakeNavigationOnlyWhileTheirControlIsSelected();
            AKeyCaptureTakesTheKeyboardAndBackCancelsOnlyIt();
            TypingInAKitTextFieldReachesNoGameKey();
            AControllerBehavesLikeTheKeyboard();
            RapidTogglesNeverStrandAKey();
            DevicesThatGoAwayLetGo();
            TheKitNavigatesAtTheUiModulesPace();
            TheKitNavigatesOnlyWhileItsSurfaceClaimsNavigation();
            AModsOwnKeyStillReachesItsAction();
            NothingIsAllocatedAtRestOrWhileHeld();
            Console.WriteLine("UiKitInputOwnershipTests passed.");
        }

        private static void ClaimsCoverTheContractsControls()
        {
            var keyboard = TopiaForgeInputDevice.Keyboard;
            var pad = TopiaForgeInputDevice.Gamepad;
            var roles = (TopiaForgeInputRole[])Enum.GetValues(typeof(TopiaForgeInputRole));
            foreach (var role in roles)
            {
                Assert(!TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.None, role, keyboard), "no claim covers a " + role + " key");
                Assert(!TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.None, role, pad), "no claim covers a " + role + " control");
                Assert(!TopiaForgeInputClaims.Cover(AllClaims, role, TopiaForgeInputDevice.Other), "nothing ever covers a mouse or touch " + role);
                Assert(TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.Keyboard, role, keyboard), "the keyboard claim covers a " + role + " key");
                Assert(!TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.Keyboard, role, pad), "the keyboard claim leaves a controller's " + role + " alone");
                Assert(!TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.NavigationWhileSelected, role, keyboard),
                    "an unresolved window claim covers nothing by itself (" + role + ")");
            }

            Assert(TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.Back, TopiaForgeInputRole.Back, keyboard), "Back covers Escape");
            Assert(TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.Back, TopiaForgeInputRole.Back, pad), "and a controller's B");
            foreach (var role in new[] { TopiaForgeInputRole.Direction, TopiaForgeInputRole.Submit, TopiaForgeInputRole.Tab, TopiaForgeInputRole.Key })
            {
                Assert(!TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.Back, role, keyboard), "Back alone lets a " + role + " key through");
                Assert(!TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.Back, role, pad), "and a controller's " + role);
            }

            foreach (var role in new[] { TopiaForgeInputRole.Back, TopiaForgeInputRole.Direction, TopiaForgeInputRole.Submit, TopiaForgeInputRole.Tab })
            {
                Assert(TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.Navigation, role, keyboard), "navigation covers the " + role + " keys");
                Assert(TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.Navigation, role, pad), "and a controller's " + role);
            }

            Assert(!TopiaForgeInputClaims.Cover(TopiaForgeInputClaim.Navigation, TopiaForgeInputRole.Key, keyboard),
                "navigation lets every other key through, W and E among them");
        }

        private static void OneBackPressDismissesOneSurfaceAndNothingElse()
        {
            var rig = new Rig();
            var shop = new Surface(TopiaForgeLayerBand.Window);
            var overlay = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(shop, TopiaForgeInputClaim.NavigationWhileSelected, new Root());
            rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, new Root());

            Assert(!rig.Key(Escape, true), "the Escape that closes the overlay never reaches the game");
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), overlay, "one Back press dismisses the topmost surface");
            AssertEqual(overlay.Dismissals, 1, "once");
            AssertEqual(shop.Dismissals, 0, "and nothing else");
            Assert(rig.Order.Contains(shop) && !rig.Order.Contains(overlay), "the overlay left the stack and the shop stayed");
            rig.Key(Escape, false);
            AssertSame(rig.EndFrame(), null, "letting go dismisses nothing");

            Assert(!rig.Button(East, true), "a controller's B is Back too, and the game sees none of it");
            AssertSame(rig.EndFrame(), shop, "B dismisses the next surface");
            AssertEqual(shop.Dismissals, 1, "once");
            rig.Button(East, false);
            AssertEqual(rig.GamePresses(KeyboardId, Escape) + rig.GamePresses(GamepadId, East), 0, "the game saw no Back press at all");
        }

        private static void AHeldBackNeverRepeatsIntoTheGame()
        {
            var rig = new Rig();
            var shop = new Surface(TopiaForgeLayerBand.Window);
            var overlay = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(shop, TopiaForgeInputClaim.NavigationWhileSelected, new Root());
            rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, new Root());

            rig.Key(Escape, true);
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), overlay, "the press closes the overlay");
            for (var frame = 0; frame < 30; frame++)
            {
                // Later keyboard events still report Escape held, and other keys come and go.
                Assert(!rig.Key(Escape, true), "a held Escape never reaches the game (frame " + frame + ")");
                rig.Key(W, frame % 2 == 0);
                AssertSame(rig.EndFrame(legacyHeld: true), null, "a held Escape dismisses nothing more (frame " + frame + ")");
            }

            AssertEqual(shop.Dismissals, 0, "the shop beneath stays open while Escape is held");
            Assert(rig.Gate.BackHeld, "the held Escape is still TopiaForge's");
            Assert(!rig.Key(Escape, false), "its release reaches nothing");
            rig.EndFrame();
            Assert(!rig.Gate.BackHeld, "and lets go of it");
            AssertEqual(rig.GamePresses(KeyboardId, W), 15, "W, which no surface claims, reached the game every time");

            // Hold Escape on the shop alone: it closes, and the pause menu never opens while the key stays down.
            rig.Key(Escape, true);
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), shop, "the next press closes the shop");
            AssertEqual(rig.Order.Count, 0, "no surface is left");
            for (var frame = 0; frame < 30; frame++)
            {
                Assert(!rig.Key(Escape, true), "an Escape held after the last surface closed still never reaches the game");
                rig.EndFrame(legacyHeld: true);
            }

            rig.Key(Escape, false);
            rig.EndFrame();
            AssertEqual(rig.GamePresses(KeyboardId, Escape), 0, "the game saw no Escape");
        }

        private static void TheNextBackPressReachesTheGame()
        {
            var rig = new Rig();
            var shop = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(shop, TopiaForgeInputClaim.NavigationWhileSelected, new Root());
            rig.Key(Escape, true);
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), shop, "the first Escape closes the shop");
            rig.Key(Escape, false);
            rig.EndFrame();

            Assert(rig.Key(Escape, true), "the next Escape, with no surface left, reaches the game, and its pause menu opens");
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), null, "and TopiaForge dismisses nothing");
            Assert(!rig.Gate.BackHeld, "nor holds the key");
            rig.Key(Escape, false);
            AssertEqual(rig.GamePresses(KeyboardId, Escape), 1, "the game saw exactly that press");

            Assert(rig.Button(East, true), "so does a controller's next B");
            rig.Button(East, false);
        }

        private static void APressHeldBeforeAClaimStaysTheGames()
        {
            var rig = new Rig();
            Assert(rig.Key(Space, true), "Space pressed in play reaches the game: the player jumps");
            Assert(rig.Stick(LeftStick, 0f, 0.9f), "the stick pushed in play moves the player");

            var overlay = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, new Root());
            Assert(!rig.Key(Space, true), "a Space still held as the overlay opens is no new press");
            AssertEqual(rig.Seen(KeyboardId, Space), 1f, "and stays the game's: it never sees a release it did not make");
            AssertEqual(rig.Gate.IsTaken(KeyboardId, Space), false, "TopiaForge does not take a press that began before its claim");
            rig.Stick(LeftStick, 0f, 0.8f);
            AssertEqual(rig.Seen(GamepadId, LeftStick), 0.8f, "nor the stick still pushed");

            rig.Key(Space, false);
            AssertEqual(rig.Seen(KeyboardId, Space), 0f, "the game sees its own release");
            rig.Stick(LeftStick, 0f, 0f);
            Assert(!rig.Key(Space, true), "the next Space, pressed while the overlay shows, is the overlay's");
            Assert(rig.Gate.SubmitPressedIn(rig.Frame), "and submits for the kit");
            Assert(!rig.Stick(LeftStick, 0f, 0.9f), "as the next push of the stick navigates the overlay");
            AssertEqual(rig.GamePresses(KeyboardId, Space), 1, "the game saw only the jump");
        }

        private static void TheOverlayGivesNavigationBackOnEveryPath()
        {
            foreach (var path in new[] { "closed", "dismissed", "a scene change", "its canvas lost", "an exception", "the plugin's teardown" })
            {
                var rig = new Rig();
                var overlay = new Surface(TopiaForgeLayerBand.Window);
                var root = new Root();
                rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, root);
                Assert(!rig.Key(DownArrow, true), path + ": while the overlay shows, the arrows never reach the menu beneath");
                Assert(!rig.Button(South, true), path + ": nor a controller's A");
                Assert(!rig.Stick(Dpad, 0f, -1f), path + ": nor its D-pad");
                rig.Key(DownArrow, false);
                rig.Button(South, false);
                rig.Stick(Dpad, 0f, 0f);
                rig.EndFrame();

                Assert(!rig.Key(UpArrow, true), path + ": an Up pressed in the overlay is the overlay's");
                End(rig, overlay, root, path);
                AssertEqual(rig.Claims & TopiaForgeInputClaim.Navigation, TopiaForgeInputClaim.None, path + ": the overlay takes no navigation once it is gone");
                if (path == "the plugin's teardown")
                {
                    AssertEqual(rig.Gate.Held, 0, path + ": lets go of every key");
                }
                else
                {
                    Assert(!rig.Key(UpArrow, true), path + ": an Up held across it stays TopiaForge's until it is released");
                }

                rig.Key(UpArrow, false);
                Assert(rig.Key(DownArrow, true), path + ": then the arrows reach the game's menu again");
                Assert(rig.Key(Enter, true), path + ": and Enter");
                Assert(rig.Key(Space, true), path + ": and Space");
                Assert(rig.Key(Tab, true), path + ": and Tab");
                Assert(rig.Button(South, true), path + ": and a controller's A");
                Assert(rig.Stick(LeftStick, 0f, 1f), path + ": and its stick");
                Assert(rig.Key(Escape, true), path + ": and Escape, so the pause menu opens");
                AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), null, path + ": with nothing left for Back to dismiss");
                AssertEqual(rig.Gate.Held, 0, path + ": nothing is left held");
            }
        }

        private static void End(Rig rig, Surface overlay, Root root, string path)
        {
            switch (path)
            {
                case "closed":
                    rig.Order.Remove(overlay);
                    break;
                case "dismissed":
                    rig.Key(Escape, true);
                    AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), overlay, "Back dismisses the overlay");
                    rig.Key(Escape, false);
                    rig.EndFrame();
                    break;
                case "a scene change":
                    // A dialog that does not survive scene loads goes with the scene's objects, and nothing removes it.
                    root.Live = false;
                    break;
                case "its canvas lost":
                    // Another mod or the game destroys the canvas; the kit's loss path then takes the surface off the stack.
                    root.Live = false;
                    AssertEqual(rig.Claims, TopiaForgeInputClaim.None, "a lost canvas takes nothing even before the loss path runs");
                    rig.Order.Remove(overlay);
                    break;
                case "an exception":
                    overlay.Throws = true;
                    rig.Key(Escape, true);
                    AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), overlay, "Back reaches the overlay, which throws");
                    Assert(!rig.Order.Contains(overlay), "a surface that throws as it closes has still left the stack");
                    rig.Key(Escape, false);
                    rig.EndFrame();
                    break;
                default:
                    rig.Order.Clear();
                    rig.Gate.ForgetAll();
                    break;
            }
        }

        private static void ADeadSurfaceTakesNothingAndIsNeverDismissed()
        {
            var rig = new Rig();
            var window = new Surface(TopiaForgeLayerBand.Window);
            var modal = new Surface(TopiaForgeLayerBand.Modal);
            var modalRoot = new Root();
            var voice = new Surface(TopiaForgeLayerBand.Modal);
            rig.Order.Push(window, TopiaForgeInputClaim.NavigationWhileSelected, new Root());
            rig.Order.Push(modal, TopiaForgeInputClaim.Navigation, modalRoot);
            AssertEqual(rig.Claims, TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Navigation, "a modal takes navigation while it shows");

            modalRoot.Live = false;
            AssertEqual(rig.Claims, TopiaForgeInputClaim.Back, "a modal whose canvas went with a scene takes nothing");
            AssertSame(rig.Order.Top(), window, "and is never what Back dismisses");
            Assert(rig.Key(Enter, true), "Enter reaches the game again");
            rig.Key(Escape, true);
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), window, "Back dismisses the live window beneath");
            AssertEqual(modal.Dismissals, 0, "never the dead modal");
            rig.Key(Escape, false);
            rig.EndFrame();

            rig.Order.Push(voice, TopiaForgeInputClaim.Keyboard, null);
            AssertEqual(rig.Claims, TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Keyboard, "a surface pushed without a root is always live");
            AssertSame(rig.Order.FocusRoot(), null, "and is no place for the kit to navigate");
        }

        private static void WindowsTakeNavigationOnlyWhileTheirControlIsSelected()
        {
            var rig = new Rig();
            var shop = new Surface(TopiaForgeLayerBand.Window);
            var shopRoot = new Root();
            rig.Order.Push(shop, TopiaForgeInputClaim.NavigationWhileSelected, shopRoot);
            AssertEqual(rig.Claims, TopiaForgeInputClaim.Back, "a window with none of its controls selected takes Back only");
            AssertSame(rig.Order.FocusRoot(), null, "and the kit leaves navigation to the game");
            Assert(rig.Key(Enter, true), "Enter reaches the game");
            Assert(rig.Key(Space, true), "Space reaches gameplay");
            Assert(rig.Stick(LeftStick, 1f, 0f), "and the stick moves the player");
            rig.Key(Enter, false);
            rig.Key(Space, false);
            rig.Stick(LeftStick, 0f, 0f);

            rig.Selection = shopRoot;
            AssertEqual(rig.Claims, TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Navigation, "with one of its controls selected, it takes navigation");
            AssertSame(rig.Order.FocusRoot(), shopRoot, "and the kit navigates inside it");
            Assert(!rig.Key(Enter, true), "Enter is the window's, so its selected control alone answers it");
            Assert(!rig.Key(DownArrow, true), "and so are the arrows");
            Assert(!rig.Stick(LeftStick, 0f, -1f), "and the stick");
            Assert(rig.Key(W, true), "W still reaches gameplay");
            rig.Key(Enter, false);
            rig.Key(DownArrow, false);
            rig.Stick(LeftStick, 0f, 0f);
            rig.Key(W, false);

            rig.Selection = new Root();
            AssertEqual(rig.Claims, TopiaForgeInputClaim.Back, "once the selection leaves it, it takes Back only again");
            Assert(rig.Key(Enter, true), "and Enter reaches the game");
            rig.Key(Enter, false);
        }

        private static readonly TopiaForgeInputClaim AllClaims = TopiaForgeInputClaim.Back | TopiaForgeInputClaim.Navigation
            | TopiaForgeInputClaim.NavigationWhileSelected | TopiaForgeInputClaim.Keyboard;

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private static void AssertEqual<T>(T actual, T expected, string message)
        {
            if (!Equals(actual, expected))
            {
                throw new InvalidOperationException("Assertion failed: " + message + " (expected " + expected + ", got " + actual + ")");
            }
        }

        private static void AssertSame(object? actual, object? expected, string message)
        {
            if (!ReferenceEquals(actual, expected))
            {
                throw new InvalidOperationException("Assertion failed: " + message + " (expected " + Describe(expected) + ", got " + Describe(actual) + ")");
            }
        }

        private static string Describe(object? value) => value == null ? "nothing" : value.ToString() ?? value.GetType().Name;
    }
}
