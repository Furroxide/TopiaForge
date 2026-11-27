using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class VoiceChatTests
    {
        private static void PushToTalkNeverTakesAReservedControl()
        {
            foreach (var key in new[] { "V", "B", "Q", "E", "Z" })
            {
                That(VoicePushToTalk.Reserved(InputBinding.Key(key)) == VoiceBindingProblem.GameKey, key + " is one of the game's own keys");
            }

            foreach (var key in new[] { "F5", "F8", "F10", "Escape" })
            {
                That(VoicePushToTalk.Reserved(InputBinding.Key(key)) == VoiceBindingProblem.TopiaForgeKey, key + " is one of TopiaForge's keys");
            }

            That(VoicePushToTalk.Reserved(InputBinding.MouseButton(InputMouseButton.Primary)) == VoiceBindingProblem.GameMouseButton &&
                 VoicePushToTalk.Reserved(InputBinding.MouseButton(InputMouseButton.Secondary)) == VoiceBindingProblem.GameMouseButton,
                "the primary and secondary mouse buttons belong to the game");
            That(VoicePushToTalk.Reserved(InputBinding.GamepadButton(InputGamepadButton.South)) == VoiceBindingProblem.Unknown &&
                 VoicePushToTalk.Reserved(InputBinding.Key("NoSuchKey")) == VoiceBindingProblem.Unknown, "a control push-to-talk does not know is refused");
            foreach (var free in new[] { CapsLock, Forward, InputBinding.Key("BackQuote"), InputBinding.Key("G"), InputBinding.MouseButton(InputMouseButton.Back) })
            {
                That(VoicePushToTalk.Reserved(free) == VoiceBindingProblem.None, free.Control + " is free to take");
            }

            var slots = new InputBinding?[] { CapsLock, Forward };
            That(VoicePushToTalk.Check(InputBinding.Key("W"), 0, NativeInputCensus.Fallback, slots) == VoiceBindingProblem.BoundByGame &&
                 VoicePushToTalk.Check(Forward, 0, NativeInputCensus.Fallback, slots) == VoiceBindingProblem.AlreadyBound &&
                 VoicePushToTalk.Check(Forward, 1, NativeInputCensus.Fallback, slots) == VoiceBindingProblem.None &&
                 VoicePushToTalk.Check(InputBinding.Key("V"), 0, NativeInputCensus.FromPaths(new string[0]), slots) == VoiceBindingProblem.GameKey,
                "the census and the other slot are checked after the reserved controls, which hold whatever the game binds");
            That(VoicePushToTalk.Explain(VoiceBindingProblem.GameKey, InputBinding.Key("V")) == "V is one of the game's own keys. Press another key." &&
                 VoicePushToTalk.Explain(VoiceBindingProblem.BoundByGame, InputBinding.Key("W")) == "The game already uses W. Press another key or button." &&
                 VoicePushToTalk.Explain(VoiceBindingProblem.None, CapsLock).Length == 0, "each refusal says why in the player's words");
            That(VoiceControls.Describe(CapsLock) == "Caps Lock" && VoiceControls.Describe(Forward) == "Mouse Forward" && VoiceControls.Describe(null) == "Unbound",
                "controls read as the player knows them");
        }

        private static void TheCensusReadsTheGamesBindings()
        {
            That(Parsed("<Keyboard>/capsLock") == "key:CapsLock" && Parsed("<Keyboard>{Main}/v") == "key:V" && Parsed("<keyboard>/BACKQUOTE") == "key:BackQuote" &&
                 Parsed("<Keyboard>/shift") == "key:LeftShift key:RightShift" && Parsed("<Keyboard>/enter") == "key:Return" &&
                 Parsed("<Mouse>/forwardButton") == "mouse:Forward" && Parsed("<Pointer>/press") == "mouse:Primary" && Parsed("<Keyboard>/numpad0") == "key:Keypad0",
                "keys and mouse buttons are read from their paths, without case, both sides for a plain modifier");
            foreach (var none in new[] { "<Gamepad>/buttonSouth", "<Keyboard>/#(a)", "*/{Submit}", "<Keyboard>/*", "<Keyboard>", "<Keyboard>/", "", null, "2DVector" })
            {
                That(Parsed(none).Length == 0, "\"" + none + "\" names no key or mouse button");
            }

            var census = NativeInputCensus.FromPaths(new[] { "<Keyboard>/capsLock", "<Mouse>/forwardButton", "<Gamepad>/buttonSouth", null, "<Keyboard>/capsLock" });
            That(census.IsFromGame && census.Count == 2 && census.Uses(CapsLock) && census.Uses(Forward) && !census.Uses(InputBinding.Key("G")),
                "the census holds what the game binds, once each");
            That(!NativeInputCensus.Fallback.IsFromGame && NativeInputCensus.Fallback.Uses(InputBinding.Key("W")) &&
                 NativeInputCensus.Fallback.Uses(InputBinding.MouseButton(InputMouseButton.Primary)) && NativeInputCensus.Fallback.Uses(InputBinding.Key("RightShift")) &&
                 !NativeInputCensus.Fallback.Uses(CapsLock) && !NativeInputCensus.Fallback.Uses(Forward), "the fallback stands in for the game and leaves the defaults free");
            That(census.SameAs(NativeInputCensus.FromPaths(new[] { "<Mouse>/forwardButton", "<Keyboard>/capsLock" })) && !census.SameAs(NativeInputCensus.Fallback),
                "two readings of the same bindings are the same census");
        }

        private static string Parsed(string? path)
        {
            var into = new List<VoiceControl>();
            NativeInputCensus.TryParsePath(path, into);
            return string.Join(" ", into.Select(control => control.Stored));
        }

        private static void BindingsFallBackWhenTheGameUsesThem()
        {
            var into = new InputBinding?[VoicePushToTalk.SlotCount];
            var game = NativeInputCensus.FromPaths(new[] { "<Keyboard>/capsLock", "<Mouse>/forwardButton" });
            var fellBack = VoicePushToTalk.Resolve(new InputBinding?[] { CapsLock, Forward }, game, into);
            That(fellBack == 2 && Same(into[0], InputBinding.Key("BackQuote")) && Same(into[1], InputBinding.MouseButton(InputMouseButton.Back)),
                "a game that binds Caps Lock and Mouse Forward moves push-to-talk to ` and Mouse Back (" + fellBack + ")");
            fellBack = VoicePushToTalk.Resolve(new InputBinding?[] { CapsLock, Forward }, NativeInputCensus.Fallback, into);
            That(fellBack == 0 && Same(into[0], CapsLock) && Same(into[1], Forward), "otherwise the defaults hold");
            fellBack = VoicePushToTalk.Resolve(new InputBinding?[] { InputBinding.Key("G"), InputBinding.Key("G") }, NativeInputCensus.Fallback, into);
            That(fellBack == 1 && Same(into[0], InputBinding.Key("G")) && Same(into[1], CapsLock), "earlier slots win: a later slot that wants the same key falls back");
            fellBack = VoicePushToTalk.Resolve(new InputBinding?[] { InputBinding.Key("V"), null }, NativeInputCensus.Fallback, into);
            That(fellBack == 1 && Same(into[0], CapsLock) && into[1] == null, "a reserved key read from a file falls back, and an empty slot stays empty");
            var keys = new[] { "capsLock", "backquote", "t", "g", "h", "x", "c", "y", "u", "n", "m", "k", "j", "l", "i", "o", "p" };
            var everything = NativeInputCensus.FromPaths(keys.Select(key => "<Keyboard>/" + key));
            fellBack = VoicePushToTalk.Resolve(new InputBinding?[] { CapsLock, Forward }, everything, into);
            That(fellBack == 1 && into[0] == null && Same(into[1], Forward), "with every fallback key taken, the key slot has none");
        }

        private static bool Same(InputBinding? left, InputBinding right) => left.HasValue && VoicePushToTalk.Same(left.Value, right);

        private static void CaptureRefusesReservedAndTakenControls()
        {
            var rig = VoiceRig.On();
            rig.Presenter.OnShown();
            rig.Presenter.CaptureBinding(0);
            That(rig.Presenter.CapturingSlot == 0 && rig.Presenter.BindingText(0) == VoiceText.CapturePrompt && rig.Runtime.PushToTalkSuspended &&
                 rig.Presenter.BindingNote == VoiceText.CaptureNote, "a capture waits for a control, and push-to-talk waits with it");

            // The press that started the capture, Enter on its button, is not taken.
            rig.Input.Press(InputBinding.Key("Return"));
            rig.Frame();
            rig.Input.Release(InputBinding.Key("Return"));
            That(rig.Presenter.CapturingSlot == 0 && rig.Presenter.BindingNote == VoiceText.CaptureNote, "the frame the capture starts takes nothing");
            rig.Input.Hold(Forward);
            rig.Frame(3);
            That(!rig.Microphone.IsOpen && !rig.Voice.Transmitting, "push-to-talk does not talk while it is rebound");
            rig.Input.Release(Forward);

            Refused(rig, InputBinding.Key("V"), VoiceBindingProblem.GameKey);
            Refused(rig, InputBinding.Key("F8"), VoiceBindingProblem.TopiaForgeKey);
            Refused(rig, InputBinding.MouseButton(InputMouseButton.Primary), VoiceBindingProblem.GameMouseButton);
            Refused(rig, InputBinding.Key("W"), VoiceBindingProblem.BoundByGame);
            Refused(rig, Forward, VoiceBindingProblem.AlreadyBound);

            var g = InputBinding.Key("G");
            rig.Input.Press(g);
            rig.Frame();
            That(rig.Presenter.CapturingSlot == -1 && rig.Presenter.BindingText(0) == "G" && rig.Store.Stored != null && Same(rig.Store.Stored.PushToTalk[0], g) &&
                 LastNotice(rig) == VoiceText.BindingNow + "G.", "G binds, is saved, and the player is told");
            rig.Frame(3);
            That(rig.Runtime.PushToTalkSuspended && !rig.Microphone.IsOpen, "push-to-talk waits until the key that bound it is let go");
            rig.Input.Release(g);
            rig.Frame();
            That(!rig.Runtime.PushToTalkSuspended, "and then works");
            rig.Input.Hold(g);
            rig.Frame(2);
            That(rig.Microphone.IsOpen && rig.Voice.Transmitting && rig.Presenter.StatusText == VoiceText.Talking, "G talks now");
            rig.Input.Release(g);
            rig.Input.Hold(CapsLock);
            rig.Frame(2);
            That(!rig.Microphone.IsOpen, "and Caps Lock no longer does");
            rig.Input.Release(CapsLock);
            rig.Frame();
            That(rig.Presenter.StatusText == "Hold G or Mouse Forward to talk.", "the state names the new key");

            // Esc cancels, through the kit's Escape stack (EscapeCancelsTheCaptureAlone); so does asking again, or leaving VOICE.
            rig.Presenter.CaptureBinding(1);
            rig.Frame();
            rig.Escape.Press();
            That(rig.Presenter.CapturingSlot == -1 && rig.Presenter.BindingText(1) == "Mouse Forward" && !rig.Runtime.PushToTalkSuspended, "Esc cancels the capture");
            rig.Presenter.CaptureBinding(1);
            rig.Presenter.CaptureBinding(1);
            That(rig.Presenter.CapturingSlot == -1 && !rig.Runtime.PushToTalkSuspended, "asking again cancels it");
            rig.Presenter.CaptureBinding(0);
            rig.Presenter.OnHidden();
            That(rig.Presenter.CapturingSlot == -1 && !rig.Runtime.PushToTalkSuspended, "and so does leaving VOICE");

            rig.Presenter.ResetBindings();
            rig.Frame();
            That(rig.Presenter.BindingText(0) == "Caps Lock" && Same(rig.Store.Stored!.PushToTalk[0], CapsLock) && LastNotice(rig) == VoiceText.BindingsReset,
                "RESET brings back Caps Lock and Mouse Forward");
        }

        private static void Refused(VoiceRig rig, InputBinding pressed, VoiceBindingProblem problem)
        {
            rig.Input.Press(pressed);
            rig.Frame();
            rig.Input.Release(pressed);
            That(rig.Presenter.CapturingSlot == 0 && rig.Presenter.BindingNote == VoicePushToTalk.Explain(problem, pressed) &&
                 rig.Presenter.BindingNoteTone == ScreenTone.Warning && rig.Presenter.BindingText(0) == VoiceText.CapturePrompt,
                pressed.Control + " is refused as " + problem + ", and the capture waits for another (" + rig.Presenter.BindingNote + ")");
        }

        private static void TheGamesBindingsMoveTheDefaults()
        {
            var rig = new VoiceRig();
            rig.Channel.Stored = VoiceSwitch.Agreed();
            rig.Snapshot = VoiceRig.Session("s1", VoiceRig.Player("p2", "Sam", "key-sam"));
            rig.Bindings.Paths = new List<string> { "<Keyboard>/capsLock", "<Keyboard>/w", "<Gamepad>/buttonSouth" };
            rig.Start();
            That(rig.Presenter.Census.IsFromGame && rig.Presenter.BindingText(0) == "`" && rig.Presenter.BindingText(1) == "Mouse Forward" &&
                 rig.Presenter.BindingNote == "The game uses Caps Lock, so push-to-talk uses ` instead." &&
                 rig.Presenter.BindingNoteTone == ScreenTone.Warning && rig.Presenter.StatusText == "Hold ` or Mouse Forward to talk.",
                "a game that binds Caps Lock moves push-to-talk to `, and the page says so");
            rig.Input.Hold(CapsLock);
            rig.Frame(3);
            That(!rig.Microphone.IsOpen, "the game's own key never talks");
            rig.Input.Release(CapsLock);
            rig.Input.Hold(InputBinding.Key("BackQuote"));
            rig.Frame(2);
            That(rig.Microphone.IsOpen, "` does");
            rig.Input.Release(InputBinding.Key("BackQuote"));

            // The census is read again when a session starts: the game's player may come with it.
            var late = new VoiceRig(InternalSessionRole.Solo);
            late.Channel.Stored = VoiceSwitch.Agreed();
            late.Start();
            That(!late.Presenter.Census.IsFromGame && late.Presenter.BindingText(1) == "Mouse Forward", "before the game's player exists the fallback stands in");
            late.Bindings.Paths = new List<string> { "<Mouse>/forwardButton" };
            late.Control.Role = InternalSessionRole.Client;
            late.Frame();
            That(late.Presenter.Census.IsFromGame && late.Presenter.BindingText(1) == "Mouse Back", "a session that starts reads the game's bindings again");

            // Bindings that cannot be read are tried again every ten seconds.
            var retry = new VoiceRig();
            retry.Start();
            var reads = retry.Bindings.Reads;
            retry.Frame((int)(9f / VoiceRig.FrameSeconds));
            That(retry.Bindings.Reads == reads, "not before ten seconds");
            retry.Frame((int)(1.5f / VoiceRig.FrameSeconds));
            That(retry.Bindings.Reads == reads + 1, "then once more");
        }

        private static void TheMicrophonesToChooseFrom()
        {
            var rig = VoiceRig.On();
            rig.Microphone.Devices.Add("Headset");
            rig.Presenter.OnShown();
            rig.Frame();
            That(rig.Presenter.DeviceOptions.SequenceEqual(new[] { VoiceText.DefaultDevice, "USB Microphone", "Headset" }) && rig.Presenter.Device == 0 &&
                 rig.Presenter.DeviceNote.Length == 0, "the system's default comes first, then each microphone");
            rig.Presenter.SetDevice(2);
            rig.Frame();
            That(rig.Store.Stored!.Device == "Headset" && rig.Presenter.Device == 2, "a chosen microphone is kept");
            rig.Input.Hold(CapsLock);
            rig.Frame(2);
            That(rig.Microphone.OpenedDevice == "Headset", "and recorded from");
            rig.Presenter.SetDevice(1);
            rig.Frame();
            That(rig.Microphone.OpenedDevice == "USB Microphone" && rig.Microphone.Closes == 1 && rig.Microphone.IsOpen,
                "choosing another while talking closes the one open and opens the chosen one at once");
            rig.Input.Release(CapsLock);
            rig.Frame();

            rig.Microphone.Devices.Remove("USB Microphone");
            rig.Presenter.RefreshDevices();
            rig.Frame();
            That(rig.Presenter.DeviceOptions.Last() == "USB Microphone" + VoiceText.NotConnectedSuffix &&
                 rig.Presenter.Device == rig.Presenter.DeviceOptions.Count - 1 && rig.Presenter.DeviceNote == VoiceText.DeviceMissing,
                "an unplugged choice stays listed, marked, and the page says the default records meanwhile");
            rig.Microphone.Devices.Clear();
            rig.Presenter.RefreshDevices();
            rig.Frame();
            That(rig.Presenter.DeviceNote == VoiceText.NoDevices, "with no microphone at all the page says so");
            That(rig.Store.Stored.Device == "USB Microphone", "listing devices never changes the choice");
        }
    }
}
