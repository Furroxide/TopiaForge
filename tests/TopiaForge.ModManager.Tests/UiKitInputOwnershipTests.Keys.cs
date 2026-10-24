using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        private static void AKeyCaptureTakesTheKeyboardAndBackCancelsOnlyIt()
        {
            var rig = new Rig();
            var overlay = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, new Root());
            var cancelled = 0;
            var capture = new TopiaForgeKeyCapture(() => cancelled++);
            var field = new Root();
            rig.Order.Push(capture, TopiaForgeInputClaim.Keyboard, field);

            // A mod's window opens after the capture began: the capture still answers Back first.
            var later = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(later, TopiaForgeInputClaim.NavigationWhileSelected, new Root());
            AssertSame(rig.Order.FocusRoot(), null, "while a capture waits, the kit navigates nothing: the key pressed is the capture's");

            Assert(!rig.Key(B, true), "the key the player captures never reaches the game");
            Assert(rig.Gate.KeyPressedIn(B, rig.Frame), "the capture reads it where TopiaForge took it");
            for (var key = A; key <= W; key++)
            {
                if (key == B)
                {
                    continue;
                }

                Assert(!rig.Key(key, true), "no key pressed during a capture reaches the game (key " + key + ")");
                rig.Key(key, false);
            }

            rig.Order.Remove(capture);
            Assert(!rig.Key(B, true), "the captured key, still held after the capture ends, stays TopiaForge's");
            rig.Key(B, false);
            Assert(rig.Key(B, true), "and its next press reaches the game");
            rig.Key(B, false);

            // The next capture waits while yet another window opens over it: the modal band still puts the capture first.
            rig.Order.Push(capture, TopiaForgeInputClaim.Keyboard, field);
            var latest = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(latest, TopiaForgeInputClaim.NavigationWhileSelected, new Root());
            Assert(!rig.Key(Escape, true), "the Escape that cancels a capture never reaches the game");
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), capture, "one Escape cancels the capture, above windows opened after it");
            AssertEqual(cancelled, 1, "once");
            AssertEqual(latest.Dismissals + later.Dismissals + overlay.Dismissals, 0, "and closes nothing around it");
            rig.Key(Escape, false);
            rig.EndFrame();
            rig.Key(Escape, true);
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), latest, "the next Escape closes the window on top");
            rig.Key(Escape, false);
            rig.EndFrame();
            rig.Key(Escape, true);
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), later, "then the one beneath");
            rig.Key(Escape, false);
            rig.EndFrame();
            rig.Key(Escape, true);
            AssertSame(rig.EndFrame(legacyHeld: true, legacyPressed: true), overlay, "and then the overlay");
            rig.Key(Escape, false);
            rig.EndFrame();
            Assert(rig.Key(Escape, true), "only then does Escape reach the game");
            rig.Key(Escape, false);
            rig.EndFrame();

            var hidden = new TopiaForgeKeyCapture(() => cancelled++);
            var hiddenField = new Root();
            rig.Order.Push(hidden, TopiaForgeInputClaim.Keyboard, hiddenField);
            hiddenField.Live = false;
            AssertEqual(rig.Claims, TopiaForgeInputClaim.None, "a capture whose field is hidden or destroyed keeps nothing");
            Assert(rig.Key(V, true), "so its keys reach the game");
        }

        private static void TypingInAKitTextFieldReachesNoGameKey()
        {
            var rig = new Rig();
            rig.Order.Push(new Surface(TopiaForgeLayerBand.Window), TopiaForgeInputClaim.Navigation, new Root());
            rig.TextFieldHasKeyboard = true;
            foreach (var key in new[] { W, A, Space, E, V, Enter, NumpadEnter, UpArrow, LeftArrow, RightArrow, Tab, F10 })
            {
                Assert(!rig.Key(key, true), "typing key " + key + " in a kit text field reaches no game action");
                Assert(rig.Gate.KeyPressedIn(key, rig.Frame), "and the kit still reads it (key " + key + ")");
                rig.Key(key, false);
            }

            rig.Key(E, true);
            rig.TextFieldHasKeyboard = false;
            Assert(!rig.Key(E, true), "an E held as the field loses the keyboard stays TopiaForge's");
            rig.Key(E, false);
            Assert(rig.Key(E, true), "the next E reaches the game, which needs none of the overlay's navigation");
            rig.Key(E, false);
            AssertEqual(rig.GamePresses(KeyboardId, W) + rig.GamePresses(KeyboardId, Space) + rig.GamePresses(KeyboardId, V), 0,
                "the game saw none of the typing");

            // A text field on a HUD, with no surface on the stack, keeps the keyboard too: Escape never opens the pause menu.
            var hud = new Rig { TextFieldHasKeyboard = true };
            Assert(!hud.Key(Escape, true), "Escape typed into a HUD's text field never reaches the game");
            Assert(!hud.Key(Space, true), "nor does Space");
        }

        private static void AControllerBehavesLikeTheKeyboard()
        {
            var rig = new Rig();
            var overlay = new Surface(TopiaForgeLayerBand.Window);
            rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, new Root());

            Assert(!rig.Button(South, true), "A, pressed in the overlay, never reaches the menu beneath");
            Assert(rig.Gate.SubmitPressedIn(rig.Frame), "and submits for the kit, as Enter does");
            rig.Button(South, false);

            Assert(!rig.Stick(Dpad, 0f, 1f), "the D-pad is the overlay's");
            rig.Gate.Direction(out var x, out var y);
            Assert(x == 0f && y == 1f, "and points the kit up (" + x + ", " + y + ")");
            rig.Stick(Dpad, 0f, 0f);

            Assert(!rig.Stick(LeftStick, 0.3f, 0f), "a stick resting short of a push is no press");
            AssertEqual(rig.Seen(GamepadId, LeftStick), 0.3f, "and passes as it is");
            rig.Stick(LeftStick, 0f, 0f);
            Assert(!rig.Stick(LeftStick, 0.8f, 0f), "pushed, it is the overlay's");
            AssertEqual(rig.Seen(GamepadId, LeftStick), 0f, "and the game sees it at rest");
            rig.Stick(LeftStick, 0.3f, 0f);
            AssertEqual(rig.Seen(GamepadId, LeftStick), 0f, "settling back, it stays TopiaForge's until it is nearly at rest");
            rig.Stick(LeftStick, 0.1f, 0f);
            AssertEqual(rig.Seen(GamepadId, LeftStick), 0.1f, "then it is the game's again");
            AssertEqual(rig.GamePresses(GamepadId, LeftStick), 0, "the game never saw the stick pushed");
            rig.Stick(LeftStick, 0f, 0f);

            Assert(!rig.Button(East, true), "B closes the overlay");
            AssertSame(rig.EndFrame(), overlay, "as Escape does");
            Assert(!rig.Button(East, true), "and a held B stays TopiaForge's after it closed");
            rig.EndFrame();
            rig.Button(East, false);
            Assert(rig.Button(East, true), "the next B reaches the game");
            rig.Button(East, false);
            Assert(rig.Key(Escape, true) && rig.Button(South, true), "keyboard and controller come back alike");
        }

        private static void RapidTogglesNeverStrandAKey()
        {
            var rig = new Rig();
            var overlay = new Surface(TopiaForgeLayerBand.Window);
            var root = new Root();
            for (var round = 0; round < 50; round++)
            {
                rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, root);
                Assert(!rig.Key(DownArrow, true), "round " + round + ": Down pressed while the overlay shows is the overlay's");
                rig.Order.Remove(overlay);
                Assert(!rig.Key(DownArrow, true), "round " + round + ": and stays so held across the close");
                rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, root);
                rig.Key(DownArrow, false);
                rig.Order.Remove(overlay);
                Assert(rig.Key(DownArrow, true), "round " + round + ": a press while it is closed reaches the game");
                rig.Order.Push(overlay, TopiaForgeInputClaim.Navigation, root);
                Assert(!rig.Key(DownArrow, true) && rig.Seen(KeyboardId, DownArrow) == 1f,
                    "round " + round + ": held as the overlay reopens, it stays the game's");
                rig.Key(DownArrow, false);
                AssertEqual(rig.Seen(KeyboardId, DownArrow), 0f, "round " + round + ": the game sees its own release");
                rig.Order.Remove(overlay);
                rig.EndFrame();
            }

            AssertEqual(rig.Gate.Held, 0, "rapid toggles leave nothing held");
            AssertEqual(rig.GamePresses(KeyboardId, DownArrow), 50, "the game saw exactly the presses made while the overlay was closed");
        }

        private static void DevicesThatGoAwayLetGo()
        {
            var rig = new Rig();
            rig.Order.Push(new Surface(TopiaForgeLayerBand.Window), TopiaForgeInputClaim.Navigation, new Root());
            rig.Key(Escape, true);
            rig.Key(DownArrow, true);
            rig.Button(East, true);
            AssertEqual(rig.Gate.Held, 3, "three presses are held");
            rig.Gate.Forget(KeyboardId);
            AssertEqual(rig.Gate.Held, 1, "a keyboard removed or reset lets go of its presses");
            Assert(rig.Gate.BackHeld, "the controller still holds B");
            rig.Gate.ForgetAll();
            Assert(rig.Gate.Held == 0 && !rig.Gate.BackHeld, "losing focus lets go of everything");

            // More keyboards and controllers than slots: a press on one more reaches the game rather than vanish.
            var gate = new TopiaForgeInputGate();
            for (var device = 1; device <= TopiaForgeInputGate.DeviceSlots; device++)
            {
                Assert(gate.Take(device, TopiaForgeInputDevice.Gamepad, East, TopiaForgeInputRole.Back, 1f, 0f, 0f, 0f, TopiaForgeInputClaim.Back, 0),
                    "controller " + device + "'s B is taken");
            }

            Assert(!gate.Take(99, TopiaForgeInputDevice.Gamepad, East, TopiaForgeInputRole.Back, 1f, 0f, 0f, 0f, TopiaForgeInputClaim.Back, 0),
                "one more controller's B passes to the game");
            gate.Take(1, TopiaForgeInputDevice.Gamepad, East, TopiaForgeInputRole.Back, 0f, 0f, 0f, 0f, TopiaForgeInputClaim.Back, 1);
            Assert(gate.Take(99, TopiaForgeInputDevice.Gamepad, East, TopiaForgeInputRole.Back, 1f, 0f, 0f, 0f, TopiaForgeInputClaim.Back, 1),
                "and a slot freed by a release serves it");
        }

        /// <summary>
        /// The Input System and the game around the core, as TopiaForgeInputOwnership drives them: a device holds what the game
        /// sees; each control an event carries goes through the gate, and a taken one is held at rest, as
        /// ResetToDefaultStateInEvent does; the game counts every press that reaches it. A frame ends with Back, as
        /// TopiaForgeDismissStack.TickEscape runs it.
        /// </summary>
        private sealed class Rig
        {
            private readonly float[,] seen = new float[3, TopiaForgeInputGate.ControlsPerDevice];
            private readonly int[,] presses = new int[3, TopiaForgeInputGate.ControlsPerDevice];

            public Rig()
            {
                Order = new TopiaForgeDismissOrder(root => ((Root)root).Live, root => ReferenceEquals(Selection, root));
            }

            public TopiaForgeInputGate Gate { get; } = new TopiaForgeInputGate();

            public TopiaForgeDismissOrder Order { get; }

            public int Frame { get; private set; }

            public bool TextFieldHasKeyboard { get; set; }

            public Root? Selection { get; set; }

            public TopiaForgeInputClaim Claims
            {
                get
                {
                    var claims = Order.Claims();
                    return TextFieldHasKeyboard ? claims | TopiaForgeInputClaim.Keyboard : claims;
                }
            }

            public int GamePresses(int device, int control) => presses[device, control];

            public float Seen(int device, int control) => seen[device, control];

            /// <summary>A keyboard event in which <paramref name="key"/> reads held or not; returns whether the game saw a press.</summary>
            public bool Key(int key, bool down)
            {
                var x = key == LeftArrow ? -1f : key == RightArrow ? 1f : 0f;
                var y = key == DownArrow ? -1f : key == UpArrow ? 1f : 0f;
                return Event(KeyboardId, TopiaForgeInputDevice.Keyboard, key, RoleOf(key), down ? 1f : 0f, x, y);
            }

            /// <summary>A controller event in which a button reads held or not.</summary>
            public bool Button(int control, bool down)
            {
                var role = control == East ? TopiaForgeInputRole.Back : TopiaForgeInputRole.Submit;
                return Event(GamepadId, TopiaForgeInputDevice.Gamepad, control, role, down ? 1f : 0f, 0f, 0f);
            }

            /// <summary>A controller event in which the D-pad or the left stick points somewhere.</summary>
            public bool Stick(int control, float x, float y)
            {
                return Event(GamepadId, TopiaForgeInputDevice.Gamepad, control, TopiaForgeInputRole.Direction, (float)Math.Sqrt((x * x) + (y * y)), x, y);
            }

            /// <summary>Ends a frame as the kit's driver does; returns the surface Back dismissed, if any.</summary>
            public ITopiaForgeDismissable? EndFrame(bool legacyHeld = false, bool legacyPressed = false)
            {
                var top = Order.TakeOnBack(legacyHeld || Gate.BackHeld, legacyPressed || Gate.BackPressedIn(Frame));
                try
                {
                    top?.Dismiss();
                }
                catch (InvalidOperationException)
                {
                    // TickEscape logs a surface that throws as it closes and goes on.
                }

                Frame++;
                return top;
            }

            private bool Event(int device, TopiaForgeInputDevice kind, int control, TopiaForgeInputRole role, float magnitude, float x, float y)
            {
                var claims = Claims;
                var before = seen[device, control];
                var applied = magnitude;
                if (Gate.Examines(kind, claims) && Gate.Take(device, kind, control, role, magnitude, before, x, y, claims, Frame))
                {
                    applied = 0f;
                }

                var reached = before < TopiaForgeInputGate.PressThreshold && applied >= TopiaForgeInputGate.PressThreshold;
                if (reached)
                {
                    presses[device, control]++;
                }

                seen[device, control] = applied;
                return reached;
            }

            private static TopiaForgeInputRole RoleOf(int key)
            {
                switch (key)
                {
                    case Escape:
                        return TopiaForgeInputRole.Back;
                    case UpArrow:
                    case DownArrow:
                    case LeftArrow:
                    case RightArrow:
                        return TopiaForgeInputRole.Direction;
                    case Enter:
                    case NumpadEnter:
                    case Space:
                        return TopiaForgeInputRole.Submit;
                    case Tab:
                        return TopiaForgeInputRole.Tab;
                    default:
                        return TopiaForgeInputRole.Key;
                }
            }
        }

        private sealed class Surface : ITopiaForgeDismissable
        {
            private static int created;
            private readonly int number = ++created;

            public Surface(TopiaForgeLayerBand band)
            {
                Band = band;
            }

            public TopiaForgeLayerBand Band { get; }

            public int Dismissals { get; private set; }

            public bool Throws { get; set; }

            public void Dismiss()
            {
                Dismissals++;
                if (Throws)
                {
                    throw new InvalidOperationException("The surface failed to close.");
                }
            }

            public override string ToString() => Band + " surface " + number;
        }

        private sealed class Root
        {
            public bool Live { get; set; } = true;
        }
    }
}
