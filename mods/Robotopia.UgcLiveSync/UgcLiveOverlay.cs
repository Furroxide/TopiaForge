using System;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.UgcLiveSync
{
    /// <summary>
    /// UGC live-sync control surface on the QwUi kit: a corner pill with a status dot
    /// that expands into a draggable HUD-scheme window (position persists across
    /// restarts). The window holds the watch-folder and editor-URL sessions plus the
    /// live status/feed lines; the cursor lease rides the window's visibility.
    /// </summary>
    internal sealed class UgcLiveOverlay : MonoBehaviour
    {
        private IUgcLiveSyncService? service;
        private IModLogger? logger;
        private UiHost? ui;
        private QwWindow? window;
        private QwButton? pill;
        private QwImage? pillDot;
        private QwLabel? statusLabel;
        private QwLabel? feedLabel;
        private QwInputField? watchInput;
        private QwInputField? editorInput;

        private string editorUrl = string.Empty;
        private string watchFolder = string.Empty;
        private string lastMessage = "Ready.";

        public void Initialize(IUgcLiveSyncService service, UgcLiveSyncConfig config, IModContext context)
        {
            this.service = service;
            logger = context.Logger;
            editorUrl = config.EditorUrl ?? string.Empty;
            watchFolder = config.WatchFolder ?? string.Empty;

            service.SessionStarted += s => lastMessage = "Session started: " + s.Transport + " -> " + s.Target;
            service.SnapshotImported += i => lastMessage = "Imported '" + i.SceneName + "' (" + i.EntityCount + " entities)";
            service.PatchApplied += i => lastMessage = (i.IsFullRebuild ? "Full rebuild: " : "Patched: ") + i.SceneName + " (" + i.EntityCount + ")";
            service.SyncError += e => lastMessage = "Error (" + e.Phase + "): " + e.Message;
            service.SessionStopped += _ => lastMessage = "Session stopped.";

            ui = QwUi.For(context);
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
                var theme = ui.Theme(QwScheme.Hud);
                pillDot.SetColor(isError ? theme.Danger : service.CurrentSession != null ? theme.Success : theme.TextFaint);
            }

            if (window != null && window.IsOpen)
            {
                statusLabel?.SetText("STATUS  " + service.Status + SessionSuffix());
                if (feedLabel != null)
                {
                    feedLabel.SetText(lastMessage);
                    feedLabel.SetColor(ui!.Theme(QwScheme.Hud).ToneColor(isError ? QwTone.Danger : QwTone.Muted));
                }
            }
        }

        private void OnDestroy()
        {
            ui?.Dispose();
            ui = null;
            window = null;
        }

        private void BuildUi()
        {
            if (ui == null || window != null)
            {
                return;
            }

            // Collapsed pill (own tiny interactive HUD layer, docked top-left).
            var pillLayer = ui.Layer("livesync-pill", QwLayerBand.Hud, QwScheme.Hud, interactive: true);
            pillLayer.Go.name = "RobotopiaUgcLiveSyncOverlay";
            pill = pillLayer.Button("UGC LIVE", () =>
            {
                window!.Show();
                pill!.SetVisible(false);
            }, QwButtonStyle.Outline);
            pill.Dock(QwCorner.TopLeft, 16f).Size(148f, 36f);
            pillDot = pillLayer.FreeImage("StatusDot").Sprite(QwSprites.Circle());
            pillDot.Rect.anchorMin = new Vector2(0f, 1f);
            pillDot.Rect.anchorMax = new Vector2(0f, 1f);
            pillDot.Rect.pivot = new Vector2(0.5f, 0.5f);
            pillDot.Rect.anchoredPosition = new Vector2(160f, -22f);
            pillDot.Rect.sizeDelta = new Vector2(10f, 10f);

            // Expanded window (drag + persisted rect, HUD scheme).
            var firstRun = !ui.StateStore.TryRead("win:livesync", out _);
            window = ui.Window("livesync", "UGC LIVE SYNC", width: 470f, scheme: QwScheme.Hud);
            if (firstRun)
            {
                // Default near the pill instead of screen center.
                window.Rect.anchoredPosition = new Vector2(-680f, 240f);
            }

            window.Closed += () => pill!.SetVisible(true);

            var content = window.Content;
            statusLabel = content.Label(string.Empty, QwTextStyle.Label).Tone(QwTone.Warning);

            content.SectionHeader("WATCH FOLDER");
            watchInput = content.Input("Local export folder", watchFolder, value => watchFolder = value);
            var watchRow = content.Row(QwGap.Sm);
            watchRow.Button("START WATCHING", () => Run(() => service!.StartLocalSession(new UgcLiveSyncRequest(watchFolder: watchFolder))));

            content.SectionHeader("EDITOR / AUTOMERGE URL");
            editorInput = content.Input("Live editor or document URL", editorUrl, value => editorUrl = value);
            var editorRow = content.Row(QwGap.Sm);
            editorRow.Button("CONNECT", () => Run(() => service!.StartAutomergeSession(new UgcLiveSyncRequest(editorUrl: editorUrl, documentUrl: editorUrl))), QwButtonStyle.Outline);
            editorRow.Button("STOP", StopSession, QwButtonStyle.Danger);

            feedLabel = content.Label(lastMessage, QwTextStyle.Caption).Tone(QwTone.Muted);
        }

        private string SessionSuffix()
        {
            var session = service?.CurrentSession;
            return session == null ? string.Empty : "  //  " + session.Transport + " -> " + session.Target;
        }

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
