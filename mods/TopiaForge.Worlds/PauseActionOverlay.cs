using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// TopiaForgeUi-owned companion surface for gamemode pause actions. The vanilla pause bridge only inspects and
    /// rewires the game's existing exit button; every TopiaForge-created visual remains inside the UI kit.
    /// </summary>
    internal sealed class PauseActionOverlay : IDisposable
    {
        private const int MaxVisibleLabelChars = 48;

        private readonly UiHost ui;
        private readonly IModLogger logger;
        private readonly Action closePauseMenu;
        private readonly List<WorldPauseAction> actions = new List<WorldPauseAction>();
        private TopiaForgeWindow? window;
        private TopiaForgeContainer? actionContent;
        private bool disposed;

        public PauseActionOverlay(UiHost ui, IModLogger logger, Action closePauseMenu)
        {
            this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.closePauseMenu = closePauseMenu ?? throw new ArgumentNullException(nameof(closePauseMenu));
        }

        public void SetActions(IReadOnlyList<WorldPauseAction> next)
        {
            if (disposed)
            {
                return;
            }

            actions.Clear();
            if (next != null)
            {
                for (var index = 0; index < next.Count; index++)
                {
                    if (next[index] != null)
                    {
                        actions.Add(next[index]);
                    }
                }
            }

            Reset();
        }

        public void Show()
        {
            if (disposed || actions.Count == 0)
            {
                return;
            }

            EnsureWindow();
            window!.Show();
        }

        public void Hide()
        {
            window?.Close();
        }

        /// <summary>Releases the session-owned canvas; registered actions remain ready for a later session.</summary>
        public void Reset()
        {
            if (window == null)
            {
                return;
            }

            window.Destroy();
            window = null;
            actionContent = null;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            Reset();
            actions.Clear();
            disposed = true;
        }

        private void EnsureWindow()
        {
            if (window != null)
            {
                return;
            }

            window = ui.Window("world-pause-actions", "WORLD ACTIONS", 380f, 310f, TopiaForgeScheme.Paper);
            var scroll = window.Content.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.Xs).FixedHeight(220f);
            actionContent = scroll.Content;

            for (var index = 0; index < actions.Count; index++)
            {
                var action = actions[index];
                actionContent.Button(
                    VisibleLabel(action.Label),
                    () => ConfirmOrExecute(action),
                    action.Destructive ? TopiaForgeButtonStyle.Danger : TopiaForgeButtonStyle.Filled);
            }
        }

        private void ConfirmOrExecute(WorldPauseAction action)
        {
            if (!action.Destructive)
            {
                Execute(action);
                return;
            }

            ui.Modal.Destructive(
                "CONFIRM ACTION",
                "This action cannot be undone: " + action.Label,
                "CONFIRM",
                () => Execute(action));
        }

        private void Execute(WorldPauseAction action)
        {
            try
            {
                action.Callback();
                ui.Toast(action.Label + " complete.", TopiaForgeTone.Success);
            }
            catch (Exception ex)
            {
                logger.Warn("Worlds pause action '" + action.Id + "' failed: " + ex.Message);
                ui.Toast(action.Label + " failed. Check diagnostics.", TopiaForgeTone.Danger);
                return;
            }

            if (action.ClosePauseMenu)
            {
                Hide();
                closePauseMenu();
            }
        }

        private static string VisibleLabel(string label)
        {
            return label.Length <= MaxVisibleLabelChars
                ? label
                : label.Substring(0, MaxVisibleLabelChars - 1) + "…";
        }
    }
}
