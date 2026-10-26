using System;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        /// <summary>The real voice question, gate and navigation driver: Enter and controller A decline only the consent.</summary>
        private static void VoiceConsentSubmitPressesNothingBeneathIt()
        {
            foreach (var controller in new[] { false, true })
            {
                var input = new Rig();
                var overlay = new Surface(TopiaForgeLayerBand.Window);
                var overlayRoot = new Root();
                input.Order.Push(overlay, TopiaForgeInputClaim.Navigation, overlayRoot);
                var voice = new VoiceRig();
                voice.Start();
                voice.Presenter.OnShown();
                voice.Presenter.SetVoiceOn(true);
                var question = voice.Presenter.ConsentQuestion!;
                var consent = new Surface(TopiaForgeLayerBand.Modal);
                var consentRoot = new Root();
                input.Order.Push(consent, TopiaForgeInputClaim.Navigation, consentRoot);
                question.Shown(new OwnedVoiceDialog(input, consent));
                input.Selection = consentRoot; // MultiplayerVoiceView focuses Not now; the source pin checks that wiring.
                var driver = new TopiaForgeNavigationDriver();
                VoiceNavigation(input, driver);
                Assert(!(controller ? input.Button(South, true) : input.Key(Enter, true)), "the consent submit never reaches START DEMO");
                Assert(VoiceNavigation(input, driver).Submit, "the kit still submits the selected Not now button");
                question.Answer(false);
                voice.Frame();
                Assert(question.IsAnswered && !voice.Presenter.VoiceOn && voice.Microphone.Opens == 0,
                    "Not now keeps voice off and the microphone closed");
                AssertSame(input.Order.Top(), overlay, "declining closes only the consent");
                input.Selection = overlayRoot;
                Assert(!VoiceNavigation(input, driver).Submit, "the same frame's submit presses no overlay control beneath the consent");

                // Close the overlay while the submit is still held: neither the game nor a late-sampled mod gets a press.
                input.Order.Remove(overlay);
                var action = new ActionSampler(controller ? PadSouth : KeyReturn);
                for (var frame = 0; frame < 5; frame++)
                {
                    Assert(!(controller ? input.Button(South, true) : input.Key(Enter, true)), "a held consent submit stays out of the game after closing");
                    var kind = controller ? TopiaForgeInputDevice.Gamepad : TopiaForgeInputDevice.Keyboard;
                    var control = controller ? South : Enter;
                    Assert(!action.Frame(1f, default, input.Gate.HeldOn(kind, control)), "nor does it become a mod action");
                    input.EndFrame();
                }

                if (controller) input.Button(South, false); else input.Key(Enter, false);
                action.Frame(0f, default);
                Assert(controller ? input.Button(South, true) : input.Key(Enter, true), "the next fresh submit reaches the native menu");
                Assert(action.Frame(1f, default), "and a mod action can read that fresh press");
                voice.Presenter.Stop();
                voice.Runtime.Dispose();
            }
        }

        /// <summary>The voice presenter captures the gate's press; neither the game nor a mod sees it, even after closing.</summary>
        private static void VoiceCaptureKeepsItsKeyUntilRelease()
        {
            foreach (var legacy in new[] { false, true })
            {
                var input = new Rig();
                var capture = new OwnedVoiceCapture(input);
                var keys = new OwnedVoiceInput(input, legacy);
                var voice = new VoiceRig(input: keys, escape: capture);
                voice.Channel.Stored = VoiceSwitch.Agreed();
                voice.Snapshot = VoiceRig.Session("s1", VoiceRig.Player("p2", "Sam", "key-sam"));
                voice.Start();
                var overlay = new Surface(TopiaForgeLayerBand.Window);
                input.Order.Push(overlay, TopiaForgeInputClaim.Navigation, new Root());
                voice.Presenter.OnShown();
                voice.Presenter.CaptureBinding(0);
                voice.Frame();
                AssertSame(input.Order.FocusRoot(), null, "a waiting voice capture prevents navigation beneath it");
                Assert(!keys.Press(K, KeyK), "K captured from VOICE reaches no game action");
                voice.Frame();
                Assert(voice.Presenter.CapturingSlot == -1 && voice.Presenter.BindingText(0) == "K" && !input.Order.Contains(capture),
                    "the captured key binds and releases the keyboard claim with either input backend");
                voice.Presenter.OnHidden();
                input.Order.Remove(overlay);
                var counter = new ActionSampler(KeyK);
                for (var frame = 0; frame < 5; frame++)
                {
                    Assert(!input.Key(K, true), "the captured K stays out of the game after VOICE closes");
                    Assert(!counter.Frame(1f, default, input.Gate.HeldOn(TopiaForgeInputDevice.Keyboard, K)),
                        "the captured K fires no mod action even if the action first samples after capture ended");
                    input.EndFrame();
                    voice.Frame();
                    Assert(!voice.Voice.Transmitting, "the binding press never transmits");
                }

                keys.Release();
                counter.Frame(0f, default);
                input.EndFrame();
                Assert(keys.Press(K, KeyK) && counter.Frame(1f, default), "after release, a fresh K belongs to gameplay and mods again");
                voice.Presenter.Stop();
                voice.Runtime.Dispose();
            }
        }

        private static void VoiceCaptureEscapeLeavesTheOverlayAndGameAlone()
        {
            foreach (var voiceFirst in new[] { false, true })
            {
                var input = new Rig();
                var capture = new OwnedVoiceCapture(input);
                var keys = new OwnedVoiceInput(input, legacy: false);
                var voice = new VoiceRig(input: keys, escape: capture);
                voice.Start();
                var overlay = new Surface(TopiaForgeLayerBand.Window);
                input.Order.Push(overlay, TopiaForgeInputClaim.Navigation, new Root());
                voice.Presenter.OnShown();
                voice.Presenter.CaptureBinding(1);
                voice.Frame();
                Assert(!keys.Press(Escape, KeyEscape), "capture Escape never reaches the native pause action");
                if (voiceFirst) voice.Frame();
                AssertSame(input.EndFrame(), capture, "the real dismiss order gives Escape to the capture alone");
                if (!voiceFirst) voice.Frame();
                Assert(voice.Presenter.CapturingSlot == -1 && voice.Presenter.BindingText(1) == "Mouse Forward" && overlay.Dismissals == 0,
                    "either frame order keeps the binding and the overlay open");
                for (var frame = 0; frame < 5; frame++)
                {
                    Assert(!input.Key(Escape, true), "holding capture Escape never opens native pause");
                    AssertSame(input.EndFrame(), null, "nor does it dismiss the overlay");
                }

                keys.Release();
                input.EndFrame();
                Assert(!keys.Press(Escape, KeyEscape), "the next Escape is the overlay's, still not the game's");
                AssertSame(input.EndFrame(), overlay, "that fresh Escape closes the overlay");
                voice.Presenter.Stop();
                voice.Runtime.Dispose();
            }
        }

        private static void TypingDuringVoiceCaptureReachesNeitherGameNorBinding()
        {
            var input = new Rig();
            var keys = new OwnedVoiceInput(input, legacy: false);
            var voice = new VoiceRig(input: keys, escape: new OwnedVoiceCapture(input));
            voice.Start();
            voice.Presenter.OnShown();
            voice.Presenter.CaptureBinding(0);
            voice.Frame();
            input.TextFieldHasKeyboard = true;
            foreach (var (key, binding) in new[] { (W, KeyW), (V, InputBinding.Key("V")), (K, KeyK), (Space, KeySpace) })
            {
                Assert(!keys.Press(key, binding), "typing while VOICE waits reaches no game action");
                voice.Frame();
                Assert(voice.Presenter.CapturingSlot == 0 && voice.Presenter.BindingNote == VoiceText.CaptureNote,
                    "a focused text field keeps its input out of the voice binding");
                Assert(!new ActionSampler(binding).Frame(1f, new UiInputFocus(input.Claims, true, false, false),
                    input.Gate.HeldOn(TopiaForgeInputDevice.Keyboard, key)), "typed input fires no mod action");
                keys.Release();
                input.EndFrame();
            }

            input.TextFieldHasKeyboard = false;
            Assert(!keys.Press(K, KeyK), "a fresh K still belongs to the waiting capture");
            voice.Frame();
            AssertEqual(voice.Presenter.BindingText(0), "K", "capture resumes after text focus ends");
            voice.Presenter.Stop();
            voice.Runtime.Dispose();
        }

        private static TopiaForgeNavigationStep VoiceNavigation(Rig input, TopiaForgeNavigationDriver driver)
        {
            var root = input.Order.FocusRoot();
            return driver.Tick(root != null, ReferenceEquals(input.Selection, root), input.TextFieldHasKeyboard,
                0f, 0f, input.Gate.SubmitPressedIn(input.Frame), FrameTime, ModuleDelay, ModuleRate, root);
        }

        // Unity adapter contracts over the real ownership core. VoiceChatTests pins the production claim, band and read.
        private sealed class OwnedVoiceCapture : IVoiceEscape, ITopiaForgeDismissable
        {
            private readonly Rig input;
            private Action? cancel;
            public OwnedVoiceCapture(Rig input) { this.input = input; }
            public TopiaForgeLayerBand Band => TopiaForgeLayerBand.Modal;
            public void Hold(Action onEscape) { cancel = onEscape; input.Order.Push(this, TopiaForgeInputClaim.Keyboard, null); }
            public void Release() { cancel = null; input.Order.Remove(this); }
            public void Dismiss() { var callback = cancel; Release(); callback?.Invoke(); }
        }

        private sealed class OwnedVoiceInput : IVoiceInput
        {
            private readonly Rig input;
            private readonly bool legacy;
            private InputBinding binding;
            private int key;
            private bool held;
            private int taken = -1;
            public OwnedVoiceInput(Rig input, bool legacy) { this.input = input; this.legacy = legacy; }
            public bool IsTyping => input.TextFieldHasKeyboard;
            public bool Press(int number, InputBinding control) { key = number; binding = control; held = true; return input.Key(key, true); }
            public void Release() { held = false; input.Key(key, false); }
            public bool IsHeld(InputBinding control) => VoicePushToTalk.Same(control, binding) && (legacy ? held : input.Seen(KeyboardId, key) > 0f);
            public bool TryTakePressed(out InputBinding pressed)
            {
                pressed = binding;
                if (taken == input.Frame || !input.Gate.KeyPressedIn(key, input.Frame)) return false;
                taken = input.Frame;
                return true;
            }
        }

        private sealed class OwnedVoiceDialog : Sessions.IPromptDialog
        {
            private readonly Rig input;
            private readonly Surface surface;
            public OwnedVoiceDialog(Rig input, Surface surface) { this.input = input; this.surface = surface; }
            public void Close() => input.Order.Remove(surface);
        }
    }
}
