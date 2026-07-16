using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Session-owned game-over actions with an explicit destructive return confirmation.</summary>
    internal sealed class ZombiesGameOverPresenter : IDisposable
    {
        private readonly IModContext context;
        private readonly Action restart;
        private readonly Action returnToMenu;
        private readonly bool shopEnabled;
        private IUiSurface? window;
        private IUiModal? returnConfirmation;
        private bool suppressCompletion;
        private bool disposed;

        public ZombiesGameOverPresenter(
            IModContext context,
            Action restart,
            Action returnToMenu,
            bool shopEnabled)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.restart = restart ?? throw new ArgumentNullException(nameof(restart));
            this.returnToMenu = returnToMenu ?? throw new ArgumentNullException(nameof(returnToMenu));
            this.shopEnabled = shopEnabled;
        }

        public OperationResult<bool> Show(int score, int wave)
        {
            if (disposed)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The game-over UI is disposed.");
            }

            CloseConfirmation();
            var surface = window;
            var newlyCreated = false;
            if (surface == null)
            {
                var created = context.Ui.CreateSurface(new UiSurfaceRequest(
                    "zombies-game-over",
                    "SYSTEM FAILURE",
                    string.Empty,
                    UiSurfaceKind.Window,
                    540f,
                    300f));
                if (!created.TryGetValue(out surface) || surface == null)
                {
                    return OperationResult<bool>.Failure(created.ErrorCode, created.ErrorMessage);
                }

                window = surface;
                newlyCreated = true;
            }

            surface.SetBody(
                "SCORE  " + score.ToString(CultureInfo.InvariantCulture)
                    + "    WAVE  " + wave.ToString(CultureInfo.InvariantCulture)
                    + "\n\nThe infected robots breached your chassis.");
            var content = surface.SetContent(new UiColumn(
                new UiText("Restart safely, or leave the arena after confirmation.", UiTextStyle.Caption),
                new UiRow(
                    new UiButton("zombies-game-over-restart", "RESTART RUN", restart, UiButtonStyle.Primary),
                    new UiButton(
                        "zombies-game-over-return",
                        "RETURN TO MENU",
                        ConfirmReturn,
                        UiButtonStyle.Danger))));
            if (!content.Succeeded)
            {
                if (newlyCreated)
                {
                    window = null;
                    surface.Dispose();
                }

                return content;
            }

            surface.Show();
            return OperationResult<bool>.Success(true);
        }

        public void Tick()
        {
            if (!disposed && window != null && !window.IsVisible && returnConfirmation == null)
            {
                // Escape/X is a safe dismiss: re-present the game-over actions instead of treating it as exit.
                window.Show();
            }
        }

        public OperationResult<bool> ShowReturning()
        {
            if (disposed)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The game-over UI is disposed.");
            }

            var surface = window;
            if (surface == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The game-over window is unavailable.");
            }

            // Hide the actionable tree before replacing it. If composition fails, disposing the surface prevents
            // Tick() or a renderer from presenting stale restart/return controls during the scene transition.
            surface.Hide();
            surface.SetBody("RETURNING TO MENU...\n\nWaiting for Robotopia to finish the scene transition.");
            var content = surface.SetContent(new UiText("Please wait.", UiTextStyle.Body, UiTone.Warning));
            if (!content.Succeeded)
            {
                window = null;
                surface.Dispose();
                return content;
            }

            surface.Show();
            return OperationResult<bool>.Success(true);
        }

        /// <summary>Closes session UI without permanently disposing the presenter.</summary>
        public void Close()
        {
            if (!disposed)
            {
                CloseHandles();
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CloseHandles();
        }

        private void ConfirmReturn()
        {
            if (disposed || returnConfirmation != null)
            {
                return;
            }

            var result = context.Ui.ShowModal(
                new UiModalRequest(
                    "RETURN TO MENU?",
                    shopEnabled
                        ? "This ends the current Zombies run. Your run upgrades and credits will be lost."
                        : "This ends the current Zombies run.",
                    "RETURN TO MENU",
                    "BACK",
                    destructive: true),
                confirmed =>
                {
                    returnConfirmation = null;
                    if (!suppressCompletion && !disposed && confirmed)
                    {
                        returnToMenu();
                    }
                });
            if (result.TryGetValue(out var modal))
            {
                returnConfirmation = modal;
            }
            else
            {
                context.Logger.Warn("Zombies return confirmation could not open: " + result.ErrorMessage);
                context.Ui.ShowToast("Return confirmation is unavailable; the run is still active.", UiTone.Danger);
            }
        }

        private void CloseHandles()
        {
            suppressCompletion = true;
            CloseConfirmation();
            var currentWindow = window;
            window = null;
            currentWindow?.Dispose();
            suppressCompletion = false;
        }

        private void CloseConfirmation()
        {
            var modal = returnConfirmation;
            returnConfirmation = null;
            modal?.Dispose();
        }
    }
}
