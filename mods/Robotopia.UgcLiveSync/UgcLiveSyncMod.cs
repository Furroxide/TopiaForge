using System;
using System.IO;
using Robotopia.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Robotopia.UgcLiveSync
{
    /// <summary>
    /// Entry point for the UGC live-sync framework mod. Publishes <see cref="IUgcLiveSyncService"/>, pumps the
    /// service on the Unity main thread, forwards scene loads, and surfaces the in-game overlay. Mirrors
    /// <c>Robotopia.Worlds.WorldsMod</c>'s lifecycle discipline: every handler subscribed in <see cref="OnLoad"/>
    /// is removed in <see cref="OnUnload"/> because C# assemblies never unload under Mono.
    /// </summary>
    public sealed class UgcLiveSyncMod : IRobotopiaMod
    {
        private const float AutoConnectMaxWaitSeconds = 12f;
        private const float CommandPollIntervalSeconds = 0.35f;

        private IModContext? context;
        private UgcLiveSyncConfig? config;
        private UgcLiveSyncService? service;
        private UgcLiveOverlay? overlay;
        private GameObject? overlayObject;
        private bool pendingAutoConnect;
        private float autoConnectWait;
        private readonly UgcCommandPollGate commandPoll = new UgcCommandPollGate(CommandPollIntervalSeconds);

        // Status handshake the launcher/CLI reads (game → launcher). Rewritten on every status transition.
        private string statusFilePath = string.Empty;
        private string commandFilePath = string.Empty;
        private UgcLiveSyncStatusFile? status;
        private UgcLiveSyncStatus lastWrittenStatus = UgcLiveSyncStatus.Idle;
        private string lastWrittenTarget = string.Empty;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            config = context.LoadConfig(new UgcLiveSyncConfig());
            context.SaveConfig(config);

            var bridge = new UgcGameBridge(context.Logger);
            service = new UgcLiveSyncService(bridge, context.Logger)
            {
                CurrentMaxBytes = config.MaxSnapshotBytes,
                // Scene-transition arbitration: play-scene loads yield to a live world/gamemode session
                // (an automatic connect defers and attaches when the play scene arrives on its own).
                SceneCoordinator = context.GetService<ISceneCoordinator>(),
                SceneOwnerId = context.ModId
            };

            statusFilePath = UgcLiveSyncStatusFile.PathForConfig(context.Paths.ConfigPath);
            commandFilePath = UgcLiveSyncCommandFile.PathForConfig(context.Paths.ConfigPath);
            status = new UgcLiveSyncStatusFile
            {
                DefaultWatchFolder = bridge.GetDefaultWatchFolder(),
                Transport = config.UsesAutomerge ? "automerge" : "localFolder",
                ModVersion = context.Version?.ToString() ?? string.Empty,
            };
            service.SnapshotImported += OnSnapshotApplied;
            service.PatchApplied += OnSnapshotApplied;

            context.GetService<IModServiceRegistry>()?.Register<IUgcLiveSyncService>(context.ModId, service);

            overlayObject = new GameObject("RobotopiaUgcLiveSync");
            UnityEngine.Object.DontDestroyOnLoad(overlayObject);
            overlay = overlayObject.AddComponent<UgcLiveOverlay>();
            overlay.Initialize(service, config, context);

            pendingAutoConnect = config.AutoConnectOnStart;
            autoConnectWait = AutoConnectMaxWaitSeconds;
            commandPoll.Reset();

            context.Update += OnUpdate;
            context.SceneLoaded += OnSceneLoaded;
            context.Logger.Info("Robotopia UgcLiveSync loaded (transport '" + config.Transport + "', auto-connect " + config.AutoConnectOnStart + ").");

            WriteStatus();
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.Update -= OnUpdate;
                context.SceneLoaded -= OnSceneLoaded;
                context.GetService<IModServiceRegistry>()?.UnregisterOwner(context.ModId);
            }

            if (service != null)
            {
                service.SnapshotImported -= OnSnapshotApplied;
                service.PatchApplied -= OnSnapshotApplied;
                service.Dispose();
                WriteStatus(); // capture the final Stopped state for the launcher.
            }

            if (overlayObject != null)
            {
                UnityEngine.Object.Destroy(overlayObject);
            }

            overlay = null;
            overlayObject = null;
            service = null;
            config = null;
            context = null;
            pendingAutoConnect = false;
        }

        private void OnUpdate(float deltaTime)
        {
            if (commandPoll.Tick(Time.unscaledDeltaTime))
            {
                HandleCommandFile();
            }

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
            if (service == null || config == null || status == null || string.IsNullOrEmpty(statusFilePath))
            {
                return;
            }

            status.Status = service.Status.ToString();
            status.Transport = config.UsesAutomerge ? "automerge" : "localFolder";
            status.ModVersion = context?.Version?.ToString() ?? status.ModVersion;
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

            try
            {
                status.WriteTo(statusFilePath);
            }
            catch (Exception ex)
            {
                context?.Logger.Debug("UGC live sync: could not write status file: " + ex.Message);
            }

            lastWrittenStatus = service.Status;
            lastWrittenTarget = session?.Target ?? string.Empty;
        }

        private void HandleCommandFile()
        {
            if (string.IsNullOrEmpty(commandFilePath) || !File.Exists(commandFilePath))
            {
                return;
            }

            UgcLiveSyncCommandFile command;
            try
            {
                command = UgcLiveSyncCommandFile.ReadFrom(commandFilePath);
            }
            catch (Exception ex)
            {
                context?.Logger.Warn("UGC live sync: ignoring malformed command file: " + ex.Message);
                TryDeleteCommandFile();
                return;
            }

            TryDeleteCommandFile();
            if (command.SchemaVersion != UgcLiveSyncCommandFile.CurrentSchemaVersion)
            {
                context?.Logger.Warn("UGC live sync: unsupported command schema version "
                    + command.SchemaVersion + ".");
                return;
            }

            if (!command.IsFresh(DateTime.UtcNow))
            {
                context?.Logger.Warn("UGC live sync: ignored a stale or invalidly dated command file.");
                return;
            }

            if (!command.IsStop)
            {
                context?.Logger.Warn("UGC live sync: unknown command '" + command.Command + "'.");
                return;
            }

            pendingAutoConnect = false;
            service?.Stop();
            if (command.Cleanup)
            {
                ClearRuntimeLiveConfig();
                status?.ClearLiveSession(clearHistory: true);
            }

            context?.Logger.Info("UGC live sync command: stopped active session"
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
            try
            {
                if (!string.IsNullOrEmpty(commandFilePath) && File.Exists(commandFilePath))
                {
                    File.Delete(commandFilePath);
                }
            }
            catch (Exception ex)
            {
                context?.Logger.Debug("UGC live sync: could not delete command file: " + ex.Message);
            }
        }

        // Holds the auto-connect until the menu scene is reached (a clean transition, not a race against boot),
        // with a timeout fallback. Mirrors WorldsMod.OnUpdate.
        private void TickAutoConnect(float deltaTime)
        {
            if (!pendingAutoConnect || service == null || config == null || context == null)
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

            // Automatic priority: this connect was not a direct user action, so a needed play-scene load
            // yields to whoever holds the scene (e.g. the Worlds auto-launcher, which fires on this same
            // "menu reached" trigger and runs earlier in the frame). The session still starts — it just
            // defers the scene load and attaches when the UGC play scene arrives on its own.
            var request = UgcLiveSyncRequest.WithPriority(
                SceneTransitionPriority.Automatic,
                watchFolder: config.WatchFolder,
                editorUrl: config.EditorUrl,
                documentUrl: config.DocumentUrl,
                syncServerUrl: config.SyncServerUrl,
                sceneId: config.SceneId,
                debounceMilliseconds: config.DebounceMilliseconds);

            var result = config.UsesAutomerge
                ? service.StartAutomergeSession(request)
                : service.StartLocalSession(request);

            if (result.Ok)
            {
                context.Logger.Info("UGC live sync auto-connect: " + result.Message);
            }
            else
            {
                context.Logger.Warn("UGC live sync auto-connect failed: " + result.Message);
            }
        }
    }
}
