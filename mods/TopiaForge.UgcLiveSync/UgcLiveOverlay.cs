using System;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.UgcLiveSync
{
    /// <summary>
    /// UGC live-sync control surface on the TopiaForgeUi kit: a corner pill with a status dot
    /// that expands into a draggable HUD-scheme window (position persists across
    /// restarts). The window holds the watch-folder and editor-URL sessions plus the
    /// live status/feed lines; the cursor lease rides the window's visibility.
    /// </summary>
    internal sealed class UgcLiveOverlay : MonoBehaviour
    {
        private IUgcLiveSyncService? service;
        private IModLogger? logger;
        private UiHost? ui;
        private TopiaForgeWindow? window;
        private TopiaForgeButton? pill;
        private TopiaForgeImage? pillDot;
        private TopiaForgeLabel? statusLabel;
        private TopiaForgeLabel? feedLabel;
        private TopiaForgeInputField? watchInput;
        private TopiaForgeInputField? editorInput;

        private string editorUrl = string.Empty;
        private string watchFolder = string.Empty;
        private string lastMessage = "Ready.";
        private UgcLiveSyncStatus? renderedStatus;
        private UgcSyncTransport? renderedTransport;
        private string renderedTarget = string.Empty;

        public void Initialize(IUgcLiveSyncService service, UgcLiveSyncConfig config, IModContext context)
        {
            this.service = service;
            logger = context.Logger;
            editorUrl = config.EditorUrl ?? string.Empty;
            watchFolder = config.WatchFolder ?? string.Empty;

            service.SessionStarted += OnSessionStarted;
            service.SnapshotImported += OnSnapshotImported;
            service.PatchApplied += OnPatchApplied;
            service.SyncError += OnSyncError;
            service.SessionStopped += OnSessionStopped;

            ui = TopiaForgeUi.For(context);
            BuildUi();
        }

        private void Update()
        {
            if (service == null)
            {
                return;
            }

            var isError = lastMessage.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
            if (pillDot != null && ui != null)
            {
                var theme = ui.Theme(TopiaForgeScheme.Hud);
                pillDot.SetColor(isError ? theme.Danger : service.CurrentSession != null ? theme.Success : theme.TextFaint);
            }

            if (window != null && window.IsOpen)
            {
                RenderStatus();
                if (feedLabel != null)
                {
                    feedLabel.SetText(lastMessage);
                    feedLabel.SetColor(ui!.Theme(TopiaForgeScheme.Hud).ToneColor(isError ? TopiaForgeTone.Danger : TopiaForgeTone.Muted));
                }
            }
        }

        private void OnDestroy()
        {
            if (service != null)
            {
                service.SessionStarted -= OnSessionStarted;
                service.SnapshotImported -= OnSnapshotImported;
                service.PatchApplied -= OnPatchApplied;
                service.SyncError -= OnSyncError;
                service.SessionStopped -= OnSessionStopped;
            }

            ui?.Dispose();
            ui = null;
            window = null;
            service = null;
        }

        private void BuildUi()
        {
            if (ui == null || window != null)
            {
                return;
            }

            // Collapsed pill (own tiny interactive HUD layer, docked top-left).
            var pillLayer = ui.Layer("livesync-pill", TopiaForgeLayerBand.Hud, TopiaForgeScheme.Hud, interactive: true);
            pillLayer.Go.name = "TopiaForgeUgcLiveSyncOverlay";
            pill = pillLayer.Button("UGC LIVE", () =>
            {
                window!.Show();
                pill!.SetVisible(false);
            }, TopiaForgeButtonStyle.Outline);
            pill.Dock(TopiaForgeCorner.TopLeft, 16f).Size(148f, 36f);
            pillDot = pillLayer.FreeImage("StatusDot").Sprite(TopiaForgeSprites.Circle());
            pillDot.Rect.anchorMin = new Vector2(0f, 1f);
            pillDot.Rect.anchorMax = new Vector2(0f, 1f);
            pillDot.Rect.pivot = new Vector2(0.5f, 0.5f);
            pillDot.Rect.anchoredPosition = new Vector2(160f, -22f);
            pillDot.Rect.sizeDelta = new Vector2(10f, 10f);

            // Expanded window (drag + persisted rect, HUD scheme).
            var firstRun = !ui.StateStore.TryRead("win:livesync", out _);
            window = ui.Window("livesync", "UGC LIVE SYNC", width: 470f, scheme: TopiaForgeScheme.Hud);
            if (firstRun)
            {
                // Default near the pill instead of screen center.
                window.Rect.anchoredPosition = new Vector2(-680f, 240f);
            }

            window.Closed += () => pill!.SetVisible(true);

            var content = window.Content;
            statusLabel = content.Label(string.Empty, TopiaForgeTextStyle.Label).Tone(TopiaForgeTone.Warning);

            content.SectionHeader("WATCH FOLDER");
            watchInput = content.Input("Local export folder", watchFolder, value => watchFolder = value);
            var watchRow = content.Row(TopiaForgeGap.Sm);
            watchRow.Button("START WATCHING", () => Run(() => service!.StartLocalSession(new UgcLiveSyncRequest(watchFolder: watchFolder))));

            content.SectionHeader("EDITOR / AUTOMERGE URL");
            editorInput = content.Input("Live editor or document URL", editorUrl, value => editorUrl = value);
            var editorRow = content.Row(TopiaForgeGap.Sm);
            editorRow.Button("CONNECT", () => Run(() => service!.StartAutomergeSession(new UgcLiveSyncRequest(editorUrl: editorUrl, documentUrl: editorUrl))), TopiaForgeButtonStyle.Outline);
            editorRow.Button("STOP", StopSession, TopiaForgeButtonStyle.Danger);

            feedLabel = content.Label(lastMessage, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
        }

        private void RenderStatus()
        {
            if (service == null || statusLabel == null)
            {
                return;
            }

            var session = service.CurrentSession;
            var transport = session?.Transport;
            var target = session?.Target ?? string.Empty;
            if (renderedStatus == service.Status
                && renderedTransport == transport
                && string.Equals(renderedTarget, target, StringComparison.Ordinal))
            {
                return;
            }

            renderedStatus = service.Status;
            renderedTransport = transport;
            renderedTarget = target;
            statusLabel.SetText(session == null
                ? "STATUS  " + service.Status
                : "STATUS  " + service.Status + "  //  " + session.Transport + " -> " + target);
        }

        private void OnSessionStarted(UgcSyncSession session) =>
            lastMessage = "Session started: " + session.Transport + " -> " + session.Target;

        private void OnSnapshotImported(UgcSnapshotInfo info) =>
            lastMessage = "Imported '" + info.SceneName + "' (" + info.EntityCount + " entities)";

        private void OnPatchApplied(UgcSnapshotInfo info) =>
            lastMessage = (info.IsFullRebuild ? "Full rebuild: " : "Patched: ")
                + info.SceneName + " (" + info.EntityCount + ")";

        private void OnSyncError(UgcSyncError error) =>
            lastMessage = "Error (" + error.Phase + "): " + error.Message;

        private void OnSessionStopped(UgcSyncSession session) => lastMessage = "Session stopped.";

        private void StopSession()
        {
            service?.Stop();
            lastMessage = "Stopped.";
        }

        private void Run(Func<UgcLiveSyncResult> action)
        {
            try
            {
                lastMessage = action().Message;
            }
            catch (Exception ex)
            {
                lastMessage = ex.Message;
                logger?.Warn("UGC live sync action failed: " + ex.Message);
            }
        }
    }
}
