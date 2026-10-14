using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>What a host opens by default: the in-game "Open to multiplayer" settings and the host dialog's start.</summary>
    public sealed class MultiplayerHostingDefaults
    {
        public MultiplayerHostingDefaults(string name, int maxPlayers, string visibility, int port, bool voice)
        {
            Name = LaunchSessionValues.DisplayText(name, nameof(name), 1, LaunchSessionValues.MaximumServerName);
            MaxPlayers = LaunchSessionValues.Ranged(maxPlayers, nameof(maxPlayers), LaunchSessionValues.MinimumPlayers, LaunchSessionValues.MaximumPlayers);
            Visibility = LaunchSessionValues.Choice(visibility, nameof(visibility), LaunchContractValues.Visibilities);
            Port = LaunchSessionValues.Ranged(port, nameof(port), 0, 65535);
            Voice = voice;
        }

        public string Name { get; }
        public int MaxPlayers { get; }
        public string Visibility { get; }

        /// <summary>Gets the port to host on; zero picks any free port.</summary>
        public int Port { get; }
        public bool Voice { get; }
    }

    /// <summary>
    /// The launcher's multiplayer preferences as the game reads them (<c>multiplayer-preferences-v1.json</c> in manager
    /// staging, launcher to game). The game never edits it: it records changes made in game in a
    /// <see cref="MultiplayerPreferencesJournal"/>, which the launcher applies before writing the next revision.
    /// <see cref="JournalApplied"/> is the last journal sequence applied. Privacy is always relay-only here: direct play is
    /// a choice for one session, never a stored default. The projection holds no address.
    /// </summary>
    public sealed class MultiplayerPreferencesProjection
    {
        public const int SchemaVersion = 1;

        public MultiplayerPreferencesProjection(int revision, int journalApplied, string displayName, int disclosure, int voiceConsent,
            bool voiceEnabled, bool lanDiscovery, bool upnp, MultiplayerHostingDefaults hosting, string? serviceOrigin = null)
        {
            Revision = LaunchContractValues.Revision(revision, nameof(revision));
            JournalApplied = LaunchContractValues.Revision(journalApplied, nameof(journalApplied));
            DisplayName = LaunchSessionValues.DisplayText(displayName, nameof(displayName), LaunchSessionValues.MinimumDisplayName,
                LaunchSessionValues.MaximumDisplayName);
            Disclosure = LaunchContractValues.Revision(disclosure, nameof(disclosure));
            VoiceConsent = LaunchContractValues.Revision(voiceConsent, nameof(voiceConsent));
            if (voiceEnabled && voiceConsent < 1) throw new ArgumentException("Voice stays off until the player consents.", nameof(voiceEnabled));
            VoiceEnabled = voiceEnabled;
            LanDiscovery = lanDiscovery;
            Upnp = upnp;
            Hosting = hosting ?? throw new ArgumentNullException(nameof(hosting));
            ServiceOrigin = serviceOrigin == null ? null : LaunchSessionValues.ServiceOrigin(serviceOrigin, nameof(serviceOrigin));
        }

        public int Revision { get; }
        public int JournalApplied { get; }
        public string DisplayName { get; }

        /// <summary>Gets the address disclosure version the player acknowledged; 0 when never.</summary>
        public int Disclosure { get; }

        /// <summary>Gets the microphone consent version the player gave; 0 when never.</summary>
        public int VoiceConsent { get; }
        public bool VoiceEnabled { get; }
        public bool LanDiscovery { get; }
        public bool Upnp { get; }
        public MultiplayerHostingDefaults Hosting { get; }
        public string? ServiceOrigin { get; }
    }

    /// <summary>One preference the player changed in game: exactly one of the setting members, numbered by sequence.</summary>
    public sealed class MultiplayerPreferenceChange
    {
        private MultiplayerPreferenceChange(int sequence, string kind)
        {
            Sequence = LaunchSessionValues.Ranged(sequence, nameof(sequence), 1, int.MaxValue);
            Kind = kind;
        }

        public static MultiplayerPreferenceChange DisplayName(int sequence, string displayName) =>
            new MultiplayerPreferenceChange(sequence, "displayName")
            {
                NewDisplayName = LaunchSessionValues.DisplayText(displayName, nameof(displayName), LaunchSessionValues.MinimumDisplayName,
                    LaunchSessionValues.MaximumDisplayName)
            };

        /// <summary>The player acknowledged the address disclosure; the launcher never lowers the version it holds.</summary>
        public static MultiplayerPreferenceChange Disclosure(int sequence, int disclosure) =>
            new MultiplayerPreferenceChange(sequence, "disclosure") { NewDisclosure = LaunchSessionValues.Ranged(disclosure, nameof(disclosure), 1, int.MaxValue) };

        public static MultiplayerPreferenceChange Voice(int sequence, int consent, bool enabled)
        {
            if (enabled && consent < 1) throw new ArgumentException("Voice stays off until the player consents.", nameof(enabled));
            return new MultiplayerPreferenceChange(sequence, "voice") { NewVoiceConsent = LaunchContractValues.Revision(consent, nameof(consent)), NewVoiceEnabled = enabled };
        }

        public static MultiplayerPreferenceChange Network(int sequence, bool lanDiscovery, bool upnp) =>
            new MultiplayerPreferenceChange(sequence, "network") { NewLanDiscovery = lanDiscovery, NewUpnp = upnp };

        public static MultiplayerPreferenceChange Hosting(int sequence, MultiplayerHostingDefaults hosting) =>
            new MultiplayerPreferenceChange(sequence, "hosting") { NewHosting = hosting ?? throw new ArgumentNullException(nameof(hosting)) };

        public int Sequence { get; }

        /// <summary>Gets <c>displayName</c>, <c>disclosure</c>, <c>voice</c>, <c>network</c> or <c>hosting</c>.</summary>
        public string Kind { get; }
        public string? NewDisplayName { get; private set; }
        public int NewDisclosure { get; private set; }
        public int NewVoiceConsent { get; private set; }
        public bool NewVoiceEnabled { get; private set; }
        public bool NewLanDiscovery { get; private set; }
        public bool NewUpnp { get; private set; }
        public MultiplayerHostingDefaults? NewHosting { get; private set; }
    }

    /// <summary>
    /// Preferences the player changed in game (<c>multiplayer-preferences-journal-v1.json</c> in manager staging, game to
    /// launcher): at most 64 changes in strictly increasing sequence order. The game appends and rewrites the whole file
    /// atomically, and drops what a projection's <see cref="MultiplayerPreferencesProjection.JournalApplied"/> covers.
    /// </summary>
    public sealed class MultiplayerPreferencesJournal
    {
        public const int SchemaVersion = 1;
        public const int MaximumChanges = 64;

        public MultiplayerPreferencesJournal(IEnumerable<MultiplayerPreferenceChange> changes)
        {
            var copy = (changes ?? throw new ArgumentNullException(nameof(changes))).Take(MaximumChanges + 1).ToArray();
            if (copy.Length > MaximumChanges) throw new ArgumentException("The journal holds at most 64 changes.", nameof(changes));
            for (var index = 1; index < copy.Length; index++)
            {
                if (copy[index].Sequence <= copy[index - 1].Sequence)
                    throw new ArgumentException("Journal sequences increase strictly.", nameof(changes));
            }

            Changes = Array.AsReadOnly(copy);
        }

        public IReadOnlyList<MultiplayerPreferenceChange> Changes { get; }

        /// <summary>The next sequence a change takes.</summary>
        public int NextSequence(MultiplayerPreferencesProjection? projection) =>
            Math.Max(Changes.Count == 0 ? 0 : Changes[Changes.Count - 1].Sequence, projection?.JournalApplied ?? 0) + 1;

        /// <summary>This journal without what <paramref name="projection"/> applied, with <paramref name="change"/> appended.</summary>
        public MultiplayerPreferencesJournal Append(MultiplayerPreferenceChange change, MultiplayerPreferencesProjection? projection)
        {
            var applied = projection?.JournalApplied ?? 0;
            return new MultiplayerPreferencesJournal(Changes.Where(item => item.Sequence > applied).Append(change));
        }
    }
}
