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
        public const int MaxAvailableScenes = 128;
        public const int MaxFileBytes = 64 * 1024;

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

            var nextLength = Math.Min(MaxAvailableScenes, scenes.Length + 1);
            var next = new string[nextLength];
            var sourceOffset = scenes.Length >= MaxAvailableScenes ? scenes.Length - (MaxAvailableScenes - 1) : 0;
            var copyCount = Math.Min(scenes.Length, nextLength - 1);
            Array.Copy(scenes, sourceOffset, next, 0, copyCount);
            next[next.Length - 1] = scene;
            AvailableScenes = next;
        }

        /// <summary>Clears live-session fields after a stop; optionally clears historical applied-scene data too.</summary>
        public void ClearLiveSession(bool clearHistory)
        {
            WatchFolder = string.Empty;
            ConnectedDocumentUrl = string.Empty;
            SceneId = string.Empty;
            if (clearHistory)
            {
                AvailableScenes = Array.Empty<string>();
                LastAppliedUtc = string.Empty;
            }
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
            return serializer.ReadObject(stream) as UgcLiveSyncStatusFile
                ?? throw new InvalidDataException("UGC live-sync status JSON produced no status object.");
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

            var json = ToJson();
            var bytes = new UTF8Encoding(false, true).GetBytes(json);
            if (bytes.Length > MaxFileBytes)
            {
                throw new InvalidDataException("UGC live-sync status exceeds " + MaxFileBytes + " bytes.");
            }

            var temp = statusFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                    temp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(statusFilePath))
                {
                    File.Replace(temp, statusFilePath, null);
                }
                else
                {
                    File.Move(temp, statusFilePath);
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
        }
    }
}
