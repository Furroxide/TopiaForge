using System;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The preferences the launcher keeps that this game shows and changes: the player's display name and the hosting
    /// defaults. Immutable; every change returns a new value. A null <see cref="HostingName"/> means the server name is made
    /// from the display name, as a game without the launcher's projection makes it.
    /// </summary>
    internal sealed class MultiplayerPreferenceValues
    {
        internal const int DefaultPort = 27250;

        internal static readonly MultiplayerPreferenceValues Default = new MultiplayerPreferenceValues(
            MultiplayerSettings.DefaultDisplayName, null, MultiplayerSettings.DefaultMaxPlayers, InternalSessionVisibility.Lan, false, DefaultPort);

        internal MultiplayerPreferenceValues(string displayName, string? hostingName, int maxPlayers, InternalSessionVisibility visibility, bool voice, int port)
        {
            DisplayName = MultiplayerSettings.IsValidDisplayName(displayName) ? displayName : MultiplayerSettings.DefaultDisplayName;
            HostingName = InternalText.IsDisplayText(hostingName, InternalHostSettings.MaximumNameLength) ? hostingName : null;
            MaxPlayers = maxPlayers < InternalHostSettings.MinimumPlayers || maxPlayers > InternalHostSettings.MaximumPlayers ? MultiplayerSettings.DefaultMaxPlayers : maxPlayers;
            Visibility = Enum.IsDefined(typeof(InternalSessionVisibility), visibility) ? visibility : InternalSessionVisibility.Lan;
            Voice = voice;
            Port = port < 0 || port > 65535 ? DefaultPort : port;
        }

        internal string DisplayName { get; }

        /// <summary>Gets the server name hosting starts with, or null to make one from the display name.</summary>
        internal string? HostingName { get; }

        internal int MaxPlayers { get; }

        internal InternalSessionVisibility Visibility { get; }

        internal bool Voice { get; }

        /// <summary>Gets the game port the launcher hosts on: zero for any free one. This game's own hosting keeps the default.</summary>
        internal int Port { get; }

        internal MultiplayerPreferenceValues WithDisplayName(string name) => new MultiplayerPreferenceValues(name, HostingName, MaxPlayers, Visibility, Voice, Port);

        internal MultiplayerPreferenceValues WithHosting(string name, int maxPlayers, InternalSessionVisibility visibility, bool voice) =>
            new MultiplayerPreferenceValues(DisplayName, name, maxPlayers, visibility, voice, Port);

        /// <summary>
        /// The values a game reads at start: the launcher's projection, or the defaults without one, with every change of
        /// <paramref name="journal"/> the projection does not count yet applied in order, as the launcher will apply them. A
        /// projection counts a journal of its own epoch up to its position; a journal under any other epoch started over, and
        /// applies whole. Changes of preferences this game does not show are left to the launcher.
        /// </summary>
        internal static MultiplayerPreferenceValues From(MultiplayerPreferencesProjection? projection, MultiplayerPreferencesJournal? journal)
        {
            var values = projection == null
                ? Default
                : new MultiplayerPreferenceValues(projection.DisplayName, projection.Hosting.Name, projection.Hosting.MaxPlayers,
                    VisibilityOf(projection.Hosting.Visibility), projection.Hosting.Voice, projection.Hosting.Port);
            if (journal == null) return values;
            var applied = projection != null && string.Equals(projection.JournalEpoch, journal.Epoch, StringComparison.Ordinal) ? projection.JournalApplied : 0;
            foreach (var change in journal.Changes)
            {
                if (change.Sequence <= applied) continue;
                if (change.Kind == "displayName" && change.NewDisplayName != null)
                {
                    values = values.WithDisplayName(change.NewDisplayName);
                }
                else if (change.Kind == "hosting" && change.NewHosting != null)
                {
                    var hosting = change.NewHosting;
                    values = new MultiplayerPreferenceValues(values.DisplayName, hosting.Name, hosting.MaxPlayers, VisibilityOf(hosting.Visibility), hosting.Voice, hosting.Port);
                }
            }

            return values;
        }

        internal static string VisibilityName(InternalSessionVisibility visibility) =>
            visibility == InternalSessionVisibility.Public ? "public" : visibility == InternalSessionVisibility.Invite ? "invite" : "lan";

        private static InternalSessionVisibility VisibilityOf(string visibility) =>
            visibility == "public" ? InternalSessionVisibility.Public
            : visibility == "invite" ? InternalSessionVisibility.Invite
            : InternalSessionVisibility.Lan;
    }

    /// <summary>
    /// The launcher's preferences as this game uses and changes them (docs/internal/MultiplayerLaunchContract.md, section 7).
    /// They are read once at start, on the background queue, from the launcher's projection and the changes this game
    /// recorded since that the projection does not count yet. A change made in game takes effect at once and is recorded in
    /// the journal for the launcher to take in, never kept in manager data as well, so each preference has one writable copy.
    /// Journal writes run on the background queue one at a time, since a refused write or a locked journal waits for about
    /// 310 ms; a change made meanwhile is written next, the newest of each preference only. Without the launcher's channels the
    /// values last for the session. Main thread only.
    /// </summary>
    internal sealed class MultiplayerPreferencesService
    {
        internal const string RecordFailedText = "Your multiplayer preferences could not be saved for the TopiaForge launcher; this game tries again with your next change. Check that TopiaForge can write its data folder.";

        private readonly MultiplayerBackgroundQueue queue;
        private readonly ILauncherChannels? channels;
        private MultiplayerPreferenceValues current = MultiplayerPreferenceValues.Default;
        private string? pendingName;
        private MultiplayerHostingDefaults? pendingHosting;
        private bool loading;
        private bool writing;

        internal MultiplayerPreferencesService(MultiplayerBackgroundQueue queue, ILauncherChannels? channels)
        {
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.channels = channels;
        }

        /// <summary>Gets the values; the defaults until they load.</summary>
        internal MultiplayerPreferenceValues Current => current;

        internal bool IsLoaded { get; private set; }

        /// <summary>Gets whether a change is still being recorded.</summary>
        internal bool IsSaving => writing || pendingName != null || pendingHosting != null;

        /// <summary>Gets the last failure to record a change, for a toast; the screens take and clear it.</summary>
        internal string Failure { get; private set; } = string.Empty;

        /// <summary>Reads the values, once; <paramref name="loaded"/> runs on the main thread when they arrive.</summary>
        internal void Load(Action<MultiplayerPreferenceValues>? loaded = null)
        {
            if (IsLoaded || loading) return;
            loading = true;
            var source = channels;
            if (source == null)
            {
                Loaded(MultiplayerPreferenceValues.Default, loaded);
                return;
            }

            MultiplayerPreferenceValues read = MultiplayerPreferenceValues.Default;
            queue.Enqueue(() => read = MultiplayerPreferenceValues.From(source.ReadPreferences(), source.ReadPreferencesJournal()),
                failure => Loaded(failure == null ? read : MultiplayerPreferenceValues.Default, loaded));
        }

        /// <summary>Changes the display name, which must be 2 to 24 printable characters, and records it for the launcher; only once loaded.</summary>
        internal void ChangeDisplayName(string name)
        {
            if (!MultiplayerSettings.IsValidDisplayName(name)) throw new ArgumentException("A display name is 2 to 24 printable characters.", nameof(name));
            if (!IsLoaded) throw new InvalidOperationException("The multiplayer preferences have not loaded yet.");
            if (string.Equals(name, current.DisplayName, StringComparison.Ordinal)) return;
            current = current.WithDisplayName(name);
            pendingName = name;
            Record();
        }

        /// <summary>Changes the hosting defaults to what the player just hosted with, and records them for the launcher; only once loaded.</summary>
        internal void ChangeHosting(string name, int maxPlayers, InternalSessionVisibility visibility, bool voice)
        {
            if (!IsLoaded) throw new InvalidOperationException("The multiplayer preferences have not loaded yet.");
            var next = current.WithHosting(name, maxPlayers, visibility, voice);
            if (next.HostingName == null) return;
            if (string.Equals(next.HostingName, current.HostingName, StringComparison.Ordinal) && next.MaxPlayers == current.MaxPlayers &&
                next.Visibility == current.Visibility && next.Voice == current.Voice) return;
            current = next;
            pendingHosting = new MultiplayerHostingDefaults(next.HostingName, next.MaxPlayers, MultiplayerPreferenceValues.VisibilityName(next.Visibility), next.Port, next.Voice);
            Record();
        }

        /// <summary>Takes the last failure, so it is shown once.</summary>
        internal string TakeFailure()
        {
            var failure = Failure;
            Failure = string.Empty;
            return failure;
        }

        private void Loaded(MultiplayerPreferenceValues read, Action<MultiplayerPreferenceValues>? loaded)
        {
            loading = false;
            IsLoaded = true;
            current = read;
            loaded?.Invoke(current);
        }

        /// <summary>Writes the oldest pending change, one write at a time; each sets its whole preference, so only the newest of each is written.</summary>
        private void Record()
        {
            var source = channels;
            if (source == null)
            {
                pendingName = null;
                pendingHosting = null;
                return;
            }

            if (writing) return;
            Func<int, MultiplayerPreferenceChange> change;
            var name = pendingName;
            var hosting = pendingHosting;
            if (name != null)
            {
                pendingName = null;
                change = sequence => MultiplayerPreferenceChange.DisplayName(sequence, name);
            }
            else if (hosting != null)
            {
                pendingHosting = null;
                change = sequence => MultiplayerPreferenceChange.Hosting(sequence, hosting);
            }
            else
            {
                return;
            }

            writing = true;
            queue.Enqueue(() => source.RecordPreferenceChange(change), failure =>
            {
                writing = false;
                if (failure != null) Failure = RecordFailedText;
                Record();
            });
        }
    }
}
