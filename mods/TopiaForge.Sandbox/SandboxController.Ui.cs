using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    internal sealed partial class SandboxController
    {
        private void ToggleMenu()
        {
            if (menu == null)
            {
                var result = context.Ui.CreateSurface(new UiSurfaceRequest(
                    "sandbox-creator",
                    "CREATOR TOOLS",
                    BuildMenuStatus(),
                    UiSurfaceKind.Window,
                    width: 520f,
                    height: 420f,
                    content: new UiColumn(
                        new UiText("ROBOT WORKSHOP", UiTextStyle.Heading),
                        new UiText(
                            "Create and manage robots without opening the command console.",
                            UiTextStyle.Caption),
                        new UiRow(
                            new UiButton("spawn-robot", "Spawn robot", () => ExecuteMenuAction(SpawnRobot)),
                            new UiButton(
                                "undo-spawn",
                                "Undo latest",
                                () => ExecuteMenuAction(Undo),
                                UiButtonStyle.Secondary)),
                        new UiRow(
                            new UiButton(
                                "toggle-simulation",
                                "Pause / resume",
                                () => ExecuteMenuAction(ToggleRobotSimulation),
                                UiButtonStyle.Secondary),
                            new UiButton("clear-all", "Clear all", ConfirmClear, UiButtonStyle.Danger)),
                        new UiText(
                            "Tip: interact with a spawned robot to toggle FOLLOW PLAYER.",
                            UiTextStyle.Caption))));
                if (!result.TryGetValue(out menu))
                {
                    context.Ui.ShowToast(result.ErrorMessage, UiTone.Danger);
                    return;
                }
            }

            if (menu.IsVisible)
            {
                menu.Hide();
            }
            else
            {
                menu.Show();
            }
        }

        private void ExecuteMenuAction(Func<OperationResult<string>> action)
        {
            var result = action();
            if (!result.Succeeded)
            {
                context.Ui.ShowToast(result.ErrorMessage, UiTone.Danger);
            }

            menu?.SetBody(BuildMenuStatus());
        }

        private void ConfirmClear()
        {
            if (confirmation?.IsOpen == true)
            {
                return;
            }

            var result = context.Ui.ShowModal(
                new UiModalRequest(
                    "CLEAR SANDBOX?",
                    "All robots created by this sandbox session will be removed.",
                    confirmLabel: "CLEAR ALL",
                    destructive: true),
                confirmed =>
                {
                    confirmation = null;
                    if (confirmed)
                    {
                        ExecuteMenuAction(CleanUpEverything);
                    }
                });
            if (!result.TryGetValue(out confirmation))
            {
                context.Ui.ShowToast(result.ErrorMessage, UiTone.Danger);
            }
        }

        private string BuildMenuStatus()
        {
            return "ROBOTS  " + spawned.Count.ToString(CultureInfo.InvariantCulture)
                + " / " + config.MaxSpawnedObjects.ToString(CultureInfo.InvariantCulture)
                + "\nSIMULATION  " + (robotsPaused ? "PAUSED" : "RUNNING")
                + "\n\nSHORTCUTS  " + config.UndoKey + " UNDO   " + config.FreezeKey + " PAUSE";
        }
    }
}
