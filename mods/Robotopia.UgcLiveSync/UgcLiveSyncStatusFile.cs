using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Robotopia.UgcLiveSync
{
    /// <summary>
    /// The status handshake the mod writes to <c>config/robotopia.ugc.livesync.status.json</c> (next to the
    /// runtime config). It carries game → launcher state so the launcher/CLI can auto-detect the game's default
    /// watch folder and render live diagnostics (connected document, active scene, last applied snapshot) without
    /// guessing. Unity-free on purpose so it unit-tests on plain .NET (the test project references neither
    /// GameCode nor UnityEngine), and serialized with the same <see cref="DataContractJsonSerializer"/> the
    /// runtime config uses, so the JSON keys are a cross-language contract with the Dart reader.
    /// </summary>
    [DataContract]
    public sealed class UgcLiveSyncStatusFile
    {
        public UgcLiveSyncStatusFile()
        {
            SeedDefaults();
        }

        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        /// <summary>Current <c>UgcLiveSyncStatus</c> name (e.g. <c>Idle</c>, <c>Watching</c>, <c>Connected</c>).</summary>
        [DataMember(Name = "status")]
        public string Status { get; set; } = "Idle";

        /// <summary>Active transport: <c>localFolder</c> or <c>automerge</c>.</summary>
        [DataMember(Name = "transport")]
        public string Transport { get; set; } = "localFolder";

        /// <summary>The game's default UGC import folder (so the launcher can pre-fill the watch folder).</summary>
        [DataMember(Name = "defaultWatchFolder")]
        public string DefaultWatchFolder { get; set; } = string.Empty;

        /// <summary>The folder currently being watched (local channel), when a session is active.</summary>
        [DataMember(Name = "watchFolder")]
        public string WatchFolder { get; set; } = string.Empty;

        /// <summary>The live Automerge document url currently connected (Automerge channel), when active.</summary>
        [DataMember(Name = "connectedDocumentUrl")]
        public string ConnectedDocumentUrl { get; set; } = string.Empty;

        /// <summary>The scene id currently being synced (may be empty when the first scene is used).</summary>
        [DataMember(Name = "sceneId")]
        public string SceneId { get; set; } = string.Empty;

        /// <summary>Scene ids seen in applied snapshots (best-effort; the launcher also parses the watch folder).</summary>
        [DataMember(Name = "availableScenes")]
        public string[] AvailableScenes { get; set; } = Array.Empty<string>();

        /// <summary>UTC ISO-8601 timestamp of the most recently applied snapshot, or empty.</summary>
        [DataMember(Name = "lastAppliedUtc")]
        public string LastAppliedUtc { get; set; } = string.Empty;

        /// <summary>The UGC live-sync mod version that wrote this file.</summary>
        [DataMember(Name = "modVersion")]
        public string ModVersion { get; set; } = string.Empty;

        /// <summary>UTC ISO-8601 timestamp of when this file was last written.</summary>
        [DataMember(Name = "updatedUtc")]
        public string UpdatedUtc { get; set; } = string.Empty;

        // DataContractJsonSerializer bypasses the constructor on read, so seed real defaults first (mirrors
        // UgcLiveSyncConfig); present members still override them.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            SchemaVersion = 1;
            Status = "Idle";
            Transport = "localFolder";
            DefaultWatchFolder = string.Empty;
            WatchFolder = string.Empty;
            ConnectedDocumentUrl = string.Empty;
            SceneId = string.Empty;
            AvailableScenes = Array.Empty<string>();
            LastAppliedUtc = string.Empty;
            ModVersion = string.Empty;
            UpdatedUtc = string.Empty;
        }

        /// <summary>Adds a scene id to <see cref="AvailableScenes"/> if not already present.</summary>
        public void AddScene(string scene)
        {
            if (string.IsNullOrEmpty(scene))
            {
                return;
            }

            var scenes = AvailableScenes ?? Array.Empty<string>();
            foreach (var existing in scenes)
            {
                if (string.Equals(existing, scene, StringComparison.Ordinal))
                {
                    return;
                }
            }

            var next = new string[scenes.Length + 1];
            Array.Copy(scenes, next, scenes.Length);
            next[scenes.Length] = scene;
            AvailableScenes = next;
        }

        /// <summary>Serializes to JSON using the same serializer the runtime config uses.</summary>
        public string ToJson()
        {
            var serializer = new DataContractJsonSerializer(typeof(UgcLiveSyncStatusFile));
            using var stream = new MemoryStream();
            serializer.WriteObject(stream, this);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <summary>Parses JSON written by <see cref="ToJson"/>.</summary>
        public static UgcLiveSyncStatusFile FromJson(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(UgcLiveSyncStatusFile));
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty));
            return (UgcLiveSyncStatusFile)serializer.ReadObject(stream)!;
        }

        /// <summary>Derives the status-file path from the runtime config file path (sibling, <c>*.status.json</c>).</summary>
        public static string PathForConfig(string configFilePath)
        {
            if (string.IsNullOrWhiteSpace(configFilePath))
            {
                return string.Empty;
            }

            var directory = Path.GetDirectoryName(configFilePath) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(configFilePath); // robotopia.ugc.livesync
            return Path.Combine(directory, baseName + ".status.json");
        }

        /// <summary>Atomically writes the status file (temp + replace) so a reader never sees a partial file.</summary>
        public void WriteTo(string statusFilePath)
        {
            if (string.IsNullOrWhiteSpace(statusFilePath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(statusFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = statusFilePath + ".tmp";
            File.WriteAllText(temp, ToJson());
            if (File.Exists(statusFilePath))
            {
                File.Delete(statusFilePath);
            }

            File.Move(temp, statusFilePath);
        }
    }
}
