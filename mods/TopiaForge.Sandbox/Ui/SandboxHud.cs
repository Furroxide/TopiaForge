using TopiaForge.Mods.UnityUi;

namespace TopiaForge.Sandbox.Ui
{
    /// <summary>
    /// Minimal session HUD: live spawned-object counts and the hotkey hints, docked top-left. Setters are
    /// dirty-checked by the kit, so calling them every frame costs nothing while the counts are unchanged.
    /// </summary>
    internal sealed class SandboxHud
    {
        private readonly TopiaForgeLabel props;
        private readonly TopiaForgeLabel robots;

        public SandboxHud(UiHost ui, SandboxConfig config)
        {
            // Created before the sandbox scene's Single-mode load; persistent so the swap cannot destroy it.
            var hud = ui.HudLayer("sandboxhud", persistent: true);
            var panel = hud.Scaled.Panel(TopiaForgePanelStyle.HudPanel)
                .Dock(TopiaForgeCorner.TopLeft)
                .Size(280f, 118f);
            var column = panel.Column(TopiaForgeGap.Xs, TopiaForgeGap.Sm);
            column.Label("SANDBOX", TopiaForgeTextStyle.Label).Tone(TopiaForgeTone.Accent);
            props = column.Label(TopiaForgeTextStyle.Body);
            robots = column.Label(TopiaForgeTextStyle.Body);
            column.Label(config.SpawnMenuKey + " spawn menu · " + config.UndoKey + " undo · " + config.FreezeKey + " freeze",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

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
