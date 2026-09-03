using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Puts the manager's GAMEMODES and TOPIAFORGE buttons on the game's main menu.
    /// <para>
    /// The buttons live on the kit's own band-allocated canvas rather than being parented onto one of
    /// the game's canvases. That is the whole fix for their previous absence: Robotopia's main menu is
    /// UI Toolkit (<c>StartMenuApp</c> on ClockworkLabs Rish) and its scene contains no uGUI canvas to
    /// parent onto, so the old lookup could never succeed on any build of this game. Owning the canvas
    /// also means the buttons keep the kit's scaler and sorting order instead of inheriting whatever
    /// the game happened to configure. See <see cref="MenuSurfaceCensus"/>.
    /// </para>
    /// </summary>
    internal sealed class MenuButtonInjector : IDisposable
    {
        // Retry cadence while the menu scene is active. Backed off once a failure has been reported so a
        // build we genuinely cannot mount on does not spin a scan every second forever.
        private const float RetrySeconds = 1f;
        private const float RetrySecondsAfterWarning = 5f;

        private readonly ManagerOverlay overlay;
        private readonly ManagerFileLogger logger;
        private readonly List<TopiaForgeButton> injectedButtons = new List<TopiaForgeButton>();
        private UiHost? host;
        private TopiaForgeContainer? bar;
        private string sceneName = string.Empty;
        private float nextAttemptTime;
        private int attempts;
        private bool mounted;
        private bool censusLogged;
        private bool warningLogged;

        public MenuButtonInjector(ManagerOverlay overlay, ManagerFileLogger logger)
        {
            this.overlay = overlay;
            this.logger = logger;
        }

        /// <summary>
        /// Why the menu buttons are missing, or empty while they are present (or before the player has
        /// reached the menu). Kept so callers can surface the condition without re-deriving it.
        /// </summary>
        public string MountFailure { get; private set; } = string.Empty;

        public void ResetForScene(string newSceneName)
        {
            ClearInjectedUi();
            sceneName = newSceneName;
            nextAttemptTime = 0f;
            attempts = 0;
            mounted = false;
            censusLogged = false;
            warningLogged = false;
            MountFailure = string.Empty;
        }

        public void Dispose()
        {
            ClearInjectedUi();
        }

        public void Update()
        {
            // Gate on the live active scene as well as the tracked one, so the menu is still handled when
            // it is the startup scene and its sceneLoaded event was never delivered. Keeping the whole
            // pass behind the menu gate is what keeps it out of gameplay.
            if (Time.unscaledTime < nextAttemptTime)
            {
                return;
            }

            nextAttemptTime = Time.unscaledTime + (warningLogged ? RetrySecondsAfterWarning : RetrySeconds);

            if (!IsMenuScene())
            {
                return;
            }

            // No permanent "done" latch: if our canvas is torn down within the scene, the throttled retry
            // and the per-button presence check below rebuild it.
            if (mounted && HasLiveButtons())
            {
                return;
            }

            attempts++;
            TryMount();
        }

        private bool IsMenuScene()
        {
            return GameScenes.IsMainMenuScene(sceneName)
                || GameScenes.IsMainMenuScene(SceneManager.GetActiveScene().name);
        }

        private bool HasLiveButtons()
        {
            if (injectedButtons.Count == 0)
            {
                return false;
            }

            foreach (var button in injectedButtons)
            {
                if (button.Go == null)
                {
                    return false;
                }
            }

            return true;
        }

        private void TryMount()
        {
            try
            {
                ClearInjectedUi();

                host ??= TopiaForgeUi.Create(new TopiaForgeUiOptions
                {
                    OwnerId = "io.github.furroxide.topiaforge.modmanager.menu",
                    LogInfo = logger.Info,
                    LogWarn = logger.Warn,
                    LogError = message => logger.Error(message),
                });

                // Persistent, so Unity does not destroy the canvas on scene unload and strand its
                // sorting-order slot; ResetForScene owns the teardown instead.
                bar = host.Layer(
                    "menu-bar",
                    TopiaForgeLayerBand.Hud,
                    TopiaForgeScheme.Paper,
                    interactive: true,
                    persistent: true);

                AddButton("TopiaForgeGamemodesMenuButton", "GAMEMODES", () => overlay.ShowGamemodes(), 74f);
                AddButton("TopiaForgeModManagerMenuButton", "TOPIAFORGE", () => overlay.Show(), 24f);

                mounted = true;
                MountFailure = string.Empty;
                LogCensusOnce();
            }
            catch (Exception ex)
            {
                mounted = false;
                logger.Error(ex, "Failed to mount the main-menu buttons.");
            }

            if (!mounted)
            {
                ReportMountFailure();
            }
        }

        private void AddButton(string name, string label, Action onClick, float bottomOffset)
        {
            var button = bar!.Button(label, onClick, TopiaForgeButtonStyle.Filled);
            injectedButtons.Add(button);
            button.Go.name = name;

            var rect = button.Rect;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(24f, bottomOffset);
            rect.sizeDelta = new Vector2(190f, 42f);
            logger.Info("Injected '" + label + "' menu button into scene '" + sceneName + "'.");
        }

        /// <summary>
        /// Records what the menu actually looked like, once per visit. The previous implementation logged
        /// nothing at all when it could not find a home for its buttons, which is why a game whose menu
        /// had no uGUI canvas produced a silently mod-less main menu and no diagnostic to explain it.
        /// </summary>
        private void LogCensusOnce()
        {
            if (censusLogged)
            {
                return;
            }

            censusLogged = true;
            logger.Info(MenuSurfaceCensus.Describe(
                sceneName,
                mounted,
                attempts,
                CurrentSortingOrder(),
                CollectSurfaces()));
        }

        private void ReportMountFailure()
        {
            if (!MenuSurfaceCensus.ShouldWarn(attempts, mounted, warningLogged))
            {
                return;
            }

            warningLogged = true;
            MountFailure = "The TopiaForge menu buttons could not be mounted in scene '" + sceneName
                + "' after " + attempts + " attempts. Press F10 to open the manager overlay.";
            logger.Warn(MountFailure + " " + MenuSurfaceCensus.Describe(
                sceneName,
                mounted: false,
                attempts,
                CurrentSortingOrder(),
                CollectSurfaces()));
            censusLogged = true;

            // Never strand the player: the overlay is on its own canvas and needs nothing from the game,
            // so it still opens when everything else about the menu is unrecognisable.
            overlay.ShowGamemodes();
        }

        private int CurrentSortingOrder()
        {
            var canvas = bar?.Go == null ? null : bar.Go.GetComponentInParent<Canvas>();
            return canvas == null ? 0 : canvas.sortingOrder;
        }

        /// <summary>
        /// Describes the game's own menu surfaces for the log. Diagnostics only — nothing here decides
        /// whether the buttons mount — so both probes fail soft.
        /// </summary>
        private static List<MenuSurfaceCandidate> CollectSurfaces()
        {
            var surfaces = new List<MenuSurfaceCandidate>();
            CollectCanvases(surfaces);
            CollectUiToolkitPanels(surfaces);
            return surfaces;
        }

        private static void CollectCanvases(List<MenuSurfaceCandidate> surfaces)
        {
            try
            {
                foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
                {
                    if (canvas == null
                        || !canvas.gameObject.activeInHierarchy
                        || MenuSurfaceCensus.IsTopiaForgeOwned(canvas.name))
                    {
                        continue;
                    }

                    surfaces.Add(new MenuSurfaceCandidate(
                        MenuSurfaceCandidate.UguiCanvasKind,
                        canvas.name,
                        canvas.sortingOrder,
                        canvas.GetComponentsInChildren<Selectable>(true).Length));
                }
            }
            catch (Exception)
            {
                // A census is never worth failing over.
            }
        }

        /// <summary>
        /// Counts UI Toolkit runtime panels by reflection, so the loader needs no UIElements reference
        /// and degrades to reporting none if a future build drops the module.
        /// </summary>
        private static void CollectUiToolkitPanels(List<MenuSurfaceCandidate> surfaces)
        {
            try
            {
                var documentType = Type.GetType(
                    "UnityEngine.UIElements.UIDocument, UnityEngine.UIElementsModule",
                    throwOnError: false);
                if (documentType == null)
                {
                    return;
                }

                var sortingOrderProperty = documentType.GetProperty("sortingOrder");
                foreach (var document in Resources.FindObjectsOfTypeAll(documentType).OfType<Component>())
                {
                    if (document == null || !document.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    var order = 0;
                    if (sortingOrderProperty != null
                        && sortingOrderProperty.GetValue(document, null) is float value)
                    {
                        order = (int)value;
                    }

                    surfaces.Add(new MenuSurfaceCandidate(
                        MenuSurfaceCandidate.UiToolkitPanelKind,
                        document.gameObject.name,
                        order,
                        0));
                }
            }
            catch (Exception)
            {
                // Reflection into an engine module is best-effort by construction.
            }
        }

        private void ClearInjectedUi()
        {
            for (var index = injectedButtons.Count - 1; index >= 0; index--)
            {
                injectedButtons[index].Destroy();
            }

            injectedButtons.Clear();
            bar = null;
            host?.Dispose();
            host = null;
        }
    }
}
