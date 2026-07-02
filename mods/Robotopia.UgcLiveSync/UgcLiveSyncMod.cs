using System;
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
        private const string MenuSceneName = "TestCityStartMenu";
        private const float AutoConnectMaxWaitSeconds = 12f;

        private IModContext? context;
        private UgcLiveSyncConfig? config;
        private UgcLiveSyncService? service;
        private UgcLiveOverlay? overlay;
        private GameObject? overlayObject;
        private bool pendingAutoConnect;
        private float autoConnectWait;

        // Status handshake the launcher/CLI reads (game → launcher). Rewritten on every status transition.
        private string statusFilePath = string.Empty;
        private UgcLiveSyncStatusFile? status;
        private UgcLiveSyncStatus lastWrittenStatus = UgcLiveSyncStatus.Idle;
        private string lastWrittenTarget = string.Empty;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            config = context.LoadConfig(new UgcLiveSyncConfig());
            context.SaveConfig(config);

            var bridge = new UgcGameBridge(context.Logger);
            service = new UgcLiveSyncService(bridge, context.Logger) { CurrentMaxBytes = config.MaxSnapshotBytes };

            statusFilePath = UgcLiveSyncStatusFile.PathForConfig(context.Paths.ConfigPath);
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

            var target = service.CurrentSession?.Target ?? string.Empty;
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

            var session = service.CurrentSession;
            if (session != null)
            {
                if (session.Transport == UgcSyncTransport.Automerge)
                {
                    status.ConnectedDocumentUrl = session.Target;
                }
                else
                {
                    status.WatchFolder = session.Target;
                }

                if (!string.IsNullOrEmpty(session.SceneId))
                {
                    status.SceneId = session.SceneId;
                }
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
            var atMenu = string.Equals(activeScene, MenuSceneName, StringComparison.OrdinalIgnoreCase);
            if (!atMenu && autoConnectWait > 0f)
            {
                return;
            }

            pendingAutoConnect = false;

            var request = new UgcLiveSyncRequest(
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
