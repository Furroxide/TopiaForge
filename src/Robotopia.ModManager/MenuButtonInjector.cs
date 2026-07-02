using System;
using System.Linq;
using Robotopia.Mods.UnityUi;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Robotopia.ModManager
{
    internal sealed class MenuButtonInjector
    {
        // The verified main-menu scene. Used as a cheap gate so the component scan and the broad canvas
        // fallback only run in the menu — never in gameplay (which prevents false injection into HUD canvases).
        private const string MenuSceneName = "TestCityStartMenu";

        private readonly ManagerOverlay overlay;
        private readonly ManagerFileLogger logger;
        private UiHost? host;
        private string sceneName = string.Empty;
        private float nextAttemptTime;

        public MenuButtonInjector(ManagerOverlay overlay, ManagerFileLogger logger)
        {
            this.overlay = overlay;
            this.logger = logger;
        }

        public void ResetForScene(string newSceneName)
        {
            sceneName = newSceneName;
            nextAttemptTime = 0f;
        }

        public void Update()
        {
            // No permanent "done" latch: if the menu canvas is rebuilt within the scene, the throttled retry +
            // per-button presence check re-injects. Gate on the live active scene (robust even if the menu's
            // sceneLoaded event was missed because it was the startup scene) so gameplay is never scanned.
            if (Time.unscaledTime < nextAttemptTime)
            {
                return;
            }

            nextAttemptTime = Time.unscaledTime + 1f;

            if (!IsMenuScene())
            {
                return;
            }

            TryInject();
        }

        private bool IsMenuScene()
        {
            return string.Equals(sceneName, MenuSceneName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(SceneManager.GetActiveScene().name, MenuSceneName, StringComparison.OrdinalIgnoreCase);
        }

        private void TryInject()
        {
            try
            {
                var canvas = FindMenuCanvas();
                if (canvas == null)
                {
                    return;
                }

                QwEventSystems.EnsureEventSystem();
                EnsureButton(canvas, "RobotopiaGamemodesMenuButton", "GAMEMODES", () => overlay.ShowGamemodes(), 74f);
                EnsureButton(canvas, "RobotopiaModManagerMenuButton", "QUANTUMWORKS", () => overlay.Show(), 24f);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to inject menu buttons.");
            }
        }

        private void EnsureButton(Canvas canvas, string name, string label, Action onClick, float bottomOffset)
        {
            if (canvas.transform.Find(name) != null)
            {
                return;
            }

            // Kit widgets render fine under the game's own canvas: wrap it in a container.
            host ??= QwUi.Create(new QwUiOptions
            {
                OwnerId = "robotopia.modmanager.menu",
                LogInfo = logger.Info,
                LogWarn = logger.Warn,
                LogError = message => logger.Error(message),
            });
            var parent = new QwContainer(host, QwScheme.Paper, canvas.gameObject);
            var button = parent.Button(label, onClick, QwButtonStyle.Filled);
            button.Go.name = name;

            var rect = button.Rect;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(24f, bottomOffset);
            rect.sizeDelta = new Vector2(190f, 42f);
            logger.Info("Injected '" + label + "' menu button into scene '" + sceneName + "'.");
        }

        private static Canvas? FindMenuCanvas()
        {
            // The game's LevelSelectController marks the menu. Its own panel may be inactive on the landing
            // screen, so we do NOT require the controller to be active — only that it resolves to an active
            // scene Canvas (a prefab-asset instance does not). This is gated to the menu scene by the caller.
            var levelSelect = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .FirstOrDefault(m => m != null && m.GetType().Name == "LevelSelectController");
            if (levelSelect != null)
            {
                var canvas = levelSelect.GetComponentInParent<Canvas>();
                if (canvas != null && canvas.gameObject.activeInHierarchy)
                {
                    return canvas;
                }
            }

            // Fallback (menu scene only): the first active canvas with at least two text buttons.
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(c => c != null && c.gameObject.activeInHierarchy && c.name != "RobotopiaModManagerOverlay")
                .ToArray();

            foreach (var canvas in canvases)
            {
                var buttons = canvas.GetComponentsInChildren<Button>(true);
                if (buttons.Length >= 2 && buttons.Any(b => b.GetComponentInChildren<Text>() != null))
                {
                    return canvas;
                }
            }

            return null;
        }
    }
}
