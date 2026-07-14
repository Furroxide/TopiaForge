using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.UgcLiveSync
{
    /// <summary>
    /// Unity-free implementation of <see cref="IUgcLiveSyncService"/>. All game/Unity work is delegated to an
    /// <see cref="IUgcLiveSyncBridge"/> so this state machine (debounce, validation, first-vs-subsequent
    /// snapshots, lifecycle) can be unit-tested on plain .NET. The owning mod pumps <see cref="Pump"/> from the
    /// per-frame Update event (Unity main thread) and forwards scene loads to <see cref="NotifySceneLoaded"/>.
    /// </summary>
    internal sealed class UgcLiveSyncService : IUgcLiveSyncService, IDisposable
    {
        private const float SceneDispatchTimeoutSeconds = 30f;
        private const long DefaultMaxSnapshotBytes = 16L * 1024 * 1024;
        private const int MaxWatchDirectoryEntries = 4096;

        internal enum SnapshotReadOutcome
        {
            Success,
            Retry,
            Rejected
        }

        internal enum SnapshotScanOutcome
        {
            Found,
            Empty,
            Retry,
            Rejected
        }

        internal readonly struct SnapshotScanResult
        {
            public SnapshotScanResult(SnapshotScanOutcome outcome, string? path, string error)
            {
                Outcome = outcome;
                Path = path;
                Error = error;
            }

            public SnapshotScanOutcome Outcome { get; }
            public string? Path { get; }
            public string Error { get; }
        }

        private readonly IUgcLiveSyncBridge bridge;
        private readonly IModLogger logger;
        private readonly bool enableFileWatcher;
        private readonly List<UgcAssetOverride> overrides = new List<UgcAssetOverride>();
        private readonly ReadOnlyCollection<UgcAssetOverride> overridesView;
        private readonly object gate = new object();

        private FileSystemWatcher? watcher;
        private string watchFolder = string.Empty;
        private string sceneId = string.Empty;
        private float debounceSeconds = 0.2f;

        // Set on the watcher's background thread, drained on the main-thread pump.
        private bool dirty;
        private float debounceRemaining;

        // Pending start while we wait for the UGC play scene to become active.
        private UgcSyncTransport pendingTransport;
        private bool awaitingScene;
        private UgcSyncSession? pendingSession;
        private bool deferredSceneLoad;
        private bool sceneLoadDispatched;
        private float sceneDispatchRemaining;
        private string pendingSceneFailure = string.Empty;
        private SceneTransitionPriority pendingPriority = SceneTransitionPriority.UserInitiated;
        private string pendingAutomergeDocumentUrl = string.Empty;
        private string pendingAutomergeSyncServerUrl = string.Empty;
        // Native callbacks can already be queued when Stop/failure detaches the bridge. Generation-tag every
        // Automerge arm so a late callback from an abandoned or superseded request cannot resurrect a session.
        private int automergeCallbackGeneration;

        // In-flight claim on the play-scene load (see ISceneCoordinator); released on the next scene arrival.
        private IDisposable? sceneClaim;
        // SceneLoaded is dispatched to mods in load order. Keep a resolved claim visible until the next Pump
        // so a world-session owner handling the same event can still identify this foreign takeover.
        private bool sceneClaimReleasePending;

        public UgcLiveSyncService(IUgcLiveSyncBridge bridge, IModLogger logger)
            : this(bridge, logger, enableFileWatcher: true)
        {
        }

        // Test seam: disables the OS FileSystemWatcher so unit tests drive snapshots deterministically via
        // MarkDirty + Pump instead of racing real file-system events.
        internal UgcLiveSyncService(IUgcLiveSyncBridge bridge, IModLogger logger, bool enableFileWatcher)
        {
            this.bridge = bridge;
            this.logger = logger;
            this.enableFileWatcher = enableFileWatcher;
            overridesView = new ReadOnlyCollection<UgcAssetOverride>(overrides);
            Status = bridge.IsAvailable ? UgcLiveSyncStatus.Idle : UgcLiveSyncStatus.Unavailable;
        }

        public UgcSyncSession? CurrentSession { get; private set; }
        public UgcLiveSyncStatus Status { get; private set; }
        public IReadOnlyList<UgcAssetOverride> AssetOverrides => overridesView;

        // The launcher status handshake needs to expose the requested target while WaitingForScene, but the
        // public CurrentSession contract remains null until the scene is actually ready and SessionStarted fires.
        internal UgcSyncSession? PendingSession => pendingSession;

        /// <summary>
        /// Optional scene-transition arbiter (the manager's <see cref="ISceneCoordinator"/>). When set, a
        /// session that must load the UGC play scene first asks for the transition at the request's priority;
        /// a refused automatic request defers the load and attaches when the play scene arrives on its own
        /// (e.g. the player launches the sandbox) instead of stomping a live world/gamemode session.
        /// </summary>
        public ISceneCoordinator? SceneCoordinator { get; set; }

        /// <summary>Owner id used for coordinator claims; the owning mod sets this to its mod id.</summary>
        public string SceneOwnerId { get; set; } = "io.github.furroxide.topiaforge.ugc.livesync";

        public event Action<UgcSyncSession>? SessionStarted;
        public event Action<UgcSnapshotInfo>? SnapshotImported;
        public event Action<UgcSnapshotInfo>? PatchApplied;
        public event Action<UgcSyncError>? SyncError;
        public event Action<UgcSyncSession>? SessionStopped;

        public UgcLiveSyncResult StartLocalSession(UgcLiveSyncRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!bridge.IsAvailable)
            {
                Status = UgcLiveSyncStatus.Unavailable;
                return UgcLiveSyncResult.Fail("UGC live sync is unavailable in this build (game symbols not found).");
            }

            var folder = string.IsNullOrWhiteSpace(request.WatchFolder) ? bridge.GetDefaultWatchFolder() : request.WatchFolder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                return UgcLiveSyncResult.Fail("No watch folder configured and no default import folder is available.");
            }

            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                return UgcLiveSyncResult.Fail("Could not create watch folder '" + folder + "': " + ex.Message);
            }

            // Do not tear down a healthy session until the replacement request passes its cheap validation.
            Stop();
            watchFolder = folder;
            sceneId = request.SceneId ?? string.Empty;
            debounceSeconds = Math.Max(0f, request.DebounceMilliseconds / 1000f);
            pendingTransport = UgcSyncTransport.LocalFolder;
            pendingPriority = request.Priority;
            var requestedSession = new UgcSyncSession(
                UgcSyncTransport.LocalFolder, watchFolder, sceneId, DateTime.UtcNow);
            pendingSession = requestedSession;

            if (bridge.IsImportControllerReady())
            {
                if (!BeginLocalWatch())
                {
                    return UgcLiveSyncResult.Fail("Could not watch '" + watchFolder + "' (see log).");
                }

                return UgcLiveSyncResult.Success(
                    CurrentSession!, "Watching '" + watchFolder + "' for UGC snapshots.");
            }

            // No UGC play scene yet: load it (content import suppressed) and attach once it is ready. The
            // load is arbitrated — an automatic trigger yields to whoever holds the scene (e.g. a live world
            // session) and attaches later, when the play scene arrives on its own.
            awaitingScene = true;
            Status = UgcLiveSyncStatus.WaitingForScene;

            var coordinator = SceneCoordinator;
            if (coordinator != null)
            {
                var decision = coordinator.RequestTransition(new SceneTransitionRequest(
                    SceneOwnerId, bridge.PlaySceneName, request.Priority, "attach UGC live sync"));
                if (!decision.Approved)
                {
                    deferredSceneLoad = true;
                    logger.Info("UGC live sync: play-scene load deferred (" + decision.Message
                        + "); will attach when the UGC play scene loads.");
                    return UgcLiveSyncResult.Success(
                        requestedSession,
                        "Deferred: " + decision.Message + " Watching '" + watchFolder
                        + "' will begin when the UGC play scene loads.");
                }

                sceneClaim?.Dispose();
                sceneClaim = decision.Claim;
            }

            if (!bridge.EnsurePlaySceneLoaded())
            {
                FailPendingStart("load", "Could not load the UGC play scene.");
                return UgcLiveSyncResult.Fail("Could not load the UGC play scene (see log).");
            }

            BeginSceneDispatchTimeout();

            logger.Info("UGC live sync: waiting for the UGC play scene before watching '" + watchFolder + "'.");
            return UgcLiveSyncResult.Success(
                requestedSession,
                "Loading UGC play scene, then watching '" + watchFolder + "'.");
        }

        public UgcLiveSyncResult StartAutomergeSession(UgcLiveSyncRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!bridge.IsAvailable)
            {
                Status = UgcLiveSyncStatus.Unavailable;
                return UgcLiveSyncResult.Fail("UGC live sync is unavailable in this build (game symbols not found).");
            }

            var documentUrl = request.DocumentUrl ?? string.Empty;
            var resolvedScene = request.SceneId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(request.EditorUrl) && TryParseEditorUrl(request.EditorUrl, out var docFromUrl, out var sceneFromUrl))
            {
                documentUrl = docFromUrl;
                if (!string.IsNullOrWhiteSpace(sceneFromUrl))
                {
                    resolvedScene = sceneFromUrl;
                }
            }

            if (string.IsNullOrWhiteSpace(documentUrl))
            {
                return UgcLiveSyncResult.Fail("No Automerge document url or editor url provided.");
            }

            if (documentUrl.Length > 4096 || resolvedScene.Length > 512)
            {
                return UgcLiveSyncResult.Fail("Automerge document or scene identifiers exceed the runtime limit.");
            }

            var syncServer = string.IsNullOrWhiteSpace(request.SyncServerUrl)
                ? UgcLiveSyncConfig.DefaultSyncServerUrl
                : request.SyncServerUrl.Trim();
            if (!TryValidateSecureSyncServerUrl(syncServer, out var syncServerError))
            {
                return UgcLiveSyncResult.Fail(syncServerError);
            }

            // Validation succeeded; only now replace any active/pending session.
            Stop();
            sceneId = resolvedScene;
            pendingAutomergeDocumentUrl = documentUrl;
            pendingAutomergeSyncServerUrl = syncServer;
            pendingTransport = UgcSyncTransport.Automerge;
            pendingPriority = request.Priority;
            var requestedSession = new UgcSyncSession(
                UgcSyncTransport.Automerge, documentUrl, resolvedScene, DateTime.UtcNow);
            pendingSession = requestedSession;
            awaitingScene = true;
            Status = UgcLiveSyncStatus.WaitingForScene;

            var loadPlayScene = true;
            var coordinator = SceneCoordinator;
            if (coordinator != null)
            {
                var decision = coordinator.RequestTransition(new SceneTransitionRequest(
                    SceneOwnerId, bridge.PlaySceneName, request.Priority, "attach UGC live sync (Automerge)"));
                if (!decision.Approved)
                {
                    loadPlayScene = false;
                    deferredSceneLoad = true;
                    logger.Info("UGC live sync: Automerge play-scene load deferred (" + decision.Message
                        + "); the native request is armed for the next UGC play-scene load.");
                }
                else
                {
                    sceneClaim?.Dispose();
                    sceneClaim = decision.Claim;
                }
            }

            if (!bridge.StartAutomerge(
                    documentUrl,
                    syncServer,
                    resolvedScene,
                    loadPlayScene,
                    CreateAutomergeRevisionCallback()))
            {
                FailPendingStart("connect", loadPlayScene
                    ? "Could not start the Automerge live session or load the UGC play scene."
                    : "Could not arm the deferred Automerge live session.");
                return UgcLiveSyncResult.Fail("Could not start the Automerge live session (see log).");
            }

            if (!loadPlayScene)
            {
                return UgcLiveSyncResult.Success(
                    requestedSession,
                    "Deferred until the UGC play scene is available; Automerge document '" + documentUrl
                    + "' is armed for connection.");
            }

            BeginSceneDispatchTimeout();

            logger.Info("UGC live sync: loading the UGC play scene for Automerge document '" + documentUrl + "'.");
            return UgcLiveSyncResult.Success(
                requestedSession, "Loading the UGC play scene, then connecting to the Automerge document.");
        }

        public void Stop()
        {
            DisposeWatcher();
            awaitingScene = false;
            InvalidateAutomergeCallbacks();
            sceneClaim?.Dispose();
            sceneClaim = null;
            sceneClaimReleasePending = false;

            if (bridge.IsAvailable)
            {
                bridge.StopAutomerge();
                bridge.ClearAssetOverrides();
            }

            var session = CurrentSession;
            var pending = pendingSession;
            CurrentSession = null;
            pendingSession = null;
            deferredSceneLoad = false;
            sceneLoadDispatched = false;
            sceneDispatchRemaining = 0f;
            pendingSceneFailure = string.Empty;
            pendingPriority = SceneTransitionPriority.UserInitiated;
            pendingAutomergeDocumentUrl = string.Empty;
            pendingAutomergeSyncServerUrl = string.Empty;
            lock (gate)
            {
                dirty = false;
                debounceRemaining = 0f;
            }

            if (session != null)
            {
                Status = UgcLiveSyncStatus.Stopped;
                Raise(SessionStopped, session, "SessionStopped");
            }
            else if (pending != null)
            {
                // A requested session can be stopped before its scene is ready. It never raised SessionStarted,
                // so do not synthesize a mismatched SessionStopped event, but do surface the explicit stop.
                Status = UgcLiveSyncStatus.Stopped;
            }
            else if (Status != UgcLiveSyncStatus.Unavailable)
            {
                Status = UgcLiveSyncStatus.Idle;
            }
        }

        public void RegisterAssetOverride(UgcAssetOverride assetOverride)
        {
            if (assetOverride == null)
            {
                return;
            }

            overrides.RemoveAll(item => string.Equals(item.AssetId, assetOverride.AssetId, StringComparison.Ordinal));
            overrides.Add(assetOverride);
            if (CurrentSession != null && bridge.IsAvailable)
            {
                bridge.ApplyAssetOverrides(overridesView);
            }
        }

        public void ClearAssetOverrides()
        {
            overrides.Clear();
            if (bridge.IsAvailable)
            {
                bridge.ClearAssetOverrides();
            }
        }

        /// <summary>Pumped from the mod's per-frame Update on the Unity main thread.</summary>
        public void Pump(float deltaTime)
        {
            if (sceneClaimReleasePending)
            {
                sceneClaimReleasePending = false;
                sceneClaim?.Dispose();
                sceneClaim = null;
                if (pendingSceneFailure.Length > 0)
                {
                    var failure = pendingSceneFailure;
                    pendingSceneFailure = string.Empty;
                    FailPendingStart("scene", failure);
                    return;
                }
            }

            RetryDeferredSceneLoad();
            if (sceneLoadDispatched && awaitingScene)
            {
                sceneDispatchRemaining -= Math.Max(0f, deltaTime);
                if (sceneDispatchRemaining <= 0f)
                {
                    FailPendingStart(
                        "load",
                        "The UGC play-scene load did not complete within "
                            + SceneDispatchTimeoutSeconds + " seconds.");
                    return;
                }
            }

            if (Status != UgcLiveSyncStatus.Watching)
            {
                return;
            }

            var process = false;
            lock (gate)
            {
                if (dirty)
                {
                    debounceRemaining -= deltaTime;
                    if (debounceRemaining <= 0f)
                    {
                        dirty = false;
                        process = true;
                    }
                }
            }

            if (process)
            {
                ProcessNewestSnapshot();
            }
        }

        /// <summary>Forwarded from the mod's SceneLoaded event so a pending local session can attach.</summary>
        public void NotifySceneLoaded(string sceneName)
        {
            var becameActive = bridge.IsActiveScene(sceneName);
            if (bridge.IsAvailable)
            {
                // Lets the Automerge channel confirm its live session once the play scene is up.
                bridge.NotifySceneLoaded(sceneName);
            }

            if (CurrentSession != null && becameActive && !bridge.IsImportControllerReady())
            {
                logger.Info("UGC live sync: active scene changed to '" + sceneName
                    + "' without a UGC import controller; stopping the live session.");
                Stop();
                return;
            }

            // A single-mode scene becoming active resolves our dispatch even when another user transition won;
            // unrelated additive scene notifications do not. The target name itself also resolves the claim.
            if (sceneClaim != null
                && (string.Equals(sceneName, bridge.PlaySceneName, StringComparison.OrdinalIgnoreCase)
                    || becameActive))
            {
                sceneClaimReleasePending = true;
            }

            if (awaitingScene && becameActive && !bridge.IsImportControllerReady())
            {
                sceneLoadDispatched = false;
                var targetArrived = string.Equals(
                    sceneName,
                    bridge.PlaySceneName,
                    StringComparison.OrdinalIgnoreCase);
                var automaticShouldKeepYielding = pendingPriority == SceneTransitionPriority.Automatic
                    && SceneCoordinator != null
                    && (!targetArrived || (sceneClaim == null && SceneCoordinator.IsSceneBusy));
                if (automaticShouldKeepYielding)
                {
                    // A later user transition won while our automatic load was in flight, or the request was
                    // already deferred and another owner brought up the target without a usable controller.
                    // Keep yielding and retry only after that owner's claim ends. Requiring a coordinator here
                    // avoids leaving an unowned automatic request pending forever on an unusable active scene.
                    deferredSceneLoad = true;
                    logger.Info("UGC live sync: deferred scene load was superseded by '" + sceneName
                        + "'; waiting to retry after the active scene owner releases its claim.");
                }
                else
                {
                    var message = targetArrived
                        ? "The UGC play scene loaded without a usable import/live controller."
                        : "The UGC play-scene load was superseded by active scene '" + sceneName + "'.";
                    if (sceneClaimReleasePending)
                    {
                        // Keep the claim visible through the rest of this SceneLoaded dispatch, then fail on Pump.
                        pendingSceneFailure = message;
                    }
                    else
                    {
                        FailPendingStart("scene", message);
                    }
                }

                return;
            }

            if (awaitingScene && pendingTransport == UgcSyncTransport.LocalFolder && bridge.IsImportControllerReady())
            {
                awaitingScene = false;
                deferredSceneLoad = false;
                sceneLoadDispatched = false;
                BeginLocalWatch();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private bool BeginLocalWatch()
        {
            bridge.ResetApplyState();
            bridge.ApplyAssetOverrides(overridesView);

            if (enableFileWatcher)
            {
                FileSystemWatcher? nextWatcher = null;
                try
                {
                    nextWatcher = new FileSystemWatcher(watchFolder)
                    {
                        // FileSystemWatcher wildcard casing follows the host file system. Watch all names and
                        // apply our own OrdinalIgnoreCase extension gate so .JSON/.JSON.GZ work on Unix too.
                        Filter = "*",
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                    };
                    nextWatcher.Created += OnFileEvent;
                    nextWatcher.Changed += OnFileEvent;
                    nextWatcher.Renamed += OnFileRenamed;
                    nextWatcher.EnableRaisingEvents = true;
                    watcher = nextWatcher;
                }
                catch (Exception ex)
                {
                    nextWatcher?.Dispose();
                    logger.Error(ex, "UGC live sync: could not watch '" + watchFolder + "'.");
                    Status = UgcLiveSyncStatus.Error;
                    pendingSession = null;
                    Raise(SyncError, new UgcSyncError("watch", ex.Message), "SyncError");
                    return false;
                }
            }

            CurrentSession = pendingSession
                ?? new UgcSyncSession(UgcSyncTransport.LocalFolder, watchFolder, sceneId, DateTime.UtcNow);
            pendingSession = null;

            // Seed the current content immediately (next pump processes the newest file).
            lock (gate)
            {
                dirty = true;
                debounceRemaining = 0f;
            }

            Status = UgcLiveSyncStatus.Watching;
            logger.Info("UGC live sync: watching " + watchFolder);
            Raise(SessionStarted, CurrentSession, "SessionStarted");
            return true;
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            if (IsSnapshotPath(e.FullPath))
            {
                MarkDirty();
            }
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            if (IsSnapshotPath(e.FullPath) || IsSnapshotPath(e.OldFullPath))
            {
                MarkDirty();
            }
        }

        // Internal so unit tests can simulate a file-change event when the OS watcher is disabled.
        internal void MarkDirty()
        {
            lock (gate)
            {
                dirty = true;
                debounceRemaining = debounceSeconds;
            }
        }

        private void ProcessNewestSnapshot()
        {
            var scan = ScanNewestSnapshot(watchFolder, MaxWatchDirectoryEntries);
            if (scan.Outcome == SnapshotScanOutcome.Empty)
            {
                return;
            }

            if (scan.Outcome == SnapshotScanOutcome.Retry)
            {
                logger.Debug("UGC live sync: watch-folder scan will retry: " + scan.Error);
                MarkDirty();
                return;
            }

            if (scan.Outcome == SnapshotScanOutcome.Rejected || scan.Path == null)
            {
                Reject("<watch folder>", scan.Error);
                return;
            }

            var path = scan.Path;

            var maxBytes = CurrentMaxBytes;
            var fileName = Path.GetFileName(path);
            try
            {
                if (IsReparsePoint(path))
                {
                    Reject(fileName, "symbolic links/reparse points are not accepted");
                    return;
                }

                if (IsSnapshotTooLarge(path, maxBytes, out var fileLength))
                {
                    // Reject from metadata before the bounded reader allocates a snapshot buffer. The stable
                    // read below repeats the bound while closing exporter growth/replacement races.
                    Reject(fileName, "file is " + fileLength + " bytes (limit " + maxBytes + ")");
                    return;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                Reject(fileName, "file metadata is inaccessible: " + ex.Message);
                return;
            }
            catch (IOException ex)
            {
                // The exporter may be rotating the snapshot between discovery and inspection.
                logger.Debug("UGC live sync: could not inspect '" + path + "' yet: " + ex.Message);
                MarkDirty();
                return;
            }
            catch (Exception ex)
            {
                Reject(fileName, "could not inspect file metadata: " + ex.Message);
                return;
            }

            var readOutcome = ReadStableSnapshot(path, maxBytes, out var bytes, out var readError);
            if (readOutcome == SnapshotReadOutcome.Retry)
            {
                logger.Debug("UGC live sync: could not read '" + path + "' yet: " + readError);
                MarkDirty();
                return;
            }
            if (readOutcome == SnapshotReadOutcome.Rejected)
            {
                Reject(fileName, readError);
                return;
            }

            if (maxBytes > 0 && bytes.LongLength > maxBytes)
            {
                Reject(fileName, "file is " + bytes.LongLength + " bytes (limit " + maxBytes + ")");
                return;
            }

            if (!UgcSnapshotPayloadValidator.TryValidate(bytes, maxBytes, out var expansionError))
            {
                Reject(fileName, expansionError);
                return;
            }

            try
            {
                var outcome = bridge.ApplyLocalSnapshot(bytes, sceneId, fileName);
                RaiseOutcome(outcome);
            }
            catch (Exception ex)
            {
                logger.Warn("UGC live sync: failed to apply '" + fileName + "': " + ex.Message);
                Status = UgcLiveSyncStatus.Watching;
                Raise(SyncError, new UgcSyncError("apply", ex.Message), "SyncError");
            }
        }

        private long currentMaxBytes = DefaultMaxSnapshotBytes;

        // Allows tests/config to cap snapshot size; the mod assigns this from UgcLiveSyncConfig. A malformed
        // non-positive config must never disable the allocation/expansion guard.
        public long CurrentMaxBytes
        {
            get => currentMaxBytes;
            set => currentMaxBytes = value > 0
                ? Math.Min(value, int.MaxValue)
                : DefaultMaxSnapshotBytes;
        }

        internal static bool IsSnapshotTooLarge(string path, long maxBytes, out long fileLength)
        {
            fileLength = new FileInfo(path).Length;
            return maxBytes > 0 && fileLength > maxBytes;
        }

        internal static bool IsReparsePoint(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        internal static SnapshotReadOutcome ReadStableSnapshot(
            string path,
            long maxBytes,
            out byte[] bytes,
            out string error)
        {
            return ReadStableSnapshot(path, maxBytes, null, out bytes, out error);
        }

        // The optional test hook deterministically simulates a same-size/same-timestamp replacement after the
        // first read. Production always passes null.
        internal static SnapshotReadOutcome ReadStableSnapshot(
            string path,
            long maxBytes,
            Action? afterInitialRead,
            out byte[] bytes,
            out string error)
        {
            bytes = Array.Empty<byte>();
            error = string.Empty;

            try
            {
                var before = new FileInfo(path);
                before.Refresh();
                if (!before.Exists)
                {
                    error = "file disappeared before it could be read";
                    return SnapshotReadOutcome.Retry;
                }

                if ((before.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    error = "snapshot must be a regular file (links/reparse points, directories, and devices are not accepted)";
                    return SnapshotReadOutcome.Rejected;
                }

                var initialLength = before.Length;
                var initialWriteTime = before.LastWriteTimeUtc;
                var initialCreationTime = before.CreationTimeUtc;
                if (maxBytes > 0 && initialLength > maxBytes)
                {
                    error = "file is " + initialLength + " bytes (limit " + maxBytes + ")";
                    return SnapshotReadOutcome.Rejected;
                }
                if (initialLength > int.MaxValue)
                {
                    error = "file is too large for the runtime snapshot buffer";
                    return SnapshotReadOutcome.Rejected;
                }

                byte[] buffer;
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan))
                {
                    if (stream.Length != initialLength)
                    {
                        error = "file size changed before the read began";
                        return SnapshotReadOutcome.Retry;
                    }

                    buffer = new byte[(int)initialLength];
                    var offset = 0;
                    while (offset < buffer.Length)
                    {
                        var count = stream.Read(buffer, offset, buffer.Length - offset);
                        if (count == 0)
                        {
                            error = "file was truncated while it was being read";
                            return SnapshotReadOutcome.Retry;
                        }

                        offset += count;
                    }

                    if (stream.ReadByte() != -1 || stream.Length != initialLength)
                    {
                        error = "file grew while it was being read";
                        return SnapshotReadOutcome.Retry;
                    }

                }

                afterInitialRead?.Invoke();

                var after = new FileInfo(path);
                after.Refresh();
                if (!after.Exists)
                {
                    error = "file was replaced while it was being read";
                    return SnapshotReadOutcome.Retry;
                }
                if ((after.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    error = "snapshot must be a regular file (links/reparse points, directories, and devices are not accepted)";
                    return SnapshotReadOutcome.Rejected;
                }
                if (after.Length != initialLength
                    || after.LastWriteTimeUtc != initialWriteTime
                    || after.CreationTimeUtc != initialCreationTime)
                {
                    error = "file metadata changed while it was being read";
                    return SnapshotReadOutcome.Retry;
                }

                // Metadata timestamps can be preserved across an atomic replacement. Reopen the selected path and
                // compare it to the bytes read from the original handle so that same-size replacement races are
                // detected on every platform without relying on inode/file-id APIs.
                using (var verification = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan))
                {
                    if (verification.Length != buffer.Length)
                    {
                        error = "file was replaced before verification";
                        return SnapshotReadOutcome.Retry;
                    }

                    var verifyBuffer = new byte[64 * 1024];
                    var offset = 0;
                    while (offset < buffer.Length)
                    {
                        var expected = Math.Min(verifyBuffer.Length, buffer.Length - offset);
                        var count = verification.Read(verifyBuffer, 0, expected);
                        if (count != expected)
                        {
                            error = "file changed during verification";
                            return SnapshotReadOutcome.Retry;
                        }

                        for (var index = 0; index < count; index++)
                        {
                            if (verifyBuffer[index] != buffer[offset + index])
                            {
                                error = "file content was replaced during the read";
                                return SnapshotReadOutcome.Retry;
                            }
                        }

                        offset += count;
                    }

                    if (verification.ReadByte() != -1)
                    {
                        error = "file grew during verification";
                        return SnapshotReadOutcome.Retry;
                    }
                }

                bytes = buffer;
                return SnapshotReadOutcome.Success;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "file is inaccessible: " + ex.Message;
                return SnapshotReadOutcome.Rejected;
            }
            catch (IOException ex)
            {
                error = ex.Message;
                return SnapshotReadOutcome.Retry;
            }
            catch (OutOfMemoryException)
            {
                error = "snapshot buffer allocation failed";
                return SnapshotReadOutcome.Rejected;
            }
            catch (Exception ex)
            {
                error = "could not read snapshot: " + ex.Message;
                return SnapshotReadOutcome.Rejected;
            }
        }

        internal static bool TryValidateExpandedSnapshot(byte[] bytes, long maxBytes, out string error)
        {
            return UgcSnapshotPayloadValidator.TryValidate(bytes, maxBytes, out error);
        }

        private void Reject(string fileName, string reason)
        {
            logger.Warn("UGC live sync: rejected snapshot (" + fileName + ": " + reason + ")");
            Raise(SyncError, new UgcSyncError("validate", fileName + ": " + reason), "SyncError");
        }

        private void RaiseRevision(int callbackGeneration, UgcApplyOutcome outcome)
        {
            if (callbackGeneration != Volatile.Read(ref automergeCallbackGeneration))
            {
                return;
            }

            if (CurrentSession == null)
            {
                if (!awaitingScene || pendingSession == null
                    || pendingSession.Transport != UgcSyncTransport.Automerge)
                {
                    return;
                }

                CurrentSession = pendingSession;
                pendingSession = null;
                awaitingScene = false;
                deferredSceneLoad = false;
                sceneLoadDispatched = false;
                Raise(SessionStarted, CurrentSession, "SessionStarted");
            }
            else if (CurrentSession.Transport != UgcSyncTransport.Automerge)
            {
                // A queued callback from an older live request must never turn a later local-folder session into
                // a Connected Automerge session. The generation guard normally catches this; keep the transport
                // check as a second invariant at the state boundary.
                return;
            }

            Status = UgcLiveSyncStatus.Connected;
            RaiseOutcome(outcome);
        }

        private void FailPendingStart(string phase, string message)
        {
            var stopAutomerge = pendingTransport == UgcSyncTransport.Automerge;
            InvalidateAutomergeCallbacks();
            awaitingScene = false;
            pendingSession = null;
            deferredSceneLoad = false;
            sceneLoadDispatched = false;
            sceneDispatchRemaining = 0f;
            pendingSceneFailure = string.Empty;
            pendingPriority = SceneTransitionPriority.UserInitiated;
            pendingAutomergeDocumentUrl = string.Empty;
            pendingAutomergeSyncServerUrl = string.Empty;
            pendingTransport = UgcSyncTransport.LocalFolder;
            var failedClaim = sceneClaim;
            sceneClaim = null;
            sceneClaimReleasePending = false;
            Status = UgcLiveSyncStatus.Error;

            try
            {
                failedClaim?.Dispose();
            }
            catch (Exception ex)
            {
                TryLogError(ex, "UGC live sync: failed to release the abandoned scene claim.");
            }

            if (stopAutomerge && bridge.IsAvailable)
            {
                try
                {
                    // Detach/destroy the native controller and overwrite its process-wide launch request. State
                    // above is already terminal, so even a synchronous or queued callback cannot reconnect it.
                    bridge.StopAutomerge();
                }
                catch (Exception ex)
                {
                    TryLogError(ex, "UGC live sync: failed to tear down the abandoned Automerge request.");
                }
            }

            TryLogWarning("UGC live sync: " + message);
            RaiseSyncErrorSafely(new UgcSyncError(phase, message));
        }

        private void RetryDeferredSceneLoad()
        {
            if (!deferredSceneLoad || !awaitingScene || pendingSession == null)
            {
                return;
            }

            // A deferred automatic request should not wait forever once the blocking session ends. Re-acquire
            // through the coordinator (rather than polling and loading directly) so a simultaneous claimant
            // still wins deterministically. Automerge is re-armed immediately before its scene load because
            // another UgcPlay launcher may have replaced the game's process-wide launch request meanwhile.
            var coordinator = SceneCoordinator;
            if (coordinator == null || coordinator.IsSceneBusy)
            {
                return;
            }

            var decision = coordinator.RequestTransition(new SceneTransitionRequest(
                SceneOwnerId,
                bridge.PlaySceneName,
                SceneTransitionPriority.Automatic,
                pendingTransport == UgcSyncTransport.Automerge
                    ? "resume deferred UGC live sync (Automerge)"
                    : "resume deferred UGC live sync"));
            if (!decision.Approved)
            {
                return;
            }

            sceneClaim?.Dispose();
            sceneClaim = decision.Claim;
            deferredSceneLoad = false;
            sceneLoadDispatched = false;
            sceneDispatchRemaining = 0f;
            pendingSceneFailure = string.Empty;

            var dispatched = pendingTransport == UgcSyncTransport.Automerge
                ? bridge.StartAutomerge(
                    pendingAutomergeDocumentUrl,
                    pendingAutomergeSyncServerUrl,
                    sceneId,
                    loadPlayScene: true,
                    CreateAutomergeRevisionCallback())
                : bridge.EnsurePlaySceneLoaded();
            if (!dispatched)
            {
                FailPendingStart("load", "Could not resume the deferred UGC play-scene load.");
                return;
            }

            BeginSceneDispatchTimeout();

            logger.Info("UGC live sync: blocker released; resumed the deferred play-scene load.");
        }

        private void BeginSceneDispatchTimeout()
        {
            sceneLoadDispatched = true;
            sceneDispatchRemaining = SceneDispatchTimeoutSeconds;
        }

        private Action<UgcApplyOutcome> CreateAutomergeRevisionCallback()
        {
            var generation = Interlocked.Increment(ref automergeCallbackGeneration);
            return outcome => RaiseRevision(generation, outcome);
        }

        private void InvalidateAutomergeCallbacks()
        {
            Interlocked.Increment(ref automergeCallbackGeneration);
        }

        private void RaiseSyncErrorSafely(UgcSyncError error)
        {
            var handlers = SyncError;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<UgcSyncError> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(error);
                }
                catch (Exception ex)
                {
                    TryLogError(ex, "UGC live sync: a SyncError subscriber failed.");
                }
            }
        }

        private void TryLogWarning(string message)
        {
            try
            {
                logger.Warn(message);
            }
            catch (Exception ex)
            {
                // Diagnostics must not change terminal lifecycle state.
                FallbackToConsole("UGC live sync warning logger failed ('" + ex.Message + "'): " + message);
            }
        }

        private void TryLogError(Exception exception, string message)
        {
            try
            {
                logger.Error(exception, message);
            }
            catch (Exception loggingFailure)
            {
                FallbackToConsole(
                    "UGC live sync error logger failed ('" + loggingFailure.Message + "') while reporting '"
                    + message + "': " + exception.Message);
            }
        }

        private static void FallbackToConsole(string message)
        {
            try
            {
                Console.Error.WriteLine(message);
            }
            catch
            {
                // No independent sink remains; terminal lifecycle state must still be preserved.
            }
        }

        private void RaiseOutcome(UgcApplyOutcome outcome)
        {
            var info = new UgcSnapshotInfo(
                outcome.ProjectName,
                outcome.SceneId,
                outcome.SceneName,
                outcome.EntityCount,
                outcome.WasFirstSnapshot ? "initial snapshot" : (outcome.IsFullRebuild ? "full rebuild" : "incremental patch"),
                outcome.IsFullRebuild,
                DateTime.UtcNow);

            if (outcome.WasFirstSnapshot)
            {
                Raise(SnapshotImported, info, "SnapshotImported");
            }
            else
            {
                Raise(PatchApplied, info, "PatchApplied");
            }
        }

        private void Raise<T>(Action<T>? handlers, T value, string eventName)
        {
            SafeEvent.Invoke(
                handlers,
                value,
                exception => logger.Warn("UGC live sync " + eventName + " subscriber failed: " + exception.Message));
        }

        private void DisposeWatcher()
        {
            if (watcher == null)
            {
                return;
            }

            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnFileEvent;
                watcher.Changed -= OnFileEvent;
                watcher.Renamed -= OnFileRenamed;
                watcher.Dispose();
            }
            catch (Exception ex)
            {
                logger.Debug("UGC live sync: error disposing watcher: " + ex.Message);
            }
            finally
            {
                watcher = null;
            }
        }

        internal static string? FindNewestSnapshot(string folder)
        {
            var result = ScanNewestSnapshot(folder, MaxWatchDirectoryEntries);
            return result.Outcome == SnapshotScanOutcome.Found ? result.Path : null;
        }

        internal static SnapshotScanResult ScanNewestSnapshot(string folder, int maximumEntries)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return new SnapshotScanResult(SnapshotScanOutcome.Empty, null, string.Empty);
            }

            if (maximumEntries <= 0)
            {
                return new SnapshotScanResult(
                    SnapshotScanOutcome.Rejected,
                    null,
                    "watch-folder entry limit is invalid");
            }

            string? newest = null;
            var newestTime = DateTime.MinValue;
            try
            {
                var folderAttributes = File.GetAttributes(folder);
                if ((folderAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    return new SnapshotScanResult(
                        SnapshotScanOutcome.Rejected,
                        null,
                        "watch folder must not be a symbolic link/reparse point");
                }

                var seen = 0;
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    seen++;
                    if (seen > maximumEntries)
                    {
                        return new SnapshotScanResult(
                            SnapshotScanOutcome.Rejected,
                            null,
                            "watch folder contains more than " + maximumEntries + " entries");
                    }

                    if (!IsSnapshotPath(entry))
                    {
                        continue;
                    }

                    var attributes = File.GetAttributes(entry);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        continue;
                    }

                    var writeTime = File.GetLastWriteTimeUtc(entry);
                    if (newest == null || writeTime > newestTime
                        || (writeTime == newestTime && string.CompareOrdinal(entry, newest) > 0))
                    {
                        newest = entry;
                        newestTime = writeTime;
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                return new SnapshotScanResult(
                    SnapshotScanOutcome.Rejected,
                    null,
                    "watch folder is inaccessible: " + ex.Message);
            }
            catch (IOException ex)
            {
                return new SnapshotScanResult(
                    SnapshotScanOutcome.Retry,
                    null,
                    "watch folder changed during enumeration: " + ex.Message);
            }
            catch (Exception ex)
            {
                return new SnapshotScanResult(
                    SnapshotScanOutcome.Rejected,
                    null,
                    "could not scan watch folder: " + ex.Message);
            }

            return newest == null
                ? new SnapshotScanResult(SnapshotScanOutcome.Empty, null, string.Empty)
                : new SnapshotScanResult(SnapshotScanOutcome.Found, newest, string.Empty);
        }

        internal static bool IsSnapshotPath(string path)
        {
            return !string.IsNullOrWhiteSpace(path)
                && (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase));
        }

        // Parses an editor share URL of the form https://host/?project=<doc>&scene=<id>.
        internal static bool TryParseEditorUrl(string input, out string documentUrl, out string sceneId)
        {
            documentUrl = string.Empty;
            sceneId = string.Empty;
            if (string.IsNullOrWhiteSpace(input) || HasInvalidPercentEncoding(input)
                || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            {
                return false;
            }

            try
            {
                var project = GetQueryParameter(uri, "project");
                if (string.IsNullOrWhiteSpace(project))
                {
                    return false;
                }

                documentUrl = project;
                sceneId = GetQueryParameter(uri, "scene") ?? string.Empty;
                return true;
            }
            catch (UriFormatException)
            {
                documentUrl = string.Empty;
                sceneId = string.Empty;
                return false;
            }
        }

        internal static bool TryValidateSecureSyncServerUrl(string input, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(input) || input.Length > 2048 || HasInvalidPercentEncoding(input)
                || !Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                error = "The Automerge sync server URL is invalid.";
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                error = "The Automerge sync server must use https:// or wss://.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "The Automerge sync server URL must have a host and cannot contain credentials or a fragment.";
                return false;
            }

            return true;
        }

        private static string? GetQueryParameter(Uri uri, string name)
        {
            var query = uri.Query;
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                if (pair.Length == 0)
                {
                    continue;
                }

                var eq = pair.IndexOf('=');
                var key = eq < 0 ? pair : pair.Substring(0, eq);
                if (string.Equals(Uri.UnescapeDataString(key.Replace('+', ' ')), name, StringComparison.OrdinalIgnoreCase))
                {
                    return eq < 0 ? string.Empty : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                }
            }

            return null;
        }

        private static bool HasInvalidPercentEncoding(string input)
        {
            for (var index = 0; index < input.Length; index++)
            {
                if (input[index] != '%')
                {
                    continue;
                }

                if (index + 2 >= input.Length || !IsHex(input[index + 1]) || !IsHex(input[index + 2]))
                {
                    return true;
                }

                index += 2;
            }

            return false;
        }

        private static bool IsHex(char value)
        {
            return value >= '0' && value <= '9'
                || value >= 'a' && value <= 'f'
                || value >= 'A' && value <= 'F';
        }
    }
}
