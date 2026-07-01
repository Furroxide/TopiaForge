using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using Robotopia.Mods;

namespace Robotopia.UgcLiveSync
{
    /// <summary>
    /// Unity-free implementation of <see cref="IUgcLiveSyncService"/>. All game/Unity work is delegated to an
    /// <see cref="IUgcLiveSyncBridge"/> so this state machine (debounce, validation, first-vs-subsequent
    /// snapshots, lifecycle) can be unit-tested on plain .NET. The owning mod pumps <see cref="Pump"/> from the
    /// per-frame Update event (Unity main thread) and forwards scene loads to <see cref="NotifySceneLoaded"/>.
    /// </summary>
    internal sealed class UgcLiveSyncService : IUgcLiveSyncService, IDisposable
    {
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

        public event Action<UgcSyncSession>? SessionStarted;
        public event Action<UgcSnapshotInfo>? SnapshotImported;
        public event Action<UgcSnapshotInfo>? PatchApplied;
        public event Action<UgcSyncError>? SyncError;
        public event Action<UgcSyncSession>? SessionStopped;

        public UgcLiveSyncResult StartLocalSession(UgcLiveSyncRequest request)
        {
            if (!bridge.IsAvailable)
            {
                Status = UgcLiveSyncStatus.Unavailable;
                return UgcLiveSyncResult.Fail("UGC live sync is unavailable in this build (game symbols not found).");
            }

            Stop();

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

            watchFolder = folder;
            sceneId = request.SceneId ?? string.Empty;
            debounceSeconds = Math.Max(0f, request.DebounceMilliseconds / 1000f);

            if (bridge.IsImportControllerReady())
            {
                BeginLocalWatch();
                return UgcLiveSyncResult.Success(CurrentSession!, "Watching '" + watchFolder + "' for UGC snapshots.");
            }

            // No UGC play scene yet: load it (content import suppressed) and attach once it is ready.
            pendingTransport = UgcSyncTransport.LocalFolder;
            awaitingScene = true;
            Status = UgcLiveSyncStatus.WaitingForScene;
            bridge.EnsurePlaySceneLoaded();
            logger.Info("UGC live sync: waiting for the UGC play scene before watching '" + watchFolder + "'.");
            return UgcLiveSyncResult.Success(
                new UgcSyncSession(UgcSyncTransport.LocalFolder, watchFolder, sceneId, DateTime.UtcNow),
                "Loading UGC play scene, then watching '" + watchFolder + "'.");
        }

        public UgcLiveSyncResult StartAutomergeSession(UgcLiveSyncRequest request)
        {
            if (!bridge.IsAvailable)
            {
                Status = UgcLiveSyncStatus.Unavailable;
                return UgcLiveSyncResult.Fail("UGC live sync is unavailable in this build (game symbols not found).");
            }

            Stop();

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

            var syncServer = string.IsNullOrWhiteSpace(request.SyncServerUrl)
                ? UgcLiveSyncConfig.DefaultSyncServerUrl
                : request.SyncServerUrl;

            sceneId = resolvedScene;
            if (!bridge.StartAutomerge(documentUrl, syncServer, resolvedScene, RaiseRevision))
            {
                Status = UgcLiveSyncStatus.Error;
                return UgcLiveSyncResult.Fail("Could not start the Automerge live session (see log).");
            }

            CurrentSession = new UgcSyncSession(UgcSyncTransport.Automerge, documentUrl, resolvedScene, DateTime.UtcNow);
            Status = UgcLiveSyncStatus.Connected;
            logger.Info("UGC live sync: connecting to live Automerge document '" + documentUrl + "'.");
            SessionStarted?.Invoke(CurrentSession);
            return UgcLiveSyncResult.Success(CurrentSession, "Connecting to live Automerge document.");
        }

        public void Stop()
        {
            DisposeWatcher();
            awaitingScene = false;

            if (bridge.IsAvailable)
            {
                bridge.StopAutomerge();
                bridge.ClearAssetOverrides();
            }

            var session = CurrentSession;
            CurrentSession = null;
            lock (gate)
            {
                dirty = false;
                debounceRemaining = 0f;
            }

            if (session != null)
            {
                Status = UgcLiveSyncStatus.Stopped;
                SessionStopped?.Invoke(session);
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
            if (bridge.IsAvailable)
            {
                // Lets the Automerge channel confirm its live session once the play scene is up.
                bridge.NotifySceneLoaded(sceneName);
            }

            if (awaitingScene && pendingTransport == UgcSyncTransport.LocalFolder && bridge.IsImportControllerReady())
            {
                awaitingScene = false;
                BeginLocalWatch();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void BeginLocalWatch()
        {
            bridge.ResetApplyState();
            bridge.ApplyAssetOverrides(overridesView);

            CurrentSession = new UgcSyncSession(UgcSyncTransport.LocalFolder, watchFolder, sceneId, DateTime.UtcNow);

            if (enableFileWatcher)
            {
                try
                {
                    watcher = new FileSystemWatcher(watchFolder)
                    {
                        Filter = "*.json*",
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                    };
                    watcher.Created += OnFileEvent;
                    watcher.Changed += OnFileEvent;
                    watcher.Renamed += OnFileRenamed;
                    watcher.EnableRaisingEvents = true;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "UGC live sync: could not watch '" + watchFolder + "'.");
                    Status = UgcLiveSyncStatus.Error;
                    SyncError?.Invoke(new UgcSyncError("watch", ex.Message));
                    return;
                }
            }

            // Seed the current content immediately (next pump processes the newest file).
            lock (gate)
            {
                dirty = true;
                debounceRemaining = 0f;
            }

            Status = UgcLiveSyncStatus.Watching;
            logger.Info("UGC live sync: watching " + watchFolder);
            SessionStarted?.Invoke(CurrentSession);
        }

        private void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            MarkDirty();
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            MarkDirty();
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
            var path = FindNewestSnapshot(watchFolder);
            if (path == null)
            {
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                // The exporter may still be writing; a later event will retry.
                logger.Debug("UGC live sync: could not read '" + path + "' yet: " + ex.Message);
                return;
            }

            var maxBytes = CurrentMaxBytes;
            if (maxBytes > 0 && bytes.LongLength > maxBytes)
            {
                Reject(Path.GetFileName(path), "file is " + bytes.LongLength + " bytes (limit " + maxBytes + ")");
                return;
            }

            if (!LooksLikeProjectJson(bytes))
            {
                Reject(Path.GetFileName(path), "not JSON or gzip-JSON content");
                return;
            }

            try
            {
                var outcome = bridge.ApplyLocalSnapshot(bytes, sceneId, Path.GetFileName(path));
                RaiseOutcome(outcome);
            }
            catch (Exception ex)
            {
                logger.Warn("UGC live sync: failed to apply '" + Path.GetFileName(path) + "': " + ex.Message);
                Status = UgcLiveSyncStatus.Watching;
                SyncError?.Invoke(new UgcSyncError("apply", ex.Message));
            }
        }

        // Allows tests/config to cap snapshot size; the mod assigns this from UgcLiveSyncConfig.
        public long CurrentMaxBytes { get; set; } = 16L * 1024 * 1024;

        private void Reject(string fileName, string reason)
        {
            logger.Warn("UGC live sync: rejected snapshot (" + fileName + ": " + reason + ")");
            SyncError?.Invoke(new UgcSyncError("validate", fileName + ": " + reason));
        }

        private void RaiseRevision(UgcApplyOutcome outcome)
        {
            Status = UgcLiveSyncStatus.Connected;
            RaiseOutcome(outcome);
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
                SnapshotImported?.Invoke(info);
            }
            else
            {
                PatchApplied?.Invoke(info);
            }
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
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return null;
            }

            string? newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime writeTime;
                try
                {
                    writeTime = File.GetLastWriteTimeUtc(file);
                }
                catch
                {
                    continue;
                }

                if (newest == null || writeTime > newestTime)
                {
                    newest = file;
                    newestTime = writeTime;
                }
            }

            return newest;
        }

        // True when the bytes plausibly contain a UGC export project: gzip magic, or UTF-8 text whose first
        // non-whitespace character (after an optional BOM) is '{'. A cheap guard so malformed files are rejected
        // before reaching the game importer; the importer still does the authoritative parse.
        internal static bool LooksLikeProjectJson(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return false;
            }

            if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                return true; // gzip
            }

            var index = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                index = 3; // UTF-8 BOM
            }

            while (index < bytes.Length)
            {
                var b = bytes[index];
                if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n')
                {
                    index++;
                    continue;
                }

                return b == (byte)'{';
            }

            return false;
        }

        // Parses an editor share URL of the form https://host/?project=<doc>&scene=<id>.
        internal static bool TryParseEditorUrl(string input, out string documentUrl, out string sceneId)
        {
            documentUrl = string.Empty;
            sceneId = string.Empty;
            if (string.IsNullOrWhiteSpace(input) || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri))
            {
                return false;
            }

            var project = GetQueryParameter(uri, "project");
            if (string.IsNullOrWhiteSpace(project))
            {
                return false;
            }

            documentUrl = project;
            sceneId = GetQueryParameter(uri, "scene") ?? string.Empty;
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
    }
}
