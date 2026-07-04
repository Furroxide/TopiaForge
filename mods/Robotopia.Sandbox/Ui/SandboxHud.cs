using Robotopia.Mods.UnityUi;

namespace Robotopia.Sandbox.Ui
{
    /// <summary>
    /// Minimal session HUD: live spawned-object counts and the hotkey hints, docked top-left. Setters are
    /// dirty-checked by the kit, so calling them every frame costs nothing while the counts are unchanged.
    /// </summary>
    internal sealed class SandboxHud
    {
        private readonly QwLabel props;
        private readonly QwLabel robots;

        public SandboxHud(UiHost ui, SandboxConfig config)
        {
            // Created before the sandbox scene's Single-mode load; persistent so the swap cannot destroy it.
            var hud = ui.HudLayer("sandboxhud", persistent: true);
            var panel = hud.Scaled.Panel(QwPanelStyle.HudPanel)
                .Dock(QwCorner.TopLeft)
                .Size(280f, 118f);
            var column = panel.Column(QwGap.Xs, QwGap.Sm);
            column.Label("SANDBOX", QwTextStyle.Label).Tone(QwTone.Accent);
            props = column.Label(QwTextStyle.Body);
            robots = column.Label(QwTextStyle.Body);
            column.Label(config.SpawnMenuKey + " spawn menu · " + config.UndoKey + " undo · " + config.FreezeKey + " freeze",
                QwTextStyle.Caption).Tone(QwTone.Muted);

            props.SetText("PROPS ", 0);
            robots.SetText("ROBOTS ", 0);
        }

        public void Update(int propCount, int robotCount)
        {
            props.SetText("PROPS ", propCount);
            robots.SetText("ROBOTS ", robotCount);
        }
    }
}
