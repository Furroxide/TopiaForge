using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// VOICE's kit view: voice chat's switch, its transmit state as text and glyph, and the consent behind it; how the player
    /// talks (the mode, push-to-talk's two controls, the activation level over the microphone's meter); the microphone and
    /// its test; the volumes; whom the player hears. #275's line shows above everything while the session offers no voice
    /// chat. The first-use consent opens as a modal in its approved wording, with "Not now" as the default. Long content
    /// scrolls. Kit only, so the UI Gallery shows it too.
    /// </summary>
    internal sealed class MultiplayerVoiceView : IMultiplayerSubView
    {
        /// <summary>What VOICE says when voice chat could not start in this game.</summary>
        internal const string Unavailable = "Voice chat could not start in this game. Restart the game to try again.";

        private readonly UiHost host;
        private readonly IMultiplayerScreens screens;
        private readonly IVoiceScreen? voice;
        private readonly TopiaForgeLabel session;
        private readonly TopiaForgeLabel? settingsNote;
        private readonly TopiaForgeToggle? enabled;
        private readonly TopiaForgeTalkIndicator? indicator;
        private readonly TopiaForgeLabel? status;
        private readonly TopiaForgeLabel? consent;
        private readonly TopiaForgeButton? withdraw;
        private readonly TopiaForgeDropdown? mode;
        private readonly TopiaForgeButton[] bindings = new TopiaForgeButton[2];
        private readonly TopiaForgeButton? reset;
        private readonly TopiaForgeLabel? bindingNote;
        private readonly TopiaForgeSlider? threshold;
        private readonly TopiaForgeLevelMeter? meter;
        private readonly TopiaForgeDropdown? devices;
        private readonly TopiaForgeLabel? deviceNote;
        private readonly TopiaForgeSlider? micVolume;
        private readonly TopiaForgeButton? test;
        private readonly TopiaForgeLabel? testNote;
        private readonly TopiaForgeSlider? voiceVolume;
        private readonly TopiaForgeToggle? hearNobody;
        private readonly MultiplayerVoicePlayersView? players;
        private IReadOnlyList<string>? shownDevices;
        private bool hidden;

        /// <param name="host">The kit host, for the consent modal.</param>
        /// <param name="parent">Where the view builds.</param>
        /// <param name="screens">The multiplayer screens, for #275's line.</param>
        /// <param name="voice">The voice screen; null when voice chat could not start, which the view then says.</param>
        internal MultiplayerVoiceView(UiHost host, TopiaForgeContainer parent, IMultiplayerScreens screens, IVoiceScreen? voice)
        {
            this.host = host;
            this.screens = screens;
            this.voice = voice;
            parent.SectionHeader("VOICE");
            session = MultiplayerViewKit.Note(parent);
            if (voice == null)
            {
                parent.Label(Unavailable, TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Warning).PlainText();
                Tick();
                return;
            }

            voice.OnShown();
            var scroll = parent.Scroll(TopiaForgeGap.Sm);
            scroll.Flex(1f, 1f);
            var form = scroll.Content;
            settingsNote = MultiplayerViewKit.Note(form);
            var state = form.Row(TopiaForgeGap.Md);
            state.FixedHeight(TopiaForgeTokens.ControlHeight);
            enabled = state.Toggle("Voice chat", voice.VoiceOn, on =>
            {
                voice.SetVoiceOn(on);
                enabled?.SetValue(voice.VoiceOn);
            });
            indicator = state.TalkIndicator();
            status = state.Label(voice.StatusText, TopiaForgeTextStyle.Body).PlainText().Ellipsis();
            status.Flex(1f, 0f);
            var agreed = form.Row(TopiaForgeGap.Md);
            consent = agreed.Label(voice.ConsentText, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).PlainText();
            consent.Flex(1f, 0f);
            withdraw = agreed.Button("WITHDRAW CONSENT", voice.WithdrawConsent, TopiaForgeButtonStyle.Outline);

            form.SectionHeader("HOW YOU TALK");
            mode = form.Dropdown(voice.ModeOptions, voice.Mode, voice.SetMode);
            var keys = form.Row(TopiaForgeGap.Sm);
            keys.FixedHeight(TopiaForgeTokens.ControlHeight);
            keys.Label("Push-to-talk", TopiaForgeTextStyle.Label).Tone(TopiaForgeTone.Muted).NoWrap();
            bindings[0] = keys.Button(voice.BindingText(0), () => voice.CaptureBinding(0), TopiaForgeButtonStyle.Outline);
            bindings[1] = keys.Button(voice.BindingText(1), () => voice.CaptureBinding(1), TopiaForgeButtonStyle.Outline);
            keys.Spacer().Flex(1f, 0f);
            reset = keys.Button("RESET", voice.ResetBindings, TopiaForgeButtonStyle.Ghost);
            bindingNote = MultiplayerViewKit.Note(form);
            threshold = form.Slider("Activation level", voice.QuietestThresholdDb, 0f, voice.ThresholdDb, voice.SetThresholdDb);
            meter = form.LevelMeter().Range(voice.QuietestThresholdDb, 0f);

            form.SectionHeader("MICROPHONE");
            var device = form.Row(TopiaForgeGap.Sm);
            device.FixedHeight(TopiaForgeTokens.ControlHeight);
            shownDevices = voice.DeviceOptions;
            devices = device.Dropdown(shownDevices, voice.Device, voice.SetDevice);
            devices.Flex(1f, 0f);
            device.Button("REFRESH", voice.RefreshDevices, TopiaForgeButtonStyle.Outline);
            deviceNote = MultiplayerViewKit.Note(form);
            micVolume = form.Slider("Microphone volume", 0f, 200f, voice.MicVolumePercent, voice.SetMicVolumePercent);
            var testing = form.Row(TopiaForgeGap.Md);
            testing.FixedHeight(TopiaForgeTokens.ControlHeight);
            test = testing.Button(TestLabel(voice.Testing), voice.ToggleTest, TopiaForgeButtonStyle.Outline);
            testNote = testing.Label(voice.TestNote, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).PlainText().Ellipsis();
            testNote.Flex(1f, 0f);

            form.SectionHeader("LISTENING");
            voiceVolume = form.Slider("Voice volume", 0f, 100f, voice.VoiceVolumePercent, voice.SetVoiceVolumePercent);
            hearNobody = form.Toggle("Hear nobody", voice.HearNobody, on =>
            {
                voice.SetHearNobody(on);
                hearNobody?.SetValue(voice.HearNobody);
            });
            var proximity = form.Row(TopiaForgeGap.Md);
            proximity.Toggle("Proximity voice", false, _ => { }).SetEnabled(false);
            MultiplayerViewKit.Note(proximity, voice.ProximityNote);

            form.SectionHeader("PLAYERS");
            players = new MultiplayerVoicePlayersView(form, voice);
            Tick();
        }

        public void Tick()
        {
            var note = screens.VoiceNote;
            session.SetText(note);
            session.SetShown(note.Length != 0);
            var screen = voice;
            if (screen == null || hidden) return;
            settingsNote!.SetText(screen.SettingsNote);
            settingsNote.SetShown(screen.SettingsNote.Length != 0);
            enabled!.SetValue(screen.VoiceOn);
            enabled.SetEnabled(screen.CanSwitch);
            indicator!.SetState(TalkState(screen.Indicator));
            status!.SetText(screen.StatusText);
            status.SetTone(MultiplayerViewKit.Tone(screen.StatusTone));
            consent!.SetText(screen.ConsentText);
            withdraw!.SetVisible(screen.CanWithdrawConsent);
            withdraw.SetEnabled(screen.CanWithdrawConsent);
            var loaded = screen.IsLoaded;
            mode!.SetValue(screen.Mode);
            mode.SetEnabled(loaded);
            for (var slot = 0; slot < bindings.Length; slot++)
            {
                bindings[slot].SetText(screen.BindingText(slot));
                bindings[slot].SetEnabled(loaded);
            }

            reset!.SetEnabled(loaded);
            bindingNote!.SetText(screen.BindingNote);
            bindingNote.SetTone(MultiplayerViewKit.Tone(screen.BindingNoteTone));
            threshold!.SetShown(screen.ShowsThreshold);
            threshold.SetValue(screen.ThresholdDb);
            threshold.SetEnabled(loaded);
            meter!.SetThreshold(screen.ShowsThreshold ? screen.ThresholdDb : float.NaN);
            meter.SetLevel(TopiaForgeLevelMath.ToLinear(screen.LevelDb));
            if (!ReferenceEquals(screen.DeviceOptions, shownDevices))
            {
                shownDevices = screen.DeviceOptions;
                devices!.SetOptions(shownDevices, screen.Device);
            }

            devices!.SetValue(screen.Device);
            devices.SetEnabled(loaded);
            deviceNote!.SetText(screen.DeviceNote);
            deviceNote.SetShown(screen.DeviceNote.Length != 0);
            micVolume!.SetValue(screen.MicVolumePercent);
            micVolume.SetEnabled(loaded);
            test!.SetText(TestLabel(screen.Testing));
            test.SetEnabled(screen.Testing || screen.CanTest);
            testNote!.SetText(screen.TestNote);
            voiceVolume!.SetValue(screen.VoiceVolumePercent);
            voiceVolume.SetEnabled(loaded);
            hearNobody!.SetValue(screen.HearNobody);
            hearNobody.SetEnabled(loaded);
            players!.Tick();
            var question = screen.ConsentQuestion;
            if (question != null && !question.IsAnswered && !question.IsShown) AskConsent(question);
        }

        /// <summary>Says VOICE stopped showing: the screen stops a microphone test and a binding capture.</summary>
        internal void Hide()
        {
            if (hidden) return;
            hidden = true;
            voice?.OnHidden();
        }

        /// <summary>
        /// The approved consent, word for word, as a modal that survives scene loads. Turning voice chat on is the choice that
        /// opens the microphone, so it is never the default: "Not now" is the primary button and takes the focus, and closing
        /// the modal any other way answers no.
        /// </summary>
        private void AskConsent(VoiceConsentQuestion question)
        {
            var modal = host.Modal.Custom(question.Title, TopiaForgeScheme.Paper, 560f, persistent: true);
            modal.Content.Label(question.Body, TopiaForgeTextStyle.Body).PlainText();
            var row = modal.Content.Row(TopiaForgeGap.Sm);
            row.Spacer().Flex(1f, 0f);
            row.Button(question.AcceptLabel, () => question.Answer(true), TopiaForgeButtonStyle.Outline);
            var decline = row.Button(question.DeclineLabel, () => question.Answer(false));
            modal.Closed += () => question.Answer(false);
            modal.Show();
            decline.Focus();
            question.Shown(new ConsentDialog(modal));
        }

        private static string TestLabel(bool testing) => testing ? "STOP TEST" : "TEST MICROPHONE";

        private static TopiaForgeTalkState TalkState(VoiceIndicator value)
        {
            switch (value)
            {
                case VoiceIndicator.Talking:
                    return TopiaForgeTalkState.Talking;
                case VoiceIndicator.Muted:
                    return TopiaForgeTalkState.Muted;
                case VoiceIndicator.MutedByHost:
                    return TopiaForgeTalkState.MutedByHost;
                case VoiceIndicator.MicBusy:
                    return TopiaForgeTalkState.MicBusy;
                case VoiceIndicator.NoMic:
                    return TopiaForgeTalkState.NoMic;
                default:
                    return TopiaForgeTalkState.Idle;
            }
        }

        /// <summary>The consent's kit modal; closing one that already closed does nothing.</summary>
        private sealed class ConsentDialog : IPromptDialog
        {
            private readonly TopiaForgeModalInstance modal;

            internal ConsentDialog(TopiaForgeModalInstance modal)
            {
                this.modal = modal;
            }

            public void Close() => modal.Close();
        }
    }
}
