using System.Runtime.Serialization;

namespace Robotopia.UgcLiveSync
{
    /// <summary>
    /// Persisted configuration for the UGC live-sync mod, read via <c>IModContext.LoadConfig</c> from
    /// <c>BepInEx/RobotopiaModManager/config/robotopia.ugc.livesync.json</c>. The launcher/CLI writes the same
    /// file (see the Dart <c>UgcLiveSyncSettings</c> mirror), so the JSON keys here are a cross-language contract.
    /// </summary>
    /// <remarks>
    /// Asset overrides are intentionally not configurable here: they require a live <c>UnityEngine.GameObject</c>
    /// prefab, so they are registered programmatically via <see cref="Robotopia.Mods.IUgcLiveSyncService"/>.
    /// </remarks>
    [DataContract]
    public sealed class UgcLiveSyncConfig
    {
        /// <summary>Default Automerge sync server (upgraded to wss:// at connect time by the game).</summary>
        public const string DefaultSyncServerUrl = "https://automerge-repo-sync-server-main.onrender.com";

        public UgcLiveSyncConfig()
        {
            SeedDefaults();
        }

        /// <summary>Which channel to use when auto-connecting: <c>localFolder</c> or <c>automerge</c>.</summary>
        [DataMember(Name = "transport")]
        public string Transport { get; set; } = "localFolder";

        /// <summary>Folder watched for exported project files; empty uses the game's default UGC import folder.</summary>
        [DataMember(Name = "watchFolder")]
        public string WatchFolder { get; set; } = string.Empty;

        /// <summary>Full editor share URL (parsed for project + scene) for the Automerge channel.</summary>
        [DataMember(Name = "editorUrl")]
        public string EditorUrl { get; set; } = string.Empty;

        /// <summary>Automerge document url or raw id (used when <see cref="EditorUrl"/> is empty).</summary>
        [DataMember(Name = "documentUrl")]
        public string DocumentUrl { get; set; } = string.Empty;

        /// <summary>Automerge sync server url.</summary>
        [DataMember(Name = "syncServerUrl")]
        public string SyncServerUrl { get; set; } = DefaultSyncServerUrl;

        /// <summary>Preferred scene id inside the project; empty selects the first scene.</summary>
        [DataMember(Name = "sceneId")]
        public string SceneId { get; set; } = string.Empty;

        /// <summary>When true, the mod starts a session automatically once the menu scene is reached.</summary>
        [DataMember(Name = "autoConnectOnStart")]
        public bool AutoConnectOnStart { get; set; }

        /// <summary>Rejects any watched snapshot larger than this many bytes (guards against bad input).</summary>
        [DataMember(Name = "maxSnapshotBytes")]
        public long MaxSnapshotBytes { get; set; } = 16L * 1024 * 1024;

        /// <summary>Quiet period (ms) after a file change before reading it, to skip partial writes.</summary>
        [DataMember(Name = "debounceMilliseconds")]
        public int DebounceMilliseconds { get; set; } = 200;

        /// <summary>True when <see cref="Transport"/> selects the Automerge channel.</summary>
        public bool UsesAutomerge => string.Equals(Transport, "automerge", System.StringComparison.OrdinalIgnoreCase);

        // DataContractJsonSerializer constructs the instance with FormatterServices.GetUninitializedObject, which
        // bypasses the constructor and property initializers, so absent members would be null/0. Seed real
        // defaults before members are read; present members still override them (mirrors WorldsConfig).
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            Transport = "localFolder";
            WatchFolder = string.Empty;
            EditorUrl = string.Empty;
            DocumentUrl = string.Empty;
            SyncServerUrl = DefaultSyncServerUrl;
            SceneId = string.Empty;
            AutoConnectOnStart = false;
            MaxSnapshotBytes = 16L * 1024 * 1024;
            DebounceMilliseconds = 200;
        }
    }
}
