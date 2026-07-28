using System;
using System.Text;
using System.Threading.Tasks;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.UgcLiveSync
{
    /// <summary>
    /// Entry point for the UGC live-sync framework mod. Publishes <see cref="IUgcLiveSyncService"/>, pumps the
    /// service on the Unity main thread, forwards scene loads, and surfaces the in-game overlay. Mirrors
    /// <c>TopiaForge.Worlds.WorldsMod</c>'s lifecycle discipline: every handler subscribed in <see cref="OnLoad"/>
    /// is removed in <see cref="OnUnload"/> because C# assemblies never unload under Mono.
    /// </summary>
    public sealed class UgcLiveSyncMod : TopiaForgeMod
    {
        private const float AutoConnectMaxWaitSeconds = 12f;
        private const float CommandPollIntervalSeconds = 0.35f;
        private const string StatusFileName = "topiaforge.ugc.livesync.status.json";
        private const string CommandFileName = "topiaforge.ugc.livesync.command.json";
        // UgcLiveSyncConfig is ISelfNormalizingConfig, so the launcher-, CLI-, and hand-written document is
        // bounded by the config service before the service or the overlay reads it.
        private static readonly ConfigDefinition<UgcLiveSyncConfig> ConfigContract =
            new ConfigDefinition<UgcLiveSyncConfig>(1, () => new UgcLiveSyncConfig());

        private UgcLiveSyncConfig? config;
        private UgcLiveSyncService? service;
        private UgcLiveOverlay? overlay;
        private GameObject? overlayObject;
        private IUgcSyncLease? autoConnectLease;
        private bool pendingAutoConnect;
        private float autoConnectWait;
        private readonly UgcCommandPollGate commandPoll = new UgcCommandPollGate(CommandPollIntervalSeconds);

        // Status handshake the launcher/CLI reads (game → launcher). The files are owner-scoped SDK data,
        // so the provider never receives or exposes host filesystem paths.
        private UgcLiveSyncStatusFile? status;
        private UgcLiveSyncStatus lastWrittenStatus = UgcLiveSyncStatus.Idle;
        private string lastWrittenTarget = string.Empty;
        private Task<OperationResult<string>>? commandRead;
        private Task<OperationResult<bool>>? statusWrite;
        private string? queuedStatusJson;

        protected override void OnLoad()
        {
            var loaded = Context.Config.Load(ConfigContract);
            config = loaded.TryGetValue(out var value) ? value : new UgcLiveSyncConfig();
            Context.Config.Save(ConfigContract, config);

            var bridge = new UgcGameBridge(Context.Logger);
            service = new UgcLiveSyncService(bridge, Context.Logger)
            {
                CurrentMaxBytes = config.MaxSnapshotBytes
            };

            status = new UgcLiveSyncStatusFile
            {
                DefaultWatchFolder = bridge.GetDefaultWatchFolder(),
                Transport = config.UsesAutomerge ? "automerge" : "localFolder",
                ModVersion = Context.Identity.Version.ToString(),
            };
            service.SnapshotImported += OnSnapshotApplied;
            service.PatchApplied += OnSnapshotApplied;
            Context.Lifetime.Defer(() => service.SnapshotImported -= OnSnapshotApplied);
            Context.Lifetime.Defer(() => service.PatchApplied -= OnSnapshotApplied);
            Context.Lifetime.Track(service);

            var registration = Context.Extensions.Register<IUgcLiveSyncService>(service);
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(registration.ErrorMessage);
            }

            overlayObject = new GameObject("TopiaForgeUgcLiveSync");
            UnityEngine.Object.DontDestroyOnLoad(overlayObject);
            overlay = overlayObject.AddComponent<UgcLiveOverlay>();
            overlay.Initialize(service, config, Context);
            Context.Lifetime.Defer(() =>
            {
                if (overlayObject != null)
                {
                    UnityEngine.Object.Destroy(overlayObject);
                    overlayObject = null;
                }
            });

            pendingAutoConnect = config.AutoConnectOnStart;
            autoConnectWait = AutoConnectMaxWaitSeconds;
            commandPoll.Reset();

            Context.Events.SubscribeUpdate(OnUpdate);
            Context.Events.SubscribeSceneLoaded(OnSceneLoaded);
            Context.Logger.Info("TopiaForge UgcLiveSync loaded (transport '" + config.Transport + "', auto-connect " + config.AutoConnectOnStart + ").");

            WriteStatus();
        }

        protected override void OnUnload()
        {
            if (service != null)
            {
                service.Stop();
                WriteStatus(); // capture the final Stopped state for the launcher.
            }

            autoConnectLease?.Dispose();
            autoConnectLease = null;
            overlay = null;
            service = null;
            config = null;
            pendingAutoConnect = false;
        }

        private void OnUpdate(float deltaTime)
        {
            if (commandPoll.Tick(Time.unscaledDeltaTime))
            {
                BeginCommandRead();
            }

            CompleteCommandRead();
            CompleteStatusWrite();
            service?.Pump(deltaTime);
            TickAutoConnect(deltaTime);
            MaybeWriteStatus();
        }

        private void OnSceneLoaded(string sceneName)
        {
            service?.NotifySceneLoaded(sceneName);
        }

        private void OnSnapshotApplied(UgcSnapshotInfo info)
        {
            if (status == null)
            {
                return;
            }

            status.LastAppliedUtc = info.AppliedAtUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(info.SceneId))
            {
                status.SceneId = info.SceneId;
                status.AddScene(info.SceneId);
            }

            WriteStatus();
        }

        // Rewrites the status file only when the service's status or live target actually changed, so the file
        // tracks real transitions rather than churning every frame.
        private void MaybeWriteStatus()
        {
            if (service == null)
            {
                return;
            }

            var target = (service.CurrentSession ?? service.PendingSession)?.Target ?? string.Empty;
            if (service.Status != lastWrittenStatus
                || !string.Equals(target, lastWrittenTarget, StringComparison.Ordinal))
            {
                WriteStatus();
            }
        }

        private void WriteStatus()
        {
            if (service == null || config == null || status == null)
            {
                return;
            }

            status.Status = service.Status.ToString();
            status.Transport = config.UsesAutomerge ? "automerge" : "localFolder";
            status.ModVersion = Context.Identity.Version.ToString();
            status.UpdatedUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

            var session = service.CurrentSession ?? service.PendingSession;
            if (session != null)
            {
                // Assign both mutually-exclusive targets so a direct transport switch cannot leave stale
                // connection data in the launcher handshake when no intermediate idle status was written.
                status.ConnectedDocumentUrl = session.Transport == UgcSyncTransport.Automerge
                    ? session.Target
                    : string.Empty;
                status.WatchFolder = session.Transport == UgcSyncTransport.LocalFolder
                    ? session.Target
                    : string.Empty;
                status.SceneId = session.SceneId;
            }
            else
            {
                status.ClearLiveSession(clearHistory: false);
            }

            queuedStatusJson = status.ToJson();
            StartStatusWrite();

            lastWrittenStatus = service.Status;
            lastWrittenTarget = session?.Target ?? string.Empty;
        }

        private void StartStatusWrite()
        {
            if (statusWrite != null || queuedStatusJson == null)
            {
                return;
            }

            var json = queuedStatusJson;
            queuedStatusJson = null;
            statusWrite = Context.Files.WriteDataTextAsync(
                StatusFileName,
                json,
                Context.Lifetime.StoppingToken);
        }

        private void CompleteStatusWrite()
        {
            if (statusWrite == null || !statusWrite.IsCompleted)
            {
                return;
            }

            OperationResult<bool>? result = null;
            try
            {
                result = statusWrite.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Context.Logger.Debug("UGC live sync: status write failed: " + ex.Message);
            }

            statusWrite = null;
            if (result != null && !result.Succeeded && result.ErrorCode != ModErrorCode.Cancelled)
            {
                Context.Logger.Debug("UGC live sync: status write failed: " + result.ErrorMessage);
            }

            StartStatusWrite();
        }

        private void BeginCommandRead()
        {
            if (commandRead != null || !Context.Files.DataFileExists(CommandFileName))
            {
                return;
            }

            commandRead = Context.Files.ReadDataTextAsync(
                CommandFileName,
                Context.Lifetime.StoppingToken);
        }

        private void CompleteCommandRead()
        {
            if (commandRead == null || !commandRead.IsCompleted)
            {
                return;
            }

            OperationResult<string> result;
            try
            {
                result = commandRead.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                commandRead = null;
                Context.Logger.Warn("UGC live sync: could not read command file: " + ex.Message);
                return;
            }

            commandRead = null;
            if (!result.TryGetValue(out var json))
            {
                if (result.ErrorCode != ModErrorCode.NotFound && result.ErrorCode != ModErrorCode.Cancelled)
                {
                    Context.Logger.Warn("UGC live sync: could not read command file: " + result.ErrorMessage);
                }

                return;
            }

            UgcLiveSyncCommandFile command;
            try
            {
                if (Encoding.UTF8.GetByteCount(json) > UgcLiveSyncCommandFile.MaxFileBytes)
                {
                    throw new InvalidOperationException(
                        "Command exceeds " + UgcLiveSyncCommandFile.MaxFileBytes + " bytes.");
                }

                command = UgcLiveSyncCommandFile.FromJson(json);
            }
            catch (Exception ex)
            {
                Context.Logger.Warn("UGC live sync: ignoring malformed command file: " + ex.Message);
                TryDeleteCommandFile();
                return;
            }

            TryDeleteCommandFile();
            if (!command.IsFresh(DateTime.UtcNow))
            {
                Context.Logger.Warn("UGC live sync: ignored a stale or invalidly dated command file.");
                return;
            }

            if (!command.IsStop)
            {
                Context.Logger.Warn("UGC live sync: unknown command '" + command.Command + "'.");
                return;
            }

            pendingAutoConnect = false;
            autoConnectLease?.Dispose();
            autoConnectLease = null;
            service?.Stop();
            if (command.Cleanup)
            {
                ClearRuntimeLiveConfig();
                status?.ClearLiveSession(clearHistory: true);
            }

            Context.Logger.Info("UGC live sync command: stopped active session"
                + (command.Cleanup ? " and cleared live connection state." : "."));
            WriteStatus();
        }

        private void ClearRuntimeLiveConfig()
        {
            if (config == null)
            {
                return;
            }

            config.AutoConnectOnStart = false;
            config.EditorUrl = string.Empty;
            config.DocumentUrl = string.Empty;
            // The launcher/CLI writes the cleaned durable config before publishing this command. Do not save
            // this stale startup snapshot back over that file: watch folder, scene, limits, or future fields may
            // have changed while the game was running. These assignments only stop in-process reconnect state.
        }

        private void TryDeleteCommandFile()
        {
            _ = Context.Files.DeleteDataFileAsync(
                CommandFileName,
                Context.Lifetime.StoppingToken);
        }

        // Holds the auto-connect until the menu scene is reached (a clean transition, not a race against boot),
        // with a timeout fallback. Mirrors WorldsMod.OnUpdate.
        private void TickAutoConnect(float deltaTime)
        {
            if (!pendingAutoConnect || service == null || config == null)
            {
                return;
            }

            autoConnectWait -= deltaTime;
            var activeScene = SceneManager.GetActiveScene().name;
            var atMenu = GameScenes.IsMainMenuScene(activeScene);
            if (!atMenu && autoConnectWait > 0f)
            {
                return;
            }

            pendingAutoConnect = false;

            // Mark this as an automatic launch so diagnostics and future scene-policy adapters can distinguish
            // startup behavior from an explicit in-game action.
            var request = new UgcLiveSyncRequest(
                WorldLoadPriority.Automatic,
                watchFolder: config.WatchFolder,
                editorUrl: config.EditorUrl,
                documentUrl: config.DocumentUrl,
                syncServerUrl: config.SyncServerUrl,
                sceneId: config.SceneId,
                debounceMilliseconds: config.DebounceMilliseconds);

            var result = config.UsesAutomerge
                ? service.StartAutomergeSession(request)
                : service.StartLocalSession(request);

            if (result.TryGetValue(out var lease))
            {
                autoConnectLease?.Dispose();
                autoConnectLease = lease;
                Context.Logger.Info("UGC live sync auto-connect requested for '" + lease.Session.Target + "'.");
            }
            else
            {
                Context.Logger.Warn("UGC live sync auto-connect failed: " + result.ErrorMessage);
            }
        }
    }
}
