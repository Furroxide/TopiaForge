using System;
using System.Collections.Generic;

namespace Robotopia.Mods
{
    /// <summary>
    /// Live UGC (user-generated content) content-sync service. Lets a mod stream level content into the
    /// running game and hot-reload it with no restart, either from a watched export folder (the local dev
    /// loop a Unity-Editor companion drives) or from an external editor's live Automerge document.
    /// </summary>
    /// <remarks>
    /// The game is <b>preview/play only</b>: this service consumes snapshots and patches the scene; it never
    /// writes edits back to the source document. It is published by the <c>Robotopia.UgcLiveSync</c> framework
    /// mod and resolved with <c>context.GetService&lt;IUgcLiveSyncService&gt;()</c>, the same way
    /// <see cref="IWorldGamemodeService"/> is consumed.
    /// </remarks>
    public interface IUgcLiveSyncService
    {
        /// <summary>The session that is currently live, or <c>null</c> when nothing is syncing.</summary>
        UgcSyncSession? CurrentSession { get; }

        /// <summary>Current lifecycle state of the service.</summary>
        UgcLiveSyncStatus Status { get; }

        /// <summary>Asset-id overrides registered so modder-supplied prefabs render instead of placeholder cubes.</summary>
        IReadOnlyList<UgcAssetOverride> AssetOverrides { get; }

        /// <summary>Raised once when a session begins (after the UGC play scene is ready).</summary>
        event Action<UgcSyncSession>? SessionStarted;

        /// <summary>Raised after the first snapshot of a session has been imported (full build).</summary>
        event Action<UgcSnapshotInfo>? SnapshotImported;

        /// <summary>
        /// Raised after each subsequent snapshot is applied. <see cref="UgcSnapshotInfo.IsFullRebuild"/> is
        /// <c>true</c> when incremental patching could not be used and the scene was rebuilt.
        /// </summary>
        event Action<UgcSnapshotInfo>? PatchApplied;

        /// <summary>Raised when a snapshot is rejected or a sync step fails; the running scene is left intact.</summary>
        event Action<UgcSyncError>? SyncError;

        /// <summary>Raised once when a session stops (explicit <see cref="Stop"/>, scene exit, or unload).</summary>
        event Action<UgcSyncSession>? SessionStopped;

        /// <summary>
        /// Starts the local-folder channel: watches <see cref="UgcLiveSyncRequest.WatchFolder"/> for exported
        /// <c>UgcExportProject</c> files (<c>.json</c>/<c>.json.gz</c>) and applies each new snapshot live.
        /// </summary>
        UgcLiveSyncResult StartLocalSession(UgcLiveSyncRequest request);

        /// <summary>
        /// Starts the Automerge channel: subscribes to the editor's live document
        /// (<see cref="UgcLiveSyncRequest.DocumentUrl"/> or <see cref="UgcLiveSyncRequest.EditorUrl"/>) over the
        /// <see cref="UgcLiveSyncRequest.SyncServerUrl"/> and applies revisions live.
        /// </summary>
        UgcLiveSyncResult StartAutomergeSession(UgcLiveSyncRequest request);

        /// <summary>Stops the current session (if any) and releases the watcher / live connection.</summary>
        void Stop();

        /// <summary>
        /// Registers a runtime asset override so the given UGC asset id resolves to a modder-supplied prefab.
        /// The prefab must be a loaded <c>UnityEngine.GameObject</c> (for example from <see cref="IAssetBundleService"/>).
        /// Takes effect on the next import/rebuild.
        /// </summary>
        void RegisterAssetOverride(UgcAssetOverride assetOverride);

        /// <summary>Clears all registered asset overrides; built-in/placeholder resolution resumes.</summary>
        void ClearAssetOverrides();
    }

    /// <summary>Which channel a live-sync session uses.</summary>
    public enum UgcSyncTransport
    {
        /// <summary>Watch a local folder for exported project files (the Unity companion dev loop).</summary>
        LocalFolder,

        /// <summary>Subscribe to an external editor's live Automerge document.</summary>
        Automerge
    }

    /// <summary>Lifecycle state of <see cref="IUgcLiveSyncService"/>.</summary>
    public enum UgcLiveSyncStatus
    {
        /// <summary>No session is active.</summary>
        Idle,

        /// <summary>A session was requested and is being set up.</summary>
        Starting,

        /// <summary>Waiting for the UGC play scene to become active before content can be applied.</summary>
        WaitingForScene,

        /// <summary>An Automerge session is connected to the sync server.</summary>
        Connected,

        /// <summary>A local-folder session is watching for snapshots.</summary>
        Watching,

        /// <summary>The required game symbols are unavailable in this build; the service is inert.</summary>
        Unavailable,

        /// <summary>The last operation failed; see the most recent <see cref="IUgcLiveSyncService.SyncError"/>.</summary>
        Error,

        /// <summary>The session has stopped.</summary>
        Stopped
    }

    /// <summary>Parameters for starting a live-sync session. Unused fields for a given channel are ignored.</summary>
    public sealed class UgcLiveSyncRequest
    {
        public UgcLiveSyncRequest(
            string watchFolder = "",
            string editorUrl = "",
            string documentUrl = "",
            string syncServerUrl = "",
            string sceneId = "",
            string filePattern = "*.json;*.json.gz",
            int debounceMilliseconds = 200)
        {
            WatchFolder = watchFolder ?? string.Empty;
            EditorUrl = editorUrl ?? string.Empty;
            DocumentUrl = documentUrl ?? string.Empty;
            SyncServerUrl = syncServerUrl ?? string.Empty;
            SceneId = sceneId ?? string.Empty;
            FilePattern = string.IsNullOrWhiteSpace(filePattern) ? "*.json;*.json.gz" : filePattern;
            DebounceMilliseconds = debounceMilliseconds;
        }

        /// <summary>Folder watched for exported project files (local-folder channel).</summary>
        public string WatchFolder { get; }

        /// <summary>Full editor share URL (<c>https://host/?project=&lt;doc&gt;&amp;scene=&lt;id&gt;</c>); parsed if set.</summary>
        public string EditorUrl { get; }

        /// <summary>Automerge document url or raw id (Automerge channel); ignored when <see cref="EditorUrl"/> is set.</summary>
        public string DocumentUrl { get; }

        /// <summary>Automerge sync server url (defaults applied by the service when empty).</summary>
        public string SyncServerUrl { get; }

        /// <summary>Preferred scene id inside the project; empty selects the first scene.</summary>
        public string SceneId { get; }

        /// <summary>Semicolon-separated file globs to watch (local-folder channel).</summary>
        public string FilePattern { get; }

        /// <summary>Quiet period (ms) after a file change before it is read, to skip partial writes.</summary>
        public int DebounceMilliseconds { get; }
    }

    /// <summary>Describes an active live-sync session.</summary>
    public sealed class UgcSyncSession
    {
        public UgcSyncSession(UgcSyncTransport transport, string target, string sceneId, DateTime startedAtUtc)
        {
            Transport = transport;
            Target = target;
            SceneId = sceneId;
            StartedAtUtc = startedAtUtc;
        }

        /// <summary>The channel this session uses.</summary>
        public UgcSyncTransport Transport { get; }

        /// <summary>The watched folder (local) or document url (Automerge).</summary>
        public string Target { get; }

        /// <summary>The scene id being synced (may be empty when the first scene is used).</summary>
        public string SceneId { get; }

        /// <summary>When the session started (UTC).</summary>
        public DateTime StartedAtUtc { get; }
    }

    /// <summary>Metadata about a snapshot that was imported or patched into the running scene.</summary>
    public sealed class UgcSnapshotInfo
    {
        public UgcSnapshotInfo(
            string projectName,
            string sceneId,
            string sceneName,
            int entityCount,
            string revisionLabel,
            bool isFullRebuild,
            DateTime appliedAtUtc)
        {
            ProjectName = projectName;
            SceneId = sceneId;
            SceneName = sceneName;
            EntityCount = entityCount;
            RevisionLabel = revisionLabel;
            IsFullRebuild = isFullRebuild;
            AppliedAtUtc = appliedAtUtc;
        }

        /// <summary>The imported project's name.</summary>
        public string ProjectName { get; }

        /// <summary>Resolved scene id.</summary>
        public string SceneId { get; }

        /// <summary>Resolved scene display name.</summary>
        public string SceneName { get; }

        /// <summary>Number of entities in the resolved scene.</summary>
        public int EntityCount { get; }

        /// <summary>Human-readable source label (e.g. file name or "Live Automerge revision N").</summary>
        public string RevisionLabel { get; }

        /// <summary><c>true</c> when the scene was fully rebuilt rather than incrementally patched.</summary>
        public bool IsFullRebuild { get; }

        /// <summary>When this snapshot was applied (UTC).</summary>
        public DateTime AppliedAtUtc { get; }
    }

    /// <summary>
    /// Maps a UGC asset id to a modder-supplied prefab so authored entities render as real content rather than
    /// the importer's placeholder cube.
    /// </summary>
    public sealed class UgcAssetOverride
    {
        /// <param name="assetId">UGC asset id (e.g. <c>@author/my-prop</c>) as referenced by exported entities.</param>
        /// <param name="prefab">A loaded <c>UnityEngine.GameObject</c> prefab; validated by the service.</param>
        /// <param name="localPositionOffset">Optional local-space offset (x,y,z) aligning the prefab to the UGC origin.</param>
        public UgcAssetOverride(string assetId, object prefab, float[]? localPositionOffset = null)
        {
            AssetId = assetId;
            Prefab = prefab;
            LocalPositionOffset = localPositionOffset;
        }

        /// <summary>UGC asset id this override resolves.</summary>
        public string AssetId { get; }

        /// <summary>The prefab object (a <c>UnityEngine.GameObject</c>); kept as <see cref="object"/> to keep the SDK Unity-free.</summary>
        public object Prefab { get; }

        /// <summary>Optional <c>[x, y, z]</c> local-space offset; <c>null</c> means zero.</summary>
        public float[]? LocalPositionOffset { get; }
    }

    /// <summary>A non-fatal error raised while syncing; the running scene is preserved.</summary>
    public sealed class UgcSyncError
    {
        public UgcSyncError(string phase, string message)
        {
            Phase = phase;
            Message = message;
        }

        /// <summary>The phase that failed (e.g. <c>"validate"</c>, <c>"load"</c>, <c>"apply"</c>, <c>"connect"</c>).</summary>
        public string Phase { get; }

        /// <summary>Human-readable error detail.</summary>
        public string Message { get; }
    }

    /// <summary>Result of a request to start a live-sync session.</summary>
    public sealed class UgcLiveSyncResult
    {
        private UgcLiveSyncResult(bool ok, UgcSyncSession? session, string message)
        {
            Ok = ok;
            Session = session;
            Message = message;
        }

        /// <summary><c>true</c> when the session started (or is starting).</summary>
        public bool Ok { get; }

        /// <summary>The started session, when <see cref="Ok"/> is <c>true</c>.</summary>
        public UgcSyncSession? Session { get; }

        /// <summary>Human-readable status / failure detail.</summary>
        public string Message { get; }

        /// <summary>Creates a success result.</summary>
        public static UgcLiveSyncResult Success(UgcSyncSession session, string message)
        {
            return new UgcLiveSyncResult(true, session, message);
        }

        /// <summary>Creates a failure result.</summary>
        public static UgcLiveSyncResult Fail(string message)
        {
            return new UgcLiveSyncResult(false, null, message);
        }
    }
}
