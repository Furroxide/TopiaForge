using System.Collections.Generic;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>The talk indicator's states, as the kit's talk glyph draws them; every state differs in shape, not only tone.</summary>
    internal enum VoiceIndicator
    {
        Idle = 0,
        Talking = 1,

        /// <summary>Voice chat is off, needs consent, or the session offers none.</summary>
        Muted = 2,

        MutedByHost = 3,

        /// <summary>The game's own recording or a robot conversation holds the microphone.</summary>
        MicBusy = 4,

        NoMic = 5
    }

    /// <summary>One player VOICE lists: whom this player can hear, and whether they talk. Every text is display-ready.</summary>
    internal sealed class VoicePlayerRow
    {
        internal VoicePlayerRow(string key, string name, bool isHost, bool canMute)
        {
            Key = key ?? string.Empty;
            Name = name ?? string.Empty;
            IsHost = isHost;
            CanMute = canMute;
        }

        /// <summary>Gets the participant's key, for actions; it is never shown.</summary>
        internal string Key { get; }

        /// <summary>Gets the player's name as they chose it, redacted and bounded, shown only as plain text.</summary>
        internal string Name { get; }

        internal bool IsHost { get; }

        /// <summary>Gets whether this player can be muted from here.</summary>
        internal bool CanMute { get; }
    }

    /// <summary>
    /// VOICE: voice chat's switch and the consent behind it, the transmit state, how the player talks (push-to-talk or voice
    /// activation, the binding and the activation level with its meter), the microphone and a test of it, the volumes, and
    /// whom the player hears. Presenters implement it over the voice runtime; the UI Gallery over fake data. Getters
    /// allocate nothing once their value settled, so a view reads them every frame through the kit's dirty-checked
    /// setters; a list is replaced, never changed in place. Main thread only.
    /// </summary>
    internal interface IVoiceScreen
    {
        /// <summary>Gets whether the voice settings and the launcher's voice choice have been read; until then nothing can change.</summary>
        bool IsLoaded { get; }

        /// <summary>Gets a note while the settings load, or after they could not be read or saved; empty otherwise.</summary>
        string SettingsNote { get; }

        /// <summary>Gets whether voice chat is on: switched on, with the player's consent to the current wording.</summary>
        bool VoiceOn { get; }

        /// <summary>Gets whether the switch can change now: loaded, and voice chat available in this game.</summary>
        bool CanSwitch { get; }

        /// <summary>Turns voice chat on, asking the approved consent first when the player has not agreed to it, or off.</summary>
        void SetVoiceOn(bool on);

        /// <summary>Gets the consent question waiting for the player's answer, or null. The view shows it as a modal.</summary>
        VoiceConsentQuestion? ConsentQuestion { get; }

        /// <summary>Gets what the player agreed to, such as "You agreed to voice chat."; never the consent text itself.</summary>
        string ConsentText { get; }

        bool CanWithdrawConsent { get; }

        /// <summary>Withdraws the player's consent: voice chat turns off at once, and turning it on asks again.</summary>
        void WithdrawConsent();

        /// <summary>Gets the transmit state in words, such as "Hold Caps Lock or Mouse Forward to talk."</summary>
        string StatusText { get; }

        ScreenTone StatusTone { get; }

        VoiceIndicator Indicator { get; }

        IReadOnlyList<string> ModeOptions { get; }

        /// <summary>Gets the selected mode: 0 for push-to-talk, 1 for voice activation.</summary>
        int Mode { get; }

        void SetMode(int index);

        /// <summary>Gets push-to-talk slot <paramref name="slot"/>'s control as the player reads it, or what to do while it captures.</summary>
        string BindingText(int slot);

        /// <summary>Gets the slot waiting for a key or mouse button; -1 for none.</summary>
        int CapturingSlot { get; }

        /// <summary>Waits for the next key or mouse button for <paramref name="slot"/>; asked again, or Esc, cancels.</summary>
        void CaptureBinding(int slot);

        void ResetBindings();

        /// <summary>Gets why the last control was refused, or that a slot fell back because the game uses its control; empty otherwise.</summary>
        string BindingNote { get; }

        ScreenTone BindingNoteTone { get; }

        /// <summary>Gets whether the activation level applies: in voice activation.</summary>
        bool ShowsThreshold { get; }

        float ThresholdDb { get; }

        float QuietestThresholdDb { get; }

        void SetThresholdDb(float value);

        /// <summary>Gets the microphone's level in dBFS for the meter: silence while the microphone is closed.</summary>
        float LevelDb { get; }

        /// <summary>Gets the microphones to choose from, the system's default first.</summary>
        IReadOnlyList<string> DeviceOptions { get; }

        int Device { get; }

        void SetDevice(int index);

        /// <summary>Lists the microphones again, as after one was plugged in.</summary>
        void RefreshDevices();

        /// <summary>Gets a note on the chosen microphone, such as that it is not connected; empty otherwise.</summary>
        string DeviceNote { get; }

        /// <summary>Gets the microphone's volume in percent, 0 to 200.</summary>
        float MicVolumePercent { get; }

        void SetMicVolumePercent(float value);

        /// <summary>Gets other players' volume in percent, 0 to 100.</summary>
        float VoiceVolumePercent { get; }

        void SetVoiceVolumePercent(float value);

        /// <summary>Gets whether the microphone test runs: it opens the microphone, moves the meter and sends nothing.</summary>
        bool Testing { get; }

        /// <summary>Gets whether the test can run: voice chat is on.</summary>
        bool CanTest { get; }

        void ToggleTest();

        /// <summary>Gets what the test does, or why it cannot run.</summary>
        string TestNote { get; }

        /// <summary>Gets why proximity voice is not offered yet.</summary>
        string ProximityNote { get; }

        /// <summary>Gets the wish to hear nobody, which follows the player across sessions.</summary>
        bool HearNobody { get; }

        void SetHearNobody(bool value);

        /// <summary>Gets whether players are listed: once the session's host has listed them.</summary>
        bool PlayersVisible { get; }

        /// <summary>Gets why no players are listed, while <see cref="PlayersVisible"/> is false.</summary>
        string PlayersNote { get; }

        IReadOnlyList<VoicePlayerRow> Players { get; }

        /// <summary>Gets whether this player muted the player in the row with <paramref name="key"/>.</summary>
        bool IsMuted(string key);

        /// <summary>Gets whether the player in the row with <paramref name="key"/> was heard in the last moment.</summary>
        bool IsSpeaking(string key);

        /// <summary>Mutes or unmutes the player in the row with <paramref name="key"/>, checked against the current session first.</summary>
        void ToggleMute(string key);

        /// <summary>Takes the next action result to show as a toast.</summary>
        bool TryTakeNotice(out ScreenNotice notice);

        /// <summary>Says VOICE became visible: the microphones and the game's bindings are read again.</summary>
        void OnShown();

        /// <summary>Says VOICE stopped showing: a microphone test stops, and a binding capture is cancelled.</summary>
        void OnHidden();
    }
}
