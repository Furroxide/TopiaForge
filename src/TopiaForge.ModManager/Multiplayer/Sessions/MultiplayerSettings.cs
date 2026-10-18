using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>A server the player saved or joined: its server id and last-known name, never an address.</summary>
    internal sealed class ServerBookmark
    {
        internal ServerBookmark(string serverId, string name, long savedAtUnixSeconds)
        {
            if (!InternalText.IsServerId(serverId)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            ServerId = serverId;
            Name = InternalText.IsDisplayText(name, InternalHostSettings.MaximumNameLength) ? name : "Server";
            SavedAtUnixSeconds = savedAtUnixSeconds < 0 ? 0 : savedAtUnixSeconds;
        }

        internal string ServerId { get; }

        /// <summary>Gets the server's name as its host wrote it; screens redact it before they show it.</summary>
        internal string Name { get; }

        internal long SavedAtUnixSeconds { get; }
    }

    /// <summary>
    /// The player's multiplayer preferences in this game, kept in manager data: the acknowledged disclosure version, Hide my
    /// IP, the display name, the last hosting settings (never a password) and the game last hosted, and favorite and recent
    /// servers by server id. Immutable; every change returns a new value. Nothing here can make a direct connection the
    /// default without the player turning Hide my IP off themselves.
    /// </summary>
    internal sealed class MultiplayerSettings
    {
        internal const int SchemaVersion = 1;
        internal const int MinimumDisplayNameLength = 2;
        internal const int MaximumDisplayNameLength = InternalJoinSettings.MaximumDisplayNameLength;
        internal const int MaximumFavorites = 100;
        internal const int MaximumHistory = 50;
        internal const string DefaultDisplayName = MultiplayerRuntimeHost.DefaultDisplayName;
        internal const int DefaultMaxPlayers = 8;

        internal static readonly MultiplayerSettings Default = new MultiplayerSettings(
            0, true, DefaultDisplayName, null, DefaultMaxPlayers, InternalSessionVisibility.Lan, false, null, null,
            Array.Empty<ServerBookmark>(), Array.Empty<ServerBookmark>());

        private MultiplayerSettings(
            int acknowledgedDisclosure,
            bool hideMyIp,
            string displayName,
            string? serverName,
            int hostMaxPlayers,
            InternalSessionVisibility hostVisibility,
            bool hostVoice,
            string? hostedTarget,
            string? hostedWorld,
            IReadOnlyList<ServerBookmark> favorites,
            IReadOnlyList<ServerBookmark> history)
        {
            AcknowledgedDisclosure = acknowledgedDisclosure < 0 ? 0 : acknowledgedDisclosure;
            HideMyIp = hideMyIp;
            DisplayName = IsValidDisplayName(displayName) ? displayName : DefaultDisplayName;
            ServerName = InternalText.IsDisplayText(serverName, InternalHostSettings.MaximumNameLength) ? serverName : null;
            HostMaxPlayers = hostMaxPlayers < InternalHostSettings.MinimumPlayers || hostMaxPlayers > InternalHostSettings.MaximumPlayers ? DefaultMaxPlayers : hostMaxPlayers;
            HostVisibility = Enum.IsDefined(typeof(InternalSessionVisibility), hostVisibility) ? hostVisibility : InternalSessionVisibility.Lan;
            HostVoice = hostVoice;

            // A game is remembered by its launch target and world, both declaration ids; anything else is forgotten.
            var target = IsDeclarationId(hostedTarget) ? hostedTarget : null;
            HostedTarget = target;
            HostedWorld = target != null && IsDeclarationId(hostedWorld) ? hostedWorld : null;
            Favorites = favorites;
            History = history;
        }

        /// <summary>Gets the newest disclosure version the player acknowledged; 0 when never.</summary>
        internal int AcknowledgedDisclosure { get; }

        internal bool HideMyIp { get; }

        internal string DisplayName { get; }

        /// <summary>Gets the server name last hosted with, or null to derive one from the display name.</summary>
        internal string? ServerName { get; }

        internal int HostMaxPlayers { get; }

        internal InternalSessionVisibility HostVisibility { get; }

        internal bool HostVoice { get; }

        /// <summary>Gets the launch target of the game last hosted, which HOST offers first at the menu; null when none.</summary>
        internal string? HostedTarget { get; }

        /// <summary>Gets the world of the game last hosted, or null for its target's own.</summary>
        internal string? HostedWorld { get; }

        /// <summary>Gets the favorite servers, newest first, each once.</summary>
        internal IReadOnlyList<ServerBookmark> Favorites { get; }

        /// <summary>Gets the servers joined, most recent first, each once.</summary>
        internal IReadOnlyList<ServerBookmark> History { get; }

        /// <summary>Gets whether the first-time disclosure must show before the next host or join.</summary>
        internal bool NeedsDisclosure => AcknowledgedDisclosure < MultiplayerPrivacyText.DisclosureVersion;

        /// <summary>Display names are 2 to 24 printable characters, as the launcher keeps them.</summary>
        internal static bool IsValidDisplayName(string? value) =>
            value != null && value.Length >= MinimumDisplayNameLength && InternalText.IsDisplayText(value, MaximumDisplayNameLength);

        internal MultiplayerSettings AcknowledgeDisclosure() =>
            Copy(acknowledgedDisclosure: Math.Max(AcknowledgedDisclosure, MultiplayerPrivacyText.DisclosureVersion));

        internal MultiplayerSettings WithHideMyIp(bool on) => Copy(hideMyIp: on);

        internal MultiplayerSettings WithDisplayName(string name)
        {
            if (!IsValidDisplayName(name)) throw new ArgumentException("A display name is 2 to 24 printable characters.", nameof(name));
            return Copy(displayName: name);
        }

        internal MultiplayerSettings WithHosting(string? name, int maxPlayers, InternalSessionVisibility visibility, bool voice) =>
            new MultiplayerSettings(AcknowledgedDisclosure, HideMyIp, DisplayName, name, maxPlayers, visibility, voice, HostedTarget, HostedWorld, Favorites, History);

        /// <summary>Remembers the game just hosted, by its launch target and world, so HOST offers it first next time.</summary>
        internal MultiplayerSettings WithHostedGame(string targetId, string? worldId) =>
            new MultiplayerSettings(AcknowledgedDisclosure, HideMyIp, DisplayName, ServerName, HostMaxPlayers, HostVisibility, HostVoice, targetId, worldId, Favorites, History);

        internal bool IsFavorite(string serverId)
        {
            foreach (var favorite in Favorites)
            {
                if (string.Equals(favorite.ServerId, serverId, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Adds a favorite first, or refreshes it. A full list refuses rather than drop one silently.</summary>
        internal MultiplayerSettings AddFavorite(ServerBookmark bookmark)
        {
            if (bookmark == null) throw new ArgumentNullException(nameof(bookmark));
            var next = Without(Favorites, bookmark.ServerId);
            if (next.Count >= MaximumFavorites) throw new InvalidOperationException("At most " + MaximumFavorites + " favorites can be kept.");
            next.Insert(0, bookmark);
            return Copy(favorites: next.AsReadOnly());
        }

        internal MultiplayerSettings RemoveFavorite(string serverId) => Copy(favorites: Without(Favorites, serverId).AsReadOnly());

        /// <summary>Moves a joined server to the front of the history, forgetting the oldest past the bound.</summary>
        internal MultiplayerSettings RecordJoin(ServerBookmark bookmark)
        {
            if (bookmark == null) throw new ArgumentNullException(nameof(bookmark));
            var next = Without(History, bookmark.ServerId);
            next.Insert(0, bookmark);
            if (next.Count > MaximumHistory) next.RemoveRange(MaximumHistory, next.Count - MaximumHistory);
            return Copy(history: next.AsReadOnly());
        }

        /// <summary>Forgets a joined server, as when the service no longer lists it.</summary>
        internal MultiplayerSettings ForgetJoin(string serverId) => Copy(history: Without(History, serverId).AsReadOnly());

        internal MultiplayerSettingsDocument ToDocument()
        {
            return new MultiplayerSettingsDocument
            {
                SchemaVersionValue = SchemaVersion,
                AcknowledgedPrivacyDisclosure = AcknowledgedDisclosure,
                HideMyIp = HideMyIp,
                DisplayName = DisplayName,
                ServerName = ServerName,
                HostMaxPlayers = HostMaxPlayers,
                HostVisibility = HostVisibility.ToString().ToLowerInvariant(),
                HostVoice = HostVoice,
                HostedTarget = HostedTarget,
                HostedWorld = HostedWorld,
                Favorites = Documents(Favorites),
                History = Documents(History)
            };
        }

        /// <summary>
        /// Reads a stored document forgivingly: a missing or invalid member takes its default, and a repeated or invalid
        /// bookmark is dropped. Hide my IP reads as on unless it was stored off. A document of another schema version is
        /// refused, so a newer game's settings are never overwritten with an older reading of them.
        /// </summary>
        internal static MultiplayerSettings FromDocument(MultiplayerSettingsDocument? document)
        {
            if (document == null) return Default;
            if (document.SchemaVersionValue != 0 && document.SchemaVersionValue != SchemaVersion)
            {
                throw new SerializationException("Unsupported multiplayer settings schemaVersion " + document.SchemaVersionValue + ".");
            }

            var visibility = InternalSessionVisibility.Lan;
            if (string.Equals(document.HostVisibility, "public", StringComparison.Ordinal)) visibility = InternalSessionVisibility.Public;
            else if (string.Equals(document.HostVisibility, "invite", StringComparison.Ordinal)) visibility = InternalSessionVisibility.Invite;
            return new MultiplayerSettings(
                document.AcknowledgedPrivacyDisclosure,
                document.HideMyIp ?? true,
                document.DisplayName ?? DefaultDisplayName,
                document.ServerName,
                document.HostMaxPlayers,
                visibility,
                document.HostVoice,
                document.HostedTarget,
                document.HostedWorld,
                Bookmarks(document.Favorites, MaximumFavorites),
                Bookmarks(document.History, MaximumHistory));
        }

        private MultiplayerSettings Copy(
            int? acknowledgedDisclosure = null,
            bool? hideMyIp = null,
            string? displayName = null,
            IReadOnlyList<ServerBookmark>? favorites = null,
            IReadOnlyList<ServerBookmark>? history = null) =>
            new MultiplayerSettings(
                acknowledgedDisclosure ?? AcknowledgedDisclosure,
                hideMyIp ?? HideMyIp,
                displayName ?? DisplayName,
                ServerName,
                HostMaxPlayers,
                HostVisibility,
                HostVoice,
                HostedTarget,
                HostedWorld,
                favorites ?? Favorites,
                history ?? History);

        private static bool IsDeclarationId(string? value) => value != null && ManifestContributionValidator.IsValidDeclarationId(value);

        private static List<ServerBookmark> Without(IReadOnlyList<ServerBookmark> list, string serverId)
        {
            var next = new List<ServerBookmark>(list.Count + 1);
            foreach (var item in list)
            {
                if (!string.Equals(item.ServerId, serverId, StringComparison.Ordinal)) next.Add(item);
            }

            return next;
        }

        private static List<ServerBookmarkDocument> Documents(IReadOnlyList<ServerBookmark> list)
        {
            var documents = new List<ServerBookmarkDocument>(list.Count);
            foreach (var item in list) documents.Add(new ServerBookmarkDocument { ServerId = item.ServerId, Name = item.Name, SavedAt = item.SavedAtUnixSeconds });
            return documents;
        }

        private static IReadOnlyList<ServerBookmark> Bookmarks(List<ServerBookmarkDocument>? documents, int maximum)
        {
            if (documents == null) return Array.Empty<ServerBookmark>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var bookmarks = new List<ServerBookmark>();
            foreach (var document in documents)
            {
                if (bookmarks.Count >= maximum) break;
                if (document == null || !InternalText.IsServerId(document.ServerId) || !seen.Add(document.ServerId!)) continue;
                bookmarks.Add(new ServerBookmark(document.ServerId!, document.Name ?? string.Empty, document.SavedAt));
            }

            return bookmarks.AsReadOnly();
        }
    }

    /// <summary>The stored form of <see cref="MultiplayerSettings"/>, at <c>data/multiplayer-settings.json</c>.</summary>
    [DataContract]
    internal sealed class MultiplayerSettingsDocument
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersionValue { get; set; }

        [DataMember(Name = "acknowledgedPrivacyDisclosure")]
        public int AcknowledgedPrivacyDisclosure { get; set; }

        [DataMember(Name = "hideMyIp", EmitDefaultValue = false)]
        public bool? HideMyIp { get; set; }

        [DataMember(Name = "displayName", EmitDefaultValue = false)]
        public string? DisplayName { get; set; }

        [DataMember(Name = "serverName", EmitDefaultValue = false)]
        public string? ServerName { get; set; }

        [DataMember(Name = "hostMaxPlayers")]
        public int HostMaxPlayers { get; set; }

        [DataMember(Name = "hostVisibility", EmitDefaultValue = false)]
        public string? HostVisibility { get; set; }

        [DataMember(Name = "hostVoice")]
        public bool HostVoice { get; set; }

        // Optional, and ignored by a reader that predates it, so the schema version stays 1.
        [DataMember(Name = "hostedTarget", EmitDefaultValue = false)]
        public string? HostedTarget { get; set; }

        [DataMember(Name = "hostedWorld", EmitDefaultValue = false)]
        public string? HostedWorld { get; set; }

        [DataMember(Name = "favorites", EmitDefaultValue = false)]
        public List<ServerBookmarkDocument>? Favorites { get; set; }

        [DataMember(Name = "history", EmitDefaultValue = false)]
        public List<ServerBookmarkDocument>? History { get; set; }
    }

    /// <summary>The stored form of one <see cref="ServerBookmark"/>.</summary>
    [DataContract]
    internal sealed class ServerBookmarkDocument
    {
        [DataMember(Name = "serverId")]
        public string? ServerId { get; set; }

        [DataMember(Name = "name")]
        public string? Name { get; set; }

        [DataMember(Name = "savedAt")]
        public long SavedAt { get; set; }
    }
}
