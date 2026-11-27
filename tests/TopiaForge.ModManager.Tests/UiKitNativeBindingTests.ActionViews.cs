namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitNativeBindingTests
    {
        private const string ActionViewCases = """
            using System;
            using System.Linq;
            using TopiaForge.ModManager;
            using TopiaForge.Mods;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.InputSystem;
            using UnityEngine.InputSystem.LowLevel;

            public static class ActionViewCases
            {
                public static void Run()
                {
                    TracksRealActivityBeforeItIsMasked();
                    ResolvesOnlyTheAuthenticatedOwnersDefaults();
                    PaintsTheMeasuredKeyCapAndInfoCue();
                    Console.WriteLine("K2 native key glyphs passed for raw input activity, scoped binding refresh and key cap paint.");
                }

                private static void TracksRealActivityBeforeItIsMasked()
                {
                    TopiaForgeInputOwnership.ResetViewerForTest();
                    var pad = new Gamepad(13);
                    var keyboard = new Keyboard(14);
                    var mouse = new Mouse(15);
                    var listener = new TopiaForgeInputBindingField(UiInputDevice.KeyboardMouse);
                    TopiaForgeBindingCaptureInput.Begin(listener);
                    var masked = Event(pad.buttonSouth, 1);
                    TopiaForgeInputOwnership.Deliver(masked, pad);
                    Check(masked.Value(pad.buttonSouth) == 0 && Last == UiInputDevice.Gamepad,
                        "The viewer observes the real press before ownership neutralizes it.");
                    TopiaForgeBindingCaptureInput.End(listener);

                    Send(mouse, Event(mouse.delta.x, 3));
                    Check(Last == UiInputDevice.KeyboardMouse, "Relative pointer movement selects keyboard and mouse.");
                    Send(pad, Event(pad.buttonSouth, 1));
                    Send(pad, Event(pad.buttonSouth, 0));
                    Check(Last == UiInputDevice.KeyboardMouse, "A held packet and its release cannot steal the viewer's last device.");
                    Send(pad, Event(pad.buttonSouth, 1));
                    Send(mouse, Event(mouse.position.x, 1000));
                    Check(Last == UiInputDevice.Gamepad, "A new press counts, and an absolute cursor warp does not.");
                    Send(mouse, Event(mouse.scroll.y, 1));
                    Check(Last == UiInputDevice.KeyboardMouse, "A wheel event is real keyboard-and-mouse activity.");
                    Send(pad, Event(pad.leftStick.x, .04f));
                    Send(pad, Event(pad.rightTrigger, .04f));
                    Check(Last == UiInputDevice.KeyboardMouse, "Stick and trigger drift leave the last device alone.");
                    Send(pad, Delta(pad.leftStick.x, .65f));
                    Check(Last == UiInputDevice.Gamepad, "A deliberate stick delta selects the gamepad.");
                    Send(keyboard, Event(keyboard.digit1, 1));
                    Send(pad, Delta(pad.leftStick.y, .01f));
                    Send(pad, Delta(pad.leftStick.x, .65f));
                    Check(Last == UiInputDevice.KeyboardMouse, "An unrelated delta retains the raw held axis and does not replay the movement.");
                    Send(pad, Delta(pad.rightTrigger, .8f));
                    Check(Last == UiInputDevice.Gamepad, "An independent trigger movement selects the gamepad.");
                    Send(mouse, Event(mouse.leftButton, 1));
                    Send(pad, new InputEventPtr(typeof(DeviceResetEvent), (pad.buttonSouth, 1)));
                    Check(Last == UiInputDevice.KeyboardMouse, "A device reset forgets raw presses without changing the displayed device.");
                    Send(pad, Event(pad.buttonSouth, 1));
                    Check(Last == UiInputDevice.Gamepad, "The first new press after reset is recognized.");

                    var held = Event(pad.rightShoulder, 1);
                    for (var i = 0; i < 4096; i++) Send(pad, held);
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 4096; i++) Send(pad, held);
                    Check(GC.GetAllocatedBytesForCurrentThread() == before, "Settled production input events allocate zero bytes.");

                    TopiaForgeInputOwnership.ResetViewerForTest();
                    mouse.delta.x.Broken = true;
                    Send(mouse, Event(mouse.delta.x, 1));
                    TopiaForgeInputOwnership.SawBack = false;
                    Send(keyboard, Event(keyboard.escape, 1));
                    Check(TopiaForgeInputOwnership.SawBack, "An activity reader failure leaves normal Back ownership working.");
                    TopiaForgeInputOwnership.ResetViewerForTest();
                }

                private static void ResolvesOnlyTheAuthenticatedOwnersDefaults()
                {
                    var registry = new UnityInputRegistry();
                    var outside = registry.Register("unrelated.mod", "confirm", InputBinding.Key("U"));
                    var hints = new[] { new UiInputBindingHint(InputBindingKind.Key, "E"),
                        new UiInputBindingHint(InputBindingKind.GamepadButton, "South") };
                    var context = new UiSurfaceInputContext(registry, "unrelated.mod");
                    var owner = context.ForRemote(new UiWireEnvelope("signed.mod", new byte[32], 2, null, new byte[32]));
                    var absent = owner.BindAction("confirm", hints);
                    Check(absent.Face.Label == "E" && absent.Face.Label != outside.Bindings[0].Control,
                        "An authenticated missing action uses the named owner's hints and never another package's registration.");
                    var bare = context.ForRemote(null).BindAction("confirm", hints);
                    Check(bare.Face.Label == "Unbound" && bare.Face.Cue == UiGlyph.Info,
                        "A legacy route cannot give its node defaults display authority.");
                    var local = new UiSurfaceInputContext(registry, "signed.mod").BindAction("confirm", hints);
                    Check(local.Face.Label == "Unbound", "An authored local cap without registration cannot borrow wire hints.");
                    try
                    {
                        new UiSurfaceInputContext(registry, "signed.mod").Validate(new UiKeyGlyph("Confirm", "wrong-name"));
                        throw new InvalidOperationException("The exact-owner unknown action was admitted.");
                    }
                    catch (ArgumentException error)
                    {
                        Check(error.Message.Contains("wrong-name", StringComparison.Ordinal), "The rejected local action names its node.");
                    }

                    var viewer = registry.Register("signed.mod", "confirm", InputBinding.Key("Q"),
                        InputBinding.GamepadButton(InputGamepadButton.LeftShoulder));
                    Check(absent.Refresh() && absent.Face.Label == "Q" && local.Refresh() && local.Face.Label == "Q",
                        "A late viewer registration overrides previously displayed hints.");
                    var pad = new Gamepad(16);
                    Send(pad, Event(pad.buttonWest, 1));
                    Check(absent.Refresh() && absent.Face.Label == "LB" && absent.Face.SpokenControl == "left bumper",
                        "A real viewing-device change updates the same binding to its spoken gamepad cap.");
                    viewer.Snapshot(InputBinding.Key("F"), InputBinding.Key("G"));
                    Check(absent.Refresh() && absent.Face.Label == "F", "A new live binding falls back to its first other-device control.");
                    viewer.Snapshot();
                    Check(absent.Refresh() && absent.Face.Cue == UiGlyph.Info && absent.Face.Label == "Unbound",
                        "An empty live snapshot still wins over authenticated hints.");
                    registry.Remove("signed.mod", "confirm");
                    Check(absent.Refresh() && absent.Face.Label == "A", "Disposal restores only the authenticated definition hint.");
                    for (var i = 0; i < 4096; i++) _ = absent.Refresh();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 4096; i++)
                        if (absent.Refresh()) throw new InvalidOperationException("A settled binding dirtied its native layout.");
                    Check(GC.GetAllocatedBytesForCurrentThread() == before, "Settled scoped binding refresh allocates zero bytes.");
                }

                private static void PaintsTheMeasuredKeyCapAndInfoCue()
                {
                    var host = new TopiaForgeContainer();
                    host.Host.EffectiveUiScale = .75f;
                    var view = new TopiaForgeActionKeyCap(host);
                    var face = UiActionBindingPresentation.Resolve(
                        new[] { InputBinding.GamepadButton(InputGamepadButton.LeftShoulder) },
                        Array.Empty<UiInputBindingHint>(), UiInputDevice.Gamepad);
                    var size = view.Measure(face, 100).Size;
                    var bounds = new UiLayoutRect(3, 5, size.Width, size.Height);
                    view.Apply(face, bounds, UiPaintRole.Surface);
                    var root = host.Children["Key cap"];
                    var label = root.LabelView!;
                    var ink = UiThemePackParser.Default.SelectScheme(false, false).GetPair(UiPaintRole.Sunken);
                    var fill = root.Children["Key cap fill"].Go.GetComponent<TopiaForgeRoundedImage>();
                    var border = root.Children["Key cap border"].Go.GetComponent<TopiaForgeRoundedImage>();
                    var glyph = root.Children["Key cap cue"].Go.GetComponent<UnityEngine.UI.Image>();
                    Check(view.SemanticLabel == "key left bumper" && label.Text.Text == "LB" && label.Style == TopiaForgeTextStyle.Label &&
                        label.IsPlain && !label.Text.raycastTarget && !fill.raycastTarget && !border.raycastTarget,
                        "The native key cap uses plain Label type and never participates in hits.");
                    Check(Math.Abs(view.Bounds.Width - size.Width) < .001 && Math.Abs(view.Bounds.Height - size.Height) < .001,
                        "A tightly allocated native cap keeps its measured line count and dimensions.");
                    Check(view.Bounds.Height * .75 >= 24 && fill.color.a == 1 && label.Text.color.r == ink.On.R / 255f &&
                        !glyph.enabled, "The native cap uses an opaque semantic fill under a measured readable label.");
                    var measurements = label.Text.Measures;
                    for (var i = 0; i < 4096; i++) view.Apply(face, bounds, UiPaintRole.Surface);
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 4096; i++) view.Apply(face, bounds, UiPaintRole.Surface);
                    Check(GC.GetAllocatedBytesForCurrentThread() == before && label.Text.Measures == measurements,
                        "Settled key cap apply allocates nothing and performs no text measurement.");
                    var unbound = UiActionBindingPresentation.Resolve(null, Array.Empty<UiInputBindingHint>(), UiInputDevice.Gamepad);
                    host.ResolvedTheme = new TopiaForgeResolvedTheme(highContrast: true);
                    var unboundSize = view.Measure(unbound, 160).Size;
                    view.Apply(unbound, new UiLayoutRect(3, 5, unboundSize.Width, unboundSize.Height), UiPaintRole.Raised, UiTone.Muted);
                    Check(view.SemanticLabel == "Unbound" && label.Text.Text == "Unbound" && glyph.enabled &&
                        glyph.sprite?.Glyph == UiGlyph.Info && border.Stroke >= 3,
                        "The unbound cap uses the Info glyph and a high-contrast component boundary.");
                }

                private static UiInputDevice Last => TopiaForgeInputOwnership.LastViewerDevice;
                private static InputEventPtr Event(InputControl control, float value) => new(typeof(StateEvent), (control, value));
                private static InputEventPtr Delta(InputControl control, float value) => new(typeof(DeltaStateEvent), (control, value));
                private static void Send(InputDevice device, InputEventPtr input) => TopiaForgeInputOwnership.Deliver(input, device);
                private static void Check(bool pass, string why) { if (!pass) throw new InvalidOperationException(why); }
            }
            """;
    }
}
