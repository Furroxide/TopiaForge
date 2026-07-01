using System;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.UgcLiveSync
{
    internal sealed class UgcLiveOverlay : MonoBehaviour
    {
        private IUgcLiveSyncService? service;
        private IModLogger? logger;
        private readonly NeonCursorLease cursorLease = new NeonCursorLease();
        private GameObject? canvasRoot;
        private GameObject? collapsedRoot;
        private GameObject? expandedRoot;
        private Text? statusText;
        private Text? feedText;
        private InputField? watchInput;
        private InputField? editorInput;

        private bool expanded;
        private string editorUrl = string.Empty;
        private string watchFolder = string.Empty;
        private string lastMessage = "Ready.";

        public void Initialize(IUgcLiveSyncService service, UgcLiveSyncConfig config, IModLogger logger)
        {
            this.service = service;
            this.logger = logger;
            editorUrl = config.EditorUrl ?? string.Empty;
            watchFolder = config.WatchFolder ?? string.Empty;

            service.SessionStarted += s => lastMessage = "Session started: " + s.Transport + " -> " + s.Target;
            service.SnapshotImported += i => lastMessage = "Imported '" + i.SceneName + "' (" + i.EntityCount + " entities)";
            service.PatchApplied += i => lastMessage = (i.IsFullRebuild ? "Full rebuild: " : "Patched: ") + i.SceneName + " (" + i.EntityCount + ")";
            service.SyncError += e => lastMessage = "Error (" + e.Phase + "): " + e.Message;
            service.SessionStopped += _ => lastMessage = "Session stopped.";
            BuildUi();
        }

        private void Update()
        {
            if (service == null || canvasRoot == null)
            {
                return;
            }

            collapsedRoot?.SetActive(!expanded);
            expandedRoot?.SetActive(expanded);
            cursorLease.SetActive(expanded);

            if (statusText != null)
            {
                statusText.text = "STATUS  " + service.Status + SessionSuffix();
            }

            if (feedText != null)
            {
                feedText.text = lastMessage;
                feedText.color = lastMessage.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ? NeonTheme.Danger : NeonTheme.TextMuted;
            }
        }

        private void OnDestroy()
        {
            if (canvasRoot != null)
            {
                UnityEngine.Object.Destroy(canvasRoot);
                canvasRoot = null;
            }

            cursorLease.Release();
        }

        private void BuildUi()
        {
            if (canvasRoot != null)
            {
                return;
            }

            canvasRoot = NeonUi.CreateOverlayCanvas("RobotopiaUgcLiveSyncOverlay", 31800, false);
            canvasRoot.transform.SetParent(transform, false);

            collapsedRoot = NeonUi.CreateButton(canvasRoot.transform, "Collapsed", "UGC LIVE", () => SetExpanded(true), new Vector2(148f, 36f), NeonTheme.Panel, NeonTheme.Cyan).gameObject;
            NeonUi.Anchor(collapsedRoot.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -16f), new Vector2(148f, 36f));

            expandedRoot = NeonUi.CreatePanel(canvasRoot.transform, "Expanded", NeonTheme.Panel, NeonTheme.Cyan);
            NeonUi.Anchor(expandedRoot.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -16f), new Vector2(460f, 342f));

            var title = NeonUi.CreateText(expandedRoot.transform, "Title", "UGC LIVE SYNC", 20, NeonTheme.Cyan, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(title.rectTransform, 18f, 16f, 260f, 28f);
            var close = NeonUi.CreateButton(expandedRoot.transform, "Close", "CLOSE", () => SetExpanded(false), new Vector2(84f, 32f), NeonTheme.PanelAlt, NeonTheme.Danger);
            Place(close.GetComponent<RectTransform>(), 356f, 14f, 84f, 32f);

            statusText = NeonUi.CreateText(expandedRoot.transform, "Status", string.Empty, 14, NeonTheme.Amber, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(statusText.rectTransform, 18f, 54f, 422f, 24f);

            var watchLabel = NeonUi.CreateText(expandedRoot.transform, "WatchLabel", "WATCH FOLDER", 13, NeonTheme.TextMuted, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(watchLabel.rectTransform, 18f, 92f, 180f, 22f);
            watchInput = NeonUi.CreateInput(expandedRoot.transform, "WatchInput", "Local export folder", watchFolder, value => watchFolder = value);
            Place(watchInput.GetComponent<RectTransform>(), 18f, 116f, 422f, 36f);
            var watchButton = NeonUi.CreateButton(expandedRoot.transform, "StartWatch", "START WATCHING", () => Run(() => service!.StartLocalSession(new UgcLiveSyncRequest(watchFolder: watchFolder))), new Vector2(176f, 34f), NeonTheme.PanelAlt, NeonTheme.Acid);
            Place(watchButton.GetComponent<RectTransform>(), 18f, 160f, 176f, 34f);

            var editorLabel = NeonUi.CreateText(expandedRoot.transform, "EditorLabel", "EDITOR / AUTOMERGE URL", 13, NeonTheme.TextMuted, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(editorLabel.rectTransform, 18f, 212f, 240f, 22f);
            editorInput = NeonUi.CreateInput(expandedRoot.transform, "EditorInput", "Live editor or document URL", editorUrl, value => editorUrl = value);
            Place(editorInput.GetComponent<RectTransform>(), 18f, 236f, 422f, 36f);
            var connectButton = NeonUi.CreateButton(expandedRoot.transform, "Connect", "CONNECT", () => Run(() => service!.StartAutomergeSession(new UgcLiveSyncRequest(editorUrl: editorUrl, documentUrl: editorUrl))), new Vector2(126f, 34f), NeonTheme.PanelAlt, NeonTheme.Cyan);
            Place(connectButton.GetComponent<RectTransform>(), 18f, 280f, 126f, 34f);
            var stopButton = NeonUi.CreateButton(expandedRoot.transform, "Stop", "STOP", StopSession, new Vector2(92f, 34f), new Color(0.16f, 0.06f, 0.07f, 0.95f), NeonTheme.Danger);
            Place(stopButton.GetComponent<RectTransform>(), 154f, 280f, 92f, 34f);

            feedText = NeonUi.CreateText(expandedRoot.transform, "Feed", lastMessage, 13, NeonTheme.TextMuted, TextAnchor.MiddleLeft, FontStyle.Bold);
            Place(feedText.rectTransform, 258f, 278f, 182f, 42f);
            expandedRoot.SetActive(false);
        }

        private string SessionSuffix()
        {
            var session = service?.CurrentSession;
            return session == null ? string.Empty : "  //  " + session.Transport + " -> " + session.Target;
        }

        private void SetExpanded(bool value)
        {
            expanded = value;
            cursorLease.SetActive(expanded);
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

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
