using System;
using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.ModManager
{
    /// <summary>
    /// The F10 / main-menu manager overlay, rebuilt on the QwUi kit: Paper-scheme
    /// full-screen card with a nav rail, per-tab content inside scroll views, toasts
    /// for action results, a destructive-confirm modal for removal, ESC-close via the
    /// kit dismiss stack, and cursor handling via the kit lease.
    /// Public surface (Toggle/Show/ShowGamemodes/Tick/Dispose) is unchanged.
    /// </summary>
    internal sealed class ManagerOverlay : IQwDismissable
    {
        private readonly RobotopiaModManagerPlugin plugin;
        private readonly ManagerFileLogger logger;
        private readonly ManagerUiState uiState = new ManagerUiState();
        private readonly QwCursorLease cursorLease = new QwCursorLease();
        private UiHost? host;
        private GameObject? root;
        private QwPanel? shell;
        private QwTabs? nav;
        private QwLabel? statusLabel;
        private QwContainer? content;
        private IManagerTab[] tabs = Array.Empty<IManagerTab>();
        private int selectedTab;
        private string status = "Trusted local mode: install only packages you explicitly trust. C# mods execute code.";

        public ManagerOverlay(RobotopiaModManagerPlugin plugin, ManagerFileLogger logger)
        {
            this.plugin = plugin;
            this.logger = logger;
        }

        QwLayerBand IQwDismissable.Band => QwLayerBand.Window;

        void IQwDismissable.Dismiss()
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
            OpenAt(1); // Installed
        }

        public void ShowGamemodes()
        {
            OpenAt(0);
        }

        public void Tick()
        {
            // ESC and cursor are handled by the kit (dismiss stack + per-frame lease).
        }

        public void Dispose()
        {
            cursorLease.Release();
            QwDismissStack.Remove(this);
            host?.Dispose();
            host = null;
            root = null;
        }

        private void OpenAt(int tabIndex)
        {
            if (root == null)
            {
                Build();
            }

            selectedTab = tabIndex;
            nav?.Select(tabIndex);
            SetOpen(true);
            RefreshContent();
        }

        private void Build()
        {
            host = QwUi.Create(new QwUiOptions
            {
                OwnerId = "robotopia.modmanager",
                DataDirectory = plugin.Paths.Data,
                LogInfo = logger.Info,
                LogWarn = logger.Warn,
                LogError = message => logger.Error(message),
            });

            var layer = host.Layer("overlay", QwLayerBand.Window, QwScheme.Paper, interactive: true, persistent: true);
            root = layer.Go;
            // The menu-button injector's canvas scan excludes this exact name.
            root.name = "RobotopiaModManagerOverlay";

            var dim = layer.FreeImage("Dim");
            dim.Stretch();
            dim.SetColor(host.Theme(QwScheme.Paper).Backdrop);
            dim.Image.raycastTarget = true;

            shell = layer.Panel(QwPanelStyle.Card);
            shell.Rect.anchorMin = new Vector2(0.07f, 0.08f);
            shell.Rect.anchorMax = new Vector2(0.93f, 0.92f);
            shell.Rect.offsetMin = Vector2.zero;
            shell.Rect.offsetMax = Vector2.zero;

            var body = shell.Row(QwGap.None, QwGap.None, expandChildWidth: false);
            body.Stretch();

            // ---- nav rail ----
            var railPanel = body.Panel(QwPanelStyle.Sunken);
            railPanel.FixedWidth(230f);
            railPanel.Flex(0f, 1f);
            var rail = railPanel.Column(QwGap.Sm, QwGap.Md);
            rail.Stretch();
            rail.Label("QUANTUMWORKS", QwTextStyle.Title).Tone(QwTone.Primary).FixedHeight(42f);
            tabs = new IManagerTab[]
            {
                new GamemodesTab(),
                new InstalledTab(uiState),
                new PackagesTab(uiState),
                new SettingsTab(),
                new LogsTab(),
            };
            var labels = new string[tabs.Length];
            for (var index = 0; index < tabs.Length; index++)
            {
                labels[index] = tabs[index].Title;
            }

            nav = rail.NavRail(labels);
            nav.OnSelected(index =>
            {
                selectedTab = index;
                RefreshContent();
            });
            rail.Spacer();
            rail.Button("CLOSE", Close, QwButtonStyle.Outline).FixedHeight(QwTokens.ControlHeight);

            // ---- main area ----
            var main = body.Column(QwGap.Sm, QwGap.Md);
            main.Flex(1f, 1f);
            statusLabel = main.Label(status, QwTextStyle.Caption).Tone(QwTone.Warning);
            statusLabel.FixedHeight(30f);
            var contentPanel = main.Panel(QwPanelStyle.Sunken);
            contentPanel.Flex(1f, 1f);
            content = contentPanel.Column(QwGap.Sm, QwGap.Md);
            content.Stretch();

            root.SetActive(false);
        }

        private void RefreshContent()
        {
            if (content == null || host == null)
            {
                return;
            }

            // QwUi retains widgets for theme refresh/tween cancellation. Clear through the host so a tab rebuild
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
                QwToasts.Show(message, QwTone.Success);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Mod manager UI action failed.");
                SetStatus(ex.Message);
                QwToasts.Show(ex.Message, QwTone.Danger, 5f);
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
                QwDismissStack.Push(this);
                if (shell != null)
                {
                    QwMotion.WindowIn(shell);
                }
            }
            else
            {
                QwDismissStack.Remove(this);
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

        void Build(QwContainer content, ManagerTabContext context);
    }

    /// <summary>Services a tab needs from the shell.</summary>
    internal readonly struct ManagerTabContext
    {
        public ManagerTabContext(
            RobotopiaModManagerPlugin plugin,
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

        public RobotopiaModManagerPlugin Plugin { get; }
        public UiHost Host { get; }
        public Action<Func<string>> RunAction { get; }
        public Action<string> SetStatus { get; }
        public Action Refresh { get; }
        public Action Close { get; }
    }
}
