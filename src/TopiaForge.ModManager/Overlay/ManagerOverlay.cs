using System;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The F10 / main-menu manager overlay, rebuilt on the TopiaForgeUi kit: Paper-scheme
    /// full-screen card with a nav rail, per-tab content inside scroll views, toasts
    /// for action results, a destructive-confirm modal for removal, ESC-close via the
    /// kit dismiss stack, and cursor handling via the kit lease.
    /// Tabs are found by name (<see cref="ManagerTabLookup"/>), never by their place in the rail.
    /// The multiplayer presenter ticks every frame, overlay open or not, so a join is followed while the player plays.
    /// </summary>
    internal sealed class ManagerOverlay : ITopiaForgeDismissable
    {
        private readonly TopiaForgeModManagerPlugin plugin;
        private readonly ManagerFileLogger logger;
        private readonly ManagerUiState uiState = new ManagerUiState();
        private readonly TopiaForgeCursorLease cursorLease = new TopiaForgeCursorLease();
        private readonly MultiplayerSessionsPresenter multiplayer;
        private readonly MultiplayerFrameGuard multiplayerGuard;
        private readonly MultiplayerOverlayFrame multiplayerFrame;
        private UiHost? host;
        private GameObject? root;
        private TopiaForgePanel? shell;
        private TopiaForgeTabs? nav;
        private TopiaForgeLabel? statusLabel;
        private TopiaForgeContainer? content;
        private MultiplayerSessionsUi? multiplayerUi;
        private IManagerTab[] tabs = Array.Empty<IManagerTab>();
        private string[] titles = Array.Empty<string>();
        private int selectedTab;
        private bool connectingShown;
        private string status = "Trusted local mode: install only packages you explicitly trust. C# mods execute code.";

        public ManagerOverlay(TopiaForgeModManagerPlugin plugin, ManagerFileLogger logger)
        {
            this.plugin = plugin;
            this.logger = logger;

            // Nothing the multiplayer interface throws may reach Unity's frame, where the game's crash reporter takes it:
            // its stages run through this guard, which logs each failure once, redacted, and lets the frame run on.
            multiplayerGuard = new MultiplayerFrameGuard(message => logger.Error(message));
            multiplayer = new MultiplayerSessionsPresenter(
                new PluginMultiplayerEnvironment(plugin, multiplayerGuard),
                new FileMultiplayerSettingsStore(plugin.Paths.Data),
                MultiplayerWorkSchedulers.Background);
            multiplayerFrame = new MultiplayerOverlayFrame(multiplayerGuard, multiplayer, TickMultiplayerOutside, TickLiveTab);
            multiplayerGuard.Run(MultiplayerOverlayFrame.StartStage, multiplayer.Start);
        }

        /// <summary>Gets whether the multiplayer provider is loaded, so the main menu offers MULTIPLAYER.</summary>
        public bool HasMultiplayer => plugin.Multiplayer?.Control != null;

        TopiaForgeLayerBand ITopiaForgeDismissable.Band => TopiaForgeLayerBand.Window;

        void ITopiaForgeDismissable.Dismiss()
        {
            Close();
        }

        public void Toggle()
        {
            if (root == null)
            {
                Build();
            }

            SetOpen(!root!.activeSelf);
            if (root.activeSelf)
            {
                RefreshContent();
            }
        }

        public void Show()
        {
            OpenAt(ManagerTabLookup.Installed);
        }

        public void ShowGamemodes()
        {
            OpenAt(ManagerTabLookup.Gamemodes);
        }

        public void ShowMultiplayer()
        {
            OpenAt(ManagerTabLookup.Multiplayer);
        }

        public void Tick()
        {
            // ESC and cursor are handled by the kit (dismiss stack + per-frame lease). The multiplayer stages run through
            // their guard, so none of them can break this frame or the plugin's.
            multiplayerFrame.Tick(Time.unscaledDeltaTime);
        }

        public void Dispose()
        {
            multiplayerGuard.Run(MultiplayerOverlayFrame.TeardownStage, () =>
            {
                multiplayerUi?.Close();
                multiplayer.OnHidden();
                multiplayer.Stop();
            });
            cursorLease.Release();
            TopiaForgeDismissStack.Remove(this);
            host?.Dispose();
            host = null;
            root = null;
        }

        private void OpenAt(string tabName)
        {
            if (root == null)
            {
                Build();
            }

            var tabIndex = ManagerTabLookup.IndexOf(titles, tabName);
            if (tabIndex < 0) tabIndex = 0;
            if (tabIndex != selectedTab) HideLiveTab();
            selectedTab = tabIndex;
            nav?.Select(tabIndex);
            SetOpen(true);
            RefreshContent();
        }

        /// <summary>
        /// Shows the multiplayer prompts, toasts and connecting overlay. Before the overlay is built only the toasts show, until
        /// a question or a join needs the screen, as a session the launcher asked for does before the player opened anything:
        /// the overlay is then built, closed, for its prompts and its connecting overlay.
        /// </summary>
        private void TickMultiplayerOutside()
        {
            if (multiplayerUi == null && (multiplayer.PendingPrompt != null || multiplayer.Connecting.IsVisible)) Build();
            if (multiplayerUi != null) multiplayerUi.Tick();
            else MultiplayerSessionsUi.ShowNotices(multiplayer);
        }

        /// <summary>Hands the screen to a join's connecting overlay once it shows, and ticks the live tab while the overlay shows.</summary>
        private void TickLiveTab()
        {
            var connecting = multiplayer.Connecting.IsVisible;
            if (connecting && !connectingShown && root != null && root.activeSelf) Close();
            connectingShown = connecting;
            if (root != null && root.activeSelf && tabs[selectedTab] is IManagerLiveTab live) live.Tick();
        }

        /// <summary>Tells a live tab it no longer shows, as when the overlay closes or another tab is chosen.</summary>
        private void HideLiveTab()
        {
            if (selectedTab < tabs.Length && tabs[selectedTab] is IManagerLiveTab live) live.OnHidden();
        }

        private void Build()
        {
            host = TopiaForgeUi.Create(new TopiaForgeUiOptions
            {
                OwnerId = "io.github.furroxide.topiaforge.modmanager",
                DataDirectory = plugin.Paths.Data,
                LogInfo = logger.Info,
                LogWarn = logger.Warn,
                LogError = message => logger.Error(message),
            });

            var layer = host.Layer("overlay", TopiaForgeLayerBand.Window, TopiaForgeScheme.Paper, interactive: true, persistent: true);
            root = layer.Go;
            // The menu-button injector's canvas scan excludes this exact name.
            root.name = "TopiaForgeModManagerOverlay";

            var dim = layer.FreeImage("Dim");
            dim.Stretch();
            dim.SetColor(host.Theme(TopiaForgeScheme.Paper).Backdrop);
            dim.Image.raycastTarget = true;

            shell = layer.Panel(TopiaForgePanelStyle.Card);
            shell.Rect.anchorMin = new Vector2(0.07f, 0.08f);
            shell.Rect.anchorMax = new Vector2(0.93f, 0.92f);
            shell.Rect.offsetMin = Vector2.zero;
            shell.Rect.offsetMax = Vector2.zero;

            var body = shell.Row(TopiaForgeGap.None, TopiaForgeGap.None, expandChildWidth: false);
            body.Stretch();

            // ---- nav rail ----
            var railPanel = body.Panel(TopiaForgePanelStyle.Sunken);
            railPanel.FixedWidth(230f);
            railPanel.Flex(0f, 1f);
            var rail = railPanel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md);
            rail.Stretch();
            rail.Label("TOPIAFORGE", TopiaForgeTextStyle.Title).Tone(TopiaForgeTone.Primary).FixedHeight(42f);
            tabs = new IManagerTab[]
            {
                new GamemodesTab(),
                new MultiplayerTab(multiplayer, multiplayerGuard),
                new InstalledTab(uiState),
                new PackagesTab(uiState),
                new SettingsTab(),
                new LogsTab(),
            };
            titles = new string[tabs.Length];
            for (var index = 0; index < tabs.Length; index++)
            {
                titles[index] = tabs[index].Title;
            }

            ManagerTabLookup.RequireDistinct(titles);
            nav = rail.NavRail(titles);
            nav.OnSelected(index =>
            {
                if (index != selectedTab) HideLiveTab();
                selectedTab = index;
                RefreshContent();
            });
            rail.Spacer();
            rail.Button("CLOSE", Close, TopiaForgeButtonStyle.Outline).FixedHeight(TopiaForgeTokens.ControlHeight);

            // ---- main area ----
            var main = body.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md);
            main.Flex(1f, 1f);
            statusLabel = main.Label(status, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Warning);
            statusLabel.FixedHeight(30f);
            var contentPanel = main.Panel(TopiaForgePanelStyle.Sunken);
            contentPanel.Flex(1f, 1f);
            content = contentPanel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md);
            content.Stretch();
            multiplayerUi = new MultiplayerSessionsUi(host, multiplayer);

            root.SetActive(false);
        }

        private void RefreshContent()
        {
            if (content == null || host == null)
            {
                return;
            }

            // TopiaForgeUi retains widgets for theme refresh/tween cancellation. Clear through the host so a tab rebuild
            // cannot leave destroyed widgets, callbacks, or tweens registered for the rest of the process.
            host.Clear(content);

            statusLabel?.SetText(status);
            var context = new ManagerTabContext(plugin, host, RunAction, SetStatus, RefreshContent, Close);
            tabs[selectedTab].Build(content, context);
        }

        private void SetStatus(string message)
        {
            status = message;
            statusLabel?.SetText(message);
        }

        private void RunAction(Func<string> action)
        {
            try
            {
                var message = action();
                SetStatus(message);
                TopiaForgeToasts.Show(message, TopiaForgeTone.Success);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Mod manager UI action failed.");
                SetStatus(ex.Message);
                TopiaForgeToasts.Show(ex.Message, TopiaForgeTone.Danger, 5f);
            }

            RefreshContent();
        }

        private void Close()
        {
            SetOpen(false);
        }

        private void SetOpen(bool open)
        {
            if (root == null)
            {
                cursorLease.Release();
                return;
            }

            if (root.activeSelf == open)
            {
                return;
            }

            root.SetActive(open);
            cursorLease.SetActive(open);
            if (open)
            {
                // While it shows, the overlay keeps the game's menu navigation from reaching the menu beneath it.
                TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Navigation, root);
                if (shell != null)
                {
                    TopiaForgeMotion.WindowIn(shell);
                }
            }
            else
            {
                TopiaForgeDismissStack.Remove(this);
                HideLiveTab();
            }
        }
    }

    /// <summary>UI state shared between tabs across rebuilds.</summary>
    internal sealed class ManagerUiState
    {
        public string SelectedModId = string.Empty;
        public string PackagePath = string.Empty;
    }

    /// <summary>One manager tab: a title for the rail and a content builder.</summary>
    internal interface IManagerTab
    {
        string Title { get; }

        void Build(TopiaForgeContainer content, ManagerTabContext context);
    }

    /// <summary>Services a tab needs from the shell.</summary>
    internal readonly struct ManagerTabContext
    {
        public ManagerTabContext(
            TopiaForgeModManagerPlugin plugin,
            UiHost host,
            Action<Func<string>> runAction,
            Action<string> setStatus,
            Action refresh,
            Action close)
        {
            Plugin = plugin;
            Host = host;
            RunAction = runAction;
            SetStatus = setStatus;
            Refresh = refresh;
            Close = close;
        }

        public TopiaForgeModManagerPlugin Plugin { get; }
        public UiHost Host { get; }
        public Action<Func<string>> RunAction { get; }
        public Action<string> SetStatus { get; }
        public Action Refresh { get; }
        public Action Close { get; }
    }
}
