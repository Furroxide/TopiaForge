using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// The voice settings only this game keeps, in manager data: how the player transmits, the push-to-talk binding, the
    /// microphone, the two volumes, the voice-activation threshold and the wish to hear nobody. Whether voice chat is on,
    /// and the player's consent, are the launcher's preferences and live there alone (<see cref="VoiceSwitch"/>). Immutable;
    /// every change returns a new value, and an invalid value read from disk takes its default.
    /// </summary>
    internal sealed class VoiceSettings
    {
        internal const int SchemaVersion = 1;

        /// <summary>The voice-activation threshold a new player starts with, the voice service's own default.</summary>
        internal const float DefaultThresholdDb = -40f;

        /// <summary>The quietest threshold the settings page offers; the service takes down to -96 dBFS.</summary>
        internal const float QuietestThresholdDb = -60f;

        internal const float MaximumMicVolume = 2f;

        /// <summary>The longest microphone name kept; a longer one is forgotten and the system's default used.</summary>
        internal const int MaximumDeviceLength = 128;

        internal static readonly VoiceSettings Default = new VoiceSettings(
            InternalVoiceTransmitMode.PushToTalk, new InputBinding?[] { VoicePushToTalk.DefaultKey, VoicePushToTalk.DefaultMouseButton },
            null, 1f, 1f, DefaultThresholdDb, false);

        private readonly InputBinding?[] pushToTalk;

        private VoiceSettings(InternalVoiceTransmitMode mode, IReadOnlyList<InputBinding?> pushToTalk, string? device, float micVolume, float voiceVolume,
            float thresholdDb, bool hearNobody)
        {
            Mode = mode == InternalVoiceTransmitMode.VoiceActivation ? mode : InternalVoiceTransmitMode.PushToTalk;
            this.pushToTalk = new InputBinding?[VoicePushToTalk.SlotCount];
            for (var slot = 0; slot < this.pushToTalk.Length; slot++)
            {
                var binding = slot < pushToTalk.Count ? pushToTalk[slot] : null;
                this.pushToTalk[slot] = binding.HasValue && VoiceControls.TryFind(binding.Value, out _) ? binding : null;
            }

            Device = IsDeviceName(device) ? device : null;
            MicVolume = Finite(micVolume, 1f, 0f, MaximumMicVolume);
            VoiceVolume = Finite(voiceVolume, 1f, 0f, 1f);
            ThresholdDb = Finite(thresholdDb, DefaultThresholdDb, VoiceChatRuntime.SilenceDb, 0f);
            HearNobody = hearNobody;
        }

        internal InternalVoiceTransmitMode Mode { get; }

        /// <summary>Gets the push-to-talk slots the player chose; null for a slot left without a control.</summary>
        internal IReadOnlyList<InputBinding?> PushToTalk => pushToTalk;

        /// <summary>Gets the microphone the player chose; null for the system's default.</summary>
        internal string? Device { get; }

        /// <summary>Gets the microphone's gain, 0 to 2: 1 leaves it as recorded.</summary>
        internal float MicVolume { get; }

        /// <summary>Gets other players' volume, 0 to 1.</summary>
        internal float VoiceVolume { get; }

        internal float ThresholdDb { get; }

        internal bool HearNobody { get; }

        internal VoiceSettings WithMode(InternalVoiceTransmitMode mode) => Copy(mode: mode);

        internal VoiceSettings WithPushToTalk(int slot, InputBinding? binding)
        {
            if (slot < 0 || slot >= VoicePushToTalk.SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
            var next = (InputBinding?[])pushToTalk.Clone();
            next[slot] = binding;
            return Copy(pushToTalk: next);
        }

        internal VoiceSettings WithDefaultPushToTalk() => Copy(pushToTalk: new InputBinding?[] { VoicePushToTalk.DefaultKey, VoicePushToTalk.DefaultMouseButton });

        internal VoiceSettings WithDevice(string? device) => new VoiceSettings(Mode, pushToTalk, device, MicVolume, VoiceVolume, ThresholdDb, HearNobody);

        internal VoiceSettings WithMicVolume(float value) => Copy(micVolume: value);

        internal VoiceSettings WithVoiceVolume(float value) => Copy(voiceVolume: value);

        internal VoiceSettings WithThresholdDb(float value) => Copy(thresholdDb: value);

        internal VoiceSettings WithHearNobody(bool value) => Copy(hearNobody: value);

        /// <summary>Gets whether <paramref name="value"/> can name a microphone: printable text of 1 to 128 characters.</summary>
        internal static bool IsDeviceName(string? value) => InternalText.IsDisplayText(value, MaximumDeviceLength);

        internal VoiceSettingsDocument ToDocument()
        {
            var slots = new List<string>(pushToTalk.Length);
            foreach (var binding in pushToTalk) slots.Add(binding.HasValue && VoiceControls.TryFind(binding.Value, out var control) ? control.Stored : string.Empty);
            return new VoiceSettingsDocument
            {
                SchemaVersionValue = SchemaVersion,
                Mode = Mode == InternalVoiceTransmitMode.VoiceActivation ? "voice-activation" : "push-to-talk",
                PushToTalk = slots,
                Device = Device,
                MicVolume = MicVolume,
                VoiceVolume = VoiceVolume,
                ThresholdDb = ThresholdDb,
                HearNobody = HearNobody
            };
        }

        /// <summary>
        /// Reads a stored document forgivingly: a missing or invalid member takes its default. A document of another schema
        /// version is refused, so a newer game's settings are never overwritten with an older reading of them.
        /// </summary>
        internal static VoiceSettings FromDocument(VoiceSettingsDocument? document)
        {
            if (document == null) return Default;
            if (document.SchemaVersionValue != 0 && document.SchemaVersionValue != SchemaVersion)
            {
                throw new SerializationException("Unsupported voice settings schemaVersion " + document.SchemaVersionValue + ".");
            }

            var slots = new InputBinding?[VoicePushToTalk.SlotCount];
            for (var slot = 0; slot < slots.Length; slot++)
            {
                var stored = document.PushToTalk != null && slot < document.PushToTalk.Count ? document.PushToTalk[slot] : null;
                slots[slot] = stored == null ? Default.pushToTalk[slot]
                    : stored.Length == 0 ? null
                    : VoiceControls.TryParseStored(stored, out var control) ? control.Binding
                    : Default.pushToTalk[slot];
            }

            return new VoiceSettings(
                string.Equals(document.Mode, "voice-activation", StringComparison.Ordinal) ? InternalVoiceTransmitMode.VoiceActivation : InternalVoiceTransmitMode.PushToTalk,
                slots,
                document.Device,
                document.MicVolume ?? 1f,
                document.VoiceVolume ?? 1f,
                document.ThresholdDb ?? DefaultThresholdDb,
                document.HearNobody);
        }

        private VoiceSettings Copy(
            InternalVoiceTransmitMode? mode = null,
            IReadOnlyList<InputBinding?>? pushToTalk = null,
            float? micVolume = null,
            float? voiceVolume = null,
            float? thresholdDb = null,
            bool? hearNobody = null) =>
            new VoiceSettings(
                mode ?? Mode,
                pushToTalk ?? this.pushToTalk,
                Device,
                micVolume ?? MicVolume,
                voiceVolume ?? VoiceVolume,
                thresholdDb ?? ThresholdDb,
                hearNobody ?? HearNobody);

        private static float Finite(float value, float fallback, float minimum, float maximum) =>
            float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(minimum, Math.Min(maximum, value));
    }

    /// <summary>The stored form of <see cref="VoiceSettings"/>, at <c>data/multiplayer-voice.json</c>. It holds no consent and nothing heard.</summary>
    [DataContract]
    internal sealed class VoiceSettingsDocument
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersionValue { get; set; }

        [DataMember(Name = "mode", EmitDefaultValue = false)]
        public string? Mode { get; set; }

        /// <summary>Gets or sets each push-to-talk slot as <c>key:CapsLock</c> or <c>mouse:Forward</c>; empty for a slot without a control.</summary>
        [DataMember(Name = "pushToTalk", EmitDefaultValue = false)]
        public List<string>? PushToTalk { get; set; }

        [DataMember(Name = "device", EmitDefaultValue = false)]
        public string? Device { get; set; }

        [DataMember(Name = "micVolume", EmitDefaultValue = false)]
        public float? MicVolume { get; set; }

        [DataMember(Name = "voiceVolume", EmitDefaultValue = false)]
        public float? VoiceVolume { get; set; }

        [DataMember(Name = "thresholdDb", EmitDefaultValue = false)]
        public float? ThresholdDb { get; set; }

        [DataMember(Name = "hearNobody")]
        public bool HearNobody { get; set; }
    }

    /// <summary>Reads and writes the voice settings. Its members run off the main thread, on the background queue.</summary>
    internal interface IVoiceSettingsStore
    {
        VoiceSettings Load();

        void Save(VoiceSettings settings);
    }

    /// <summary>The voice settings in manager data, at <c>data/multiplayer-voice.json</c>, written atomically with a backup.</summary>
    internal sealed class FileVoiceSettingsStore : IVoiceSettingsStore
    {
        internal const string FileName = "multiplayer-voice.json";

        private readonly string path;

        internal FileVoiceSettingsStore(string dataDirectory)
        {
            if (string.IsNullOrEmpty(dataDirectory)) throw new ArgumentException("The manager data folder is required.", nameof(dataDirectory));
            path = Path.Combine(dataDirectory, FileName);
        }

        public VoiceSettings Load() => VoiceSettings.FromDocument(JsonUtil.LoadPersistentFile<VoiceSettingsDocument?>(path, null));

        public void Save(VoiceSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            JsonUtil.SaveFile(path, settings.ToDocument());
        }
    }
}
