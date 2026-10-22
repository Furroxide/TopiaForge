using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>
    /// VOICE with made-up data: the consent, every transmit state, push-to-talk capture, the meter, the microphones and the
    /// players. Nothing here opens a microphone or reads a key: a capture completes by itself after a moment, and the meter
    /// follows a made-up voice while the test runs or the player talks.
    /// </summary>
    internal sealed class FakeVoice : IVoiceScreen
    {
        private const float CaptureSeconds = 1.5f;

        private static readonly string[] Modes = { "Push-to-talk", "Voice activation" };
        private static readonly string[] Captured = { "G", "Mouse Back" };

        private readonly GallerySessionScreens screens;
        private readonly string[] bindings = { "Caps Lock", "Mouse Forward" };
        private readonly HashSet<string> muted = new HashSet<string>(StringComparer.Ordinal);
        private readonly IReadOnlyList<VoicePlayerRow> players = new[]
        {
            new VoicePlayerRow("p1", "Robin", isHost: true, canMute: true),
            new VoicePlayerRow("p2", "Alex", isHost: false, canMute: true),
            new VoicePlayerRow("p3", "Kai", isHost: false, canMute: false)
        };

        private bool consent;
        private bool on;
        private float clock;
        private float capturing;

        internal FakeVoice(GallerySessionScreens screens)
        {
            this.screens = screens;
        }

        /// <summary>Gets or sets the state the page shows while voice chat is on.</summary>
        internal VoiceIndicator State { get; set; } = VoiceIndicator.Idle;

        public bool IsLoaded => true;

        public string SettingsNote => string.Empty;

        public bool VoiceOn => on && consent;

        public bool CanSwitch => true;

        public VoiceConsentQuestion? ConsentQuestion { get; private set; }

        public string ConsentText => consent
            ? "You agreed to voice chat. Withdraw your consent to turn it off and be asked again."
            : "TopiaForge asks for your consent before voice chat first uses your microphone.";

        public bool CanWithdrawConsent => consent;

        public string StatusText => !consent ? "Voice chat is off. Turning it on asks before it uses your microphone."
            : !on ? "Voice chat is off."
            : Testing ? "Testing your microphone. Nothing is sent."
            : State == VoiceIndicator.Talking ? "Talking."
            : State == VoiceIndicator.MutedByHost ? "The host muted you."
            : State == VoiceIndicator.MicBusy ? "The game or a robot conversation is using your microphone. Voice chat waits until it is done."
            : State == VoiceIndicator.NoMic ? "No microphone found. Connect one, then press REFRESH."
            : Mode == 1 ? "Listening: you are heard when you speak above the activation level."
            : "Hold " + bindings[0] + " or " + bindings[1] + " to talk.";

        public ScreenTone StatusTone => !VoiceOn ? ScreenTone.Muted
            : State == VoiceIndicator.MutedByHost || State == VoiceIndicator.MicBusy || State == VoiceIndicator.NoMic ? ScreenTone.Warning
            : ScreenTone.Success;

        public VoiceIndicator Indicator => !VoiceOn ? VoiceIndicator.Muted : State;

        public IReadOnlyList<string> ModeOptions => Modes;

        public int Mode { get; private set; }

        public int CapturingSlot { get; private set; } = -1;

        public string BindingNote => CapturingSlot >= 0 ? "Press the key or mouse button to hold for push-to-talk. Esc cancels." : string.Empty;

        public ScreenTone BindingNoteTone => ScreenTone.Neutral;

        public bool ShowsThreshold => Mode == 1;

        public float ThresholdDb { get; private set; } = -40f;

        public float QuietestThresholdDb => -60f;

        public float LevelDb => VoiceOn && (Testing || State == VoiceIndicator.Talking || Mode == 1)
            ? -30f + (12f * (float)Math.Sin(clock * 7f)) + (6f * (float)Math.Sin(clock * 2.3f))
            : -96f;

        public IReadOnlyList<string> DeviceOptions { get; } = new[] { "System default microphone", "USB Microphone", "Headset (not connected)" };

        public int Device { get; private set; }

        public string DeviceNote => Device == 2 ? "That microphone is not connected, so voice chat uses the system default microphone until it is." : string.Empty;

        public float MicVolumePercent { get; private set; } = 100f;

        public float VoiceVolumePercent { get; private set; } = 100f;

        public bool Testing { get; private set; }

        public bool CanTest => VoiceOn;

        public string TestNote => !VoiceOn ? "Turn voice chat on to test your microphone."
            : Testing ? "Speak: the bar shows what your microphone hears. Nothing is sent or recorded."
            : "The test opens your microphone and shows its level. Nothing is sent or recorded.";

        public string ProximityNote => "Voices play at full volume for now. Proximity voice arrives with player avatars.";

        public bool HearNobody { get; private set; }

        public bool PlayersVisible => screens.HostFake.Running && screens.SessionFake.Ready;

        public string PlayersNote => screens.HostFake.Running ? "Players show here once the host lists them." : "Host a game or join one to hear other players.";

        public IReadOnlyList<VoicePlayerRow> Players => players;

        public void SetVoiceOn(bool value)
        {
            if (!value)
            {
                on = false;
                Testing = false;
                return;
            }

            if (consent)
            {
                on = true;
                return;
            }

            if (ConsentQuestion == null || ConsentQuestion.IsAnswered) ConsentQuestion = new VoiceConsentQuestion(Answered);
        }

        public void WithdrawConsent()
        {
            consent = false;
            on = false;
            Testing = false;
            screens.Notify("You withdrew your consent. Voice chat is off, and turning it on asks again.");
        }

        public void SetMode(int index) => Mode = index == 1 ? 1 : 0;

        public string BindingText(int slot) => slot == CapturingSlot ? "Press a key or button..." : slot >= 0 && slot < bindings.Length ? bindings[slot] : string.Empty;

        public void CaptureBinding(int slot)
        {
            CapturingSlot = slot == CapturingSlot ? -1 : slot;
            capturing = 0f;
        }

        public void ResetBindings()
        {
            CapturingSlot = -1;
            bindings[0] = "Caps Lock";
            bindings[1] = "Mouse Forward";
            screens.Notify("Push-to-talk is back to Caps Lock and Mouse Forward.");
        }

        public void SetThresholdDb(float value) => ThresholdDb = Math.Max(-60f, Math.Min(0f, value));

        public void SetDevice(int index) => Device = index;

        public void RefreshDevices() => screens.Notify("Found 2 microphones.");

        public void SetMicVolumePercent(float value) => MicVolumePercent = value;

        public void SetVoiceVolumePercent(float value) => VoiceVolumePercent = value;

        public void ToggleTest() => Testing = !Testing && VoiceOn;

        public void SetHearNobody(bool value) => HearNobody = value;

        public bool IsMuted(string key) => muted.Contains(key);

        public bool IsSpeaking(string key) => !muted.Contains(key) && !HearNobody && key == (((int)clock / 2) % 2 == 0 ? "p1" : "p2");

        public void ToggleMute(string key)
        {
            if (!muted.Remove(key)) muted.Add(key);
        }

        public bool TryTakeNotice(out ScreenNotice notice)
        {
            notice = default;
            return false;
        }

        public void OnShown()
        {
        }

        public void OnHidden()
        {
            Testing = false;
            CapturingSlot = -1;
        }

        internal void Tick(float deltaTime)
        {
            clock += deltaTime;
            if (ConsentQuestion != null && ConsentQuestion.IsAnswered) ConsentQuestion = null;
            if (CapturingSlot < 0) return;
            capturing += deltaTime;
            if (capturing < CaptureSeconds) return;
            bindings[CapturingSlot] = Captured[CapturingSlot];
            screens.Notify("Push-to-talk is now " + Captured[CapturingSlot] + ".", ScreenTone.Success);
            CapturingSlot = -1;
        }

        private void Answered(bool accepted)
        {
            if (!accepted)
            {
                screens.Notify("Voice chat stays off.");
                return;
            }

            consent = true;
            on = true;
            screens.Notify("Voice chat is on.", ScreenTone.Success);
        }
    }
}
