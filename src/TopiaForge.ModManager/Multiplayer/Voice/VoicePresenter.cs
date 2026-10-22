using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// VOICE's presenter over the voice runtime: the switch and the consent, the settings and their storage, the push-to-talk
    /// binding and its census, the microphones, and whom the player hears. It ticks every frame, overlay open or not, before
    /// the runtime, so a choice reaches the microphone in the frame it is made; a settled frame allocates nothing. The
    /// consent is asked in its approved wording before voice chat first turns on, and the switch is the launcher's
    /// preference, recorded through its journal. Main thread only.
    /// </summary>
    internal sealed partial class VoicePresenter : IVoiceScreen
    {
        internal const int MaximumNotices = 16;

        private readonly VoiceChatRuntime runtime;
        private readonly VoicePreferences preferences;
        private readonly MultiplayerBackgroundQueue queue;
        private readonly Func<IInternalMultiplayerControl?> control;
        private readonly Func<MultiplayerSessionSnapshot?> snapshot;
        private readonly Queue<ScreenNotice> notices = new Queue<ScreenNotice>();
        private VoiceConsentQuestion? consentQuestion;
        private VoiceTalkStatus shownStatus = (VoiceTalkStatus)(-1);
        private string statusText = VoiceText.Unavailable;
        private ScreenTone statusTone = ScreenTone.Muted;
        private VoiceIndicator indicator = VoiceIndicator.Muted;
        private string readyText = VoiceText.NoPushToTalk;
        private int readyVersion = -1;
        private bool shown;

        /// <param name="runtime">The voice runtime the choices reach.</param>
        /// <param name="preferences">Where the switch and the settings are read and written, through <paramref name="queue"/>.</param>
        /// <param name="queue">The background queue the preferences run on; this presenter pumps it.</param>
        /// <param name="microphone">The microphone, which lists the devices to choose from.</param>
        /// <param name="input">The keyboard and mouse, for rebinding push-to-talk.</param>
        /// <param name="nativeBindings">The game's own bindings, for the census.</param>
        /// <param name="control">The multiplayer control, for this game's role.</param>
        /// <param name="snapshot">The session as mods see it, for the players.</param>
        internal VoicePresenter(
            VoiceChatRuntime runtime,
            VoicePreferences preferences,
            MultiplayerBackgroundQueue queue,
            IVoiceMicrophone microphone,
            IVoiceInput input,
            IVoiceNativeBindings nativeBindings,
            Func<IInternalMultiplayerControl?> control,
            Func<MultiplayerSessionSnapshot?> snapshot)
        {
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.nativeBindings = nativeBindings ?? throw new ArgumentNullException(nameof(nativeBindings));
            this.control = control ?? throw new ArgumentNullException(nameof(control));
            this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        /// <summary>Gets the runtime, for tests.</summary>
        internal VoiceChatRuntime Runtime => runtime;

        internal VoicePreferences Preferences => preferences;

        public bool IsLoaded => preferences.IsLoaded;

        public string SettingsNote =>
            !preferences.IsLoaded ? VoiceText.Loading
            : preferences.SwitchUnsaved ? VoiceText.SwitchUnsaved
            : preferences.IsSaving || queue.IsBusy ? VoiceText.Saving
            : string.Empty;

        public bool VoiceOn => preferences.Switch.IsOn;

        public bool CanSwitch => preferences.IsLoaded && runtime.Status != VoiceTalkStatus.Unavailable;

        public VoiceConsentQuestion? ConsentQuestion => consentQuestion;

        public string ConsentText => preferences.Switch.HasConsent ? VoiceText.ConsentGiven : VoiceText.ConsentAsked;

        public bool CanWithdrawConsent => preferences.IsLoaded && preferences.Switch.HasConsent;

        public string StatusText
        {
            get
            {
                UpdateStatus(preferences.Settings);
                return statusText;
            }
        }

        public ScreenTone StatusTone
        {
            get
            {
                UpdateStatus(preferences.Settings);
                return statusTone;
            }
        }

        public VoiceIndicator Indicator
        {
            get
            {
                UpdateStatus(preferences.Settings);
                return indicator;
            }
        }

        public IReadOnlyList<string> ModeOptions => VoiceText.Modes;

        public int Mode => preferences.Settings.Mode == InternalVoiceTransmitMode.VoiceActivation ? 1 : 0;

        public bool ShowsThreshold => preferences.Settings.Mode == InternalVoiceTransmitMode.VoiceActivation;

        public float ThresholdDb => preferences.Settings.ThresholdDb;

        public float QuietestThresholdDb => VoiceSettings.QuietestThresholdDb;

        public float LevelDb => runtime.LevelDb;

        public float MicVolumePercent => preferences.Settings.MicVolume * 100f;

        public float VoiceVolumePercent => preferences.Settings.VoiceVolume * 100f;

        public bool Testing => runtime.Testing;

        public bool CanTest => preferences.IsLoaded && preferences.Switch.IsOn && runtime.Status != VoiceTalkStatus.Unavailable;

        public string TestNote => !preferences.Switch.IsOn ? VoiceText.TestOff : runtime.Testing ? VoiceText.TestRunning : VoiceText.TestReady;

        public string ProximityNote => VoiceText.Proximity;

        public bool HearNobody => preferences.Settings.HearNobody;

        /// <summary>Starts reading the switch and the settings, and takes the census, so voice chat works before VOICE ever opens.</summary>
        internal void Start()
        {
            preferences.Load();
            RefreshCensus();
        }

        public void SetVoiceOn(bool on)
        {
            if (!CanSwitch) return;
            var current = preferences.Switch;
            if (!on)
            {
                if (!current.Enabled) return;
                preferences.Update(current.WithEnabled(false));
                runtime.Testing = false;
                Notify(VoiceText.TurnedOff, ScreenTone.Neutral);
                return;
            }

            if (current.IsOn) return;
            if (current.HasConsent)
            {
                preferences.Update(current.WithEnabled(true));
                Notify(VoiceText.TurnedOn, ScreenTone.Success);
                return;
            }

            // Voice chat never turns on without the player's consent to the current wording.
            if (consentQuestion != null && !consentQuestion.IsAnswered) return;
            consentQuestion = new VoiceConsentQuestion(Answered);
        }

        public void WithdrawConsent()
        {
            if (!CanWithdrawConsent) return;
            runtime.Testing = false;
            preferences.Update(VoiceSwitch.Withdrawn());
            Notify(VoiceText.Withdrawn, ScreenTone.Neutral);
        }

        public void SetMode(int index)
        {
            if (!preferences.IsLoaded || index < 0 || index >= VoiceText.Modes.Length) return;
            var mode = index == 1 ? InternalVoiceTransmitMode.VoiceActivation : InternalVoiceTransmitMode.PushToTalk;
            if (mode == preferences.Settings.Mode) return;
            preferences.Update(preferences.Settings.WithMode(mode));
        }

        public void SetThresholdDb(float value)
        {
            if (!preferences.IsLoaded || float.IsNaN(value)) return;
            var next = Math.Max(VoiceSettings.QuietestThresholdDb, Math.Min(0f, value));
            if (next.Equals(preferences.Settings.ThresholdDb)) return;
            preferences.Update(preferences.Settings.WithThresholdDb(next));
        }

        public void SetMicVolumePercent(float value)
        {
            if (!preferences.IsLoaded || float.IsNaN(value)) return;
            var next = Math.Max(0f, Math.Min(VoiceSettings.MaximumMicVolume, value / 100f));
            if (next.Equals(preferences.Settings.MicVolume)) return;
            preferences.Update(preferences.Settings.WithMicVolume(next));
        }

        public void SetVoiceVolumePercent(float value)
        {
            if (!preferences.IsLoaded || float.IsNaN(value)) return;
            var next = Math.Max(0f, Math.Min(1f, value / 100f));
            if (next.Equals(preferences.Settings.VoiceVolume)) return;
            preferences.Update(preferences.Settings.WithVoiceVolume(next));
        }

        public void ToggleTest()
        {
            if (runtime.Testing)
            {
                runtime.Testing = false;
                return;
            }

            if (!CanTest) return;
            runtime.RetryMicrophone();
            runtime.Testing = true;
        }

        public void SetHearNobody(bool value)
        {
            if (!preferences.IsLoaded || value == preferences.Settings.HearNobody) return;
            preferences.Update(preferences.Settings.WithHearNobody(value));
            Notify(value ? VoiceText.HearNobodyOn : VoiceText.HearNobodyOff, ScreenTone.Neutral);
        }

        public bool TryTakeNotice(out ScreenNotice notice)
        {
            if (notices.Count == 0)
            {
                notice = default;
                return false;
            }

            notice = notices.Dequeue();
            return true;
        }

        public void OnShown()
        {
            shown = true;
            playersStale = true;
            RefreshDevices();
            RefreshCensus();
        }

        public void OnHidden()
        {
            shown = false;
            runtime.Testing = false;
            CancelCapture();
        }

        /// <summary>Main thread, every frame, before the runtime's tick: the player's choices reach the runtime.</summary>
        internal void Tick(float deltaTime)
        {
            queue.Pump();
            preferences.Tick(deltaTime);
            var failure = preferences.TakeFailure();
            if (failure.Length > 0) Notify(failure, ScreenTone.Warning);

            // Until the choices have been read, voice chat stays off: the safe default needs nothing from disk.
            var voiceSwitch = preferences.IsLoaded ? preferences.Switch : VoiceSwitch.Off;
            var settings = preferences.Settings;
            runtime.ConsentCurrent = voiceSwitch.HasConsent;
            runtime.VoiceEnabled = voiceSwitch.Enabled;
            runtime.Mode = settings.Mode;
            runtime.ThresholdDb = settings.ThresholdDb;
            runtime.MicGain = settings.MicVolume;
            runtime.Device = settings.Device;
            runtime.Playback.Volume = settings.VoiceVolume;
            runtime.Mutes.HearNobody = settings.HearNobody;
            if (!voiceSwitch.IsOn) runtime.Testing = false;
            TickBindings(settings, deltaTime);
            if (consentQuestion != null)
            {
                if (consentQuestion.IsAnswered) consentQuestion = null;
                else if (runtime.Status == VoiceTalkStatus.Unavailable) consentQuestion.Withdraw();
            }

            UpdateStatus(settings);
            TickPlayers();
        }

        /// <summary>Withdraws a question still waiting, as the game closes: it closes its dialog and changes nothing.</summary>
        internal void Stop()
        {
            consentQuestion?.Withdraw();
            consentQuestion = null;
            runtime.Testing = false;
            CancelCapture();
        }

        private void Answered(bool accepted)
        {
            if (!accepted)
            {
                Notify(VoiceText.StaysOff, ScreenTone.Neutral);
                return;
            }

            // Voice chat may have gone away while the question waited; the answer then changes nothing.
            if (!CanSwitch) return;
            preferences.Update(VoiceSwitch.Agreed());
            Notify(VoiceText.TurnedOn, ScreenTone.Success);
        }

        /// <summary>Queues a toast; past the bound the oldest goes, so nothing unshown piles up.</summary>
        private void Notify(string text, ScreenTone tone)
        {
            if (string.IsNullOrEmpty(text)) return;
            while (notices.Count >= MaximumNotices) notices.Dequeue();
            notices.Enqueue(new ScreenNotice(text, tone));
        }
    }
}
