using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// Whether voice chat is on, and the consent behind it: the version of the approved voice consent the player agreed
    /// to (0 for never) and their switch. These are the launcher's preferences, kept in its store, projected to this game
    /// in manager staging and changed in game through its journal, so the player's choice has one writable copy wherever
    /// they make it (docs/internal/MultiplayerLaunchContract.md, section 7). The switch counts only with consent to the
    /// current wording, <see cref="MultiplayerPrivacyText.VoiceConsentVersion"/>, which the launcher counts the same way.
    /// </summary>
    internal readonly struct VoiceSwitch : IEquatable<VoiceSwitch>
    {
        internal static readonly VoiceSwitch Off = new VoiceSwitch(0, false);

        internal VoiceSwitch(int consent, bool enabled)
        {
            Consent = consent < 0 ? 0 : consent;
            Enabled = enabled;
        }

        /// <summary>Gets the consent version the player agreed to; 0 when never or withdrawn.</summary>
        internal int Consent { get; }

        /// <summary>Gets the player's switch, as stored; see <see cref="IsOn"/>.</summary>
        internal bool Enabled { get; }

        /// <summary>Gets whether the player agreed to the wording shown now; a newer wording asks again.</summary>
        internal bool HasConsent => Consent >= MultiplayerPrivacyText.VoiceConsentVersion;

        /// <summary>Gets whether voice chat is on: switched on, with consent to the current wording.</summary>
        internal bool IsOn => Enabled && HasConsent;

        /// <summary>The switch after the player agreed to the current wording and turned voice chat on.</summary>
        internal static VoiceSwitch Agreed() => new VoiceSwitch(MultiplayerPrivacyText.VoiceConsentVersion, true);

        /// <summary>The switch turned <paramref name="on"/> or off, keeping the consent; on only counts with consent.</summary>
        internal VoiceSwitch WithEnabled(bool on) => new VoiceSwitch(Consent, on && HasConsent);

        /// <summary>The switch after the player withdrew consent: no consent, and off.</summary>
        internal static VoiceSwitch Withdrawn() => Off;

        public bool Equals(VoiceSwitch other) => Consent == other.Consent && Enabled == other.Enabled;

        public override bool Equals(object? obj) => obj is VoiceSwitch other && Equals(other);

        public override int GetHashCode() => (Consent * 397) ^ (Enabled ? 1 : 0);

        /// <summary>
        /// The switch a game reads at start: the launcher's projection, or off without one, with every voice change of
        /// <paramref name="journal"/> the projection does not count yet applied in order, as the launcher will apply them. A
        /// projection counts a journal of its own epoch up to its position; a journal under any other epoch started over and
        /// applies whole. A switch that is on without consent reads as off.
        /// </summary>
        internal static VoiceSwitch From(MultiplayerPreferencesProjection? projection, MultiplayerPreferencesJournal? journal)
        {
            var value = projection == null ? Off : new VoiceSwitch(projection.VoiceConsent, projection.VoiceEnabled);
            if (journal != null)
            {
                var applied = projection != null && string.Equals(projection.JournalEpoch, journal.Epoch, StringComparison.Ordinal) ? projection.JournalApplied : 0;
                foreach (var change in journal.Changes)
                {
                    if (change.Sequence <= applied || change.Kind != "voice") continue;
                    value = new VoiceSwitch(change.NewVoiceConsent, change.NewVoiceEnabled);
                }
            }

            return value.Enabled && !value.HasConsent ? new VoiceSwitch(value.Consent, false) : value;
        }
    }

    /// <summary>
    /// The launcher's channel for the voice switch: its projection read, and the journal written, in manager staging. Every
    /// member touches the disk and a refused write waits for about 310 ms, so it runs on the background queue only.
    /// </summary>
    internal interface IVoiceLauncherChannel
    {
        /// <summary>Reads the switch as the game starts with it: <see cref="VoiceSwitch.From"/> over what is on disk.</summary>
        VoiceSwitch Read();

        /// <summary>Records the player's switch for the launcher to take in.</summary>
        void Record(VoiceSwitch value);
    }

    /// <summary>The voice switch over the launcher's channels in this game's manager staging.</summary>
    internal sealed class StagingVoiceChannel : IVoiceLauncherChannel
    {
        private readonly LaunchStagingStore store;

        internal StagingVoiceChannel(LaunchStagingStore store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public VoiceSwitch Read() => VoiceSwitch.From(store.ReadPreferences(), store.ReadPreferencesJournal());

        public void Record(VoiceSwitch value) =>
            store.RecordPreferenceChange(sequence => MultiplayerPreferenceChange.Voice(sequence, value.Consent, value.Enabled && value.HasConsent));
    }

    /// <summary>
    /// The voice switch and the voice settings as the VOICE page uses them: the current values on the main thread, read once
    /// and written after each change through the background queue, so no file work ever holds a frame. Writes coalesce, the
    /// newest value of each written once the one in flight finishes. A switch that could not be recorded is tried again
    /// every few seconds until it is, so a withdrawn consent always reaches the launcher; the page says so meanwhile. Settings that could not be read last for this session only, so a file this game cannot read is never
    /// overwritten. Main thread only.
    /// </summary>
    internal sealed class VoicePreferences
    {
        internal const string SettingsReadFailed = "Voice settings could not be read, so changes last for this session only. Check that TopiaForge can read and write its data folder.";
        internal const string SettingsSaveFailed = "Voice settings could not be saved; this game tries again with your next change. Check that TopiaForge can write its data folder.";
        internal const string SwitchRecordFailed = "Your voice chat choice could not be saved for the TopiaForge launcher. This game keeps trying; until it can, the launcher may still show your earlier choice.";

        /// <summary>How long a switch that could not be recorded waits before it is tried again, in seconds.</summary>
        internal const float RetrySeconds = 5f;

        private readonly MultiplayerBackgroundQueue queue;
        private readonly IVoiceSettingsStore store;
        private readonly IVoiceLauncherChannel? channel;
        private VoiceSettings settings = VoiceSettings.Default;
        private VoiceSwitch voiceSwitch = VoiceSwitch.Off;
        private VoiceSwitch? pendingSwitch;
        private bool loadingSettings;
        private bool loadingSwitch;
        private bool saving;
        private bool dirty;
        private bool readOnly;
        private bool recording;
        private float sinceSwitchFailure;

        /// <param name="channel">The launcher's channel; null keeps the switch for this session only, as in tests without one.</param>
        internal VoicePreferences(MultiplayerBackgroundQueue queue, IVoiceSettingsStore store, IVoiceLauncherChannel? channel)
        {
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.channel = channel;
        }

        internal VoiceSettings Settings => settings;

        internal VoiceSwitch Switch => voiceSwitch;

        /// <summary>Gets whether both the settings and the switch have been read.</summary>
        internal bool IsLoaded { get; private set; }

        /// <summary>Gets whether a change is still being written.</summary>
        internal bool IsSaving => saving || dirty || recording || pendingSwitch.HasValue;

        /// <summary>Gets whether the last switch could not be recorded for the launcher and waits to be tried again.</summary>
        internal bool SwitchUnsaved { get; private set; }

        /// <summary>Gets the last failure to read or write, for a toast; the page takes and clears it.</summary>
        internal string Failure { get; private set; } = string.Empty;

        /// <summary>Starts reading both, once; <paramref name="loaded"/> runs on the main thread once both have arrived.</summary>
        internal void Load(Action? loaded = null)
        {
            if (IsLoaded || loadingSettings || loadingSwitch) return;
            loadingSettings = true;
            loadingSwitch = true;
            VoiceSettings? read = null;
            queue.Enqueue(() => read = store.Load(), failure =>
            {
                loadingSettings = false;
                if (failure != null || read == null)
                {
                    readOnly = true;
                    Failure = SettingsReadFailed;
                }
                else
                {
                    settings = read;
                }

                Arrived(loaded);
            });

            var source = channel;
            if (source == null)
            {
                loadingSwitch = false;
                Arrived(loaded);
                return;
            }

            var stored = VoiceSwitch.Off;
            queue.Enqueue(() => stored = source.Read(), failure =>
            {
                // A switch that cannot be read is off, so the player is asked again: the safe direction.
                loadingSwitch = false;
                voiceSwitch = failure == null ? stored : VoiceSwitch.Off;
                Arrived(loaded);
            });
        }

        /// <summary>Replaces the settings and writes them; only once loaded.</summary>
        internal void Update(VoiceSettings next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (!IsLoaded) throw new InvalidOperationException("The voice settings have not loaded yet.");
            if (ReferenceEquals(next, settings)) return;
            settings = next;
            if (!readOnly) Save();
        }

        /// <summary>Changes the switch at once and records it for the launcher; only once loaded.</summary>
        internal void Update(VoiceSwitch next)
        {
            if (!IsLoaded) throw new InvalidOperationException("The voice settings have not loaded yet.");
            if (next.Equals(voiceSwitch) && !SwitchUnsaved) return;
            voiceSwitch = next;
            pendingSwitch = next;
            Record();
        }

        /// <summary>Main thread, every frame: tries a switch that could not be recorded again, every few seconds.</summary>
        internal void Tick(float deltaTime)
        {
            if (!SwitchUnsaved || recording) return;
            sinceSwitchFailure += float.IsNaN(deltaTime) || deltaTime < 0f ? 0f : deltaTime;
            if (sinceSwitchFailure < RetrySeconds) return;
            pendingSwitch = voiceSwitch;
            Record();
        }

        /// <summary>Takes the last failure, so it is shown once.</summary>
        internal string TakeFailure()
        {
            var failure = Failure;
            Failure = string.Empty;
            return failure;
        }

        private void Arrived(Action? loaded)
        {
            if (loadingSettings || loadingSwitch) return;
            IsLoaded = true;
            loaded?.Invoke();
        }

        private void Save()
        {
            if (saving)
            {
                dirty = true;
                return;
            }

            saving = true;
            var snapshot = settings;
            queue.Enqueue(() => store.Save(snapshot), failure =>
            {
                saving = false;
                if (failure != null) Failure = SettingsSaveFailed;
                if (!dirty) return;
                dirty = false;
                Save();
            });
        }

        private void Record()
        {
            var source = channel;
            if (source == null)
            {
                pendingSwitch = null;
                SwitchUnsaved = false;
                return;
            }

            if (recording || !pendingSwitch.HasValue) return;
            var value = pendingSwitch.Value;
            pendingSwitch = null;
            recording = true;
            queue.Enqueue(() => source.Record(value), failure =>
            {
                recording = false;
                if (failure != null)
                {
                    if (!SwitchUnsaved) Failure = SwitchRecordFailed;
                    SwitchUnsaved = true;
                    sinceSwitchFailure = 0f;
                }
                else if (!pendingSwitch.HasValue)
                {
                    SwitchUnsaved = false;
                }

                Record();
            });
        }
    }
}
