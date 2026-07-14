using System.Linq;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Runtime status, manager paths, and UI accessibility settings.</summary>
    internal sealed class SettingsTab : IManagerTab
    {
        public string Title => "SETTINGS";

        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            content.Label("RUNTIME STATUS", TopiaForgeTextStyle.Display).FixedHeight(34f);
            content.Label("Manager paths and restart state.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(22f);

            var restart = context.Plugin.State.Mods.Any(m => m.RestartRequired || m.UninstallPending);
            var statusRow = content.Row(TopiaForgeGap.Sm);
            statusRow.FixedHeight(26f);
            statusRow.Label("Restart required:", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Muted);
            statusRow.Badge(restart ? "YES" : "NO", restart ? TopiaForgeTone.Warning : TopiaForgeTone.Success);

            content.KeyValueRow("Mode", "trusted local packages");
            content.KeyValueRow("Loaded mods", context.Plugin.LoadedModIds.Count.ToString());
            content.KeyValueRow("Manager root", context.Plugin.Paths.Root);
            content.KeyValueRow("Package inbox", context.Plugin.Paths.PackageInbox);
            content.KeyValueRow("Logs", context.Plugin.Paths.Logs);

            var actions = content.Row(TopiaForgeGap.Sm);
            actions.FixedHeight(TopiaForgeTokens.ControlHeight);
            actions.Button("OPEN ROOT", () => context.Plugin.OpenFolder(context.Plugin.Paths.Root), TopiaForgeButtonStyle.Outline);
            actions.Button("OPEN CONFIG", () => context.Plugin.OpenFolder(context.Plugin.Paths.Config), TopiaForgeButtonStyle.Outline);
            actions.Button("OPEN LOGS", () => context.Plugin.OpenFolder(context.Plugin.Paths.Logs), TopiaForgeButtonStyle.Outline);

            content.SectionHeader("INTERFACE");
            content.Toggle("High contrast UI", TopiaForgeTheme.HighContrast, value => TopiaForgeTheme.HighContrast = value);
            content.Toggle("Reduced motion", TopiaForgeTheme.ReducedMotion, value => TopiaForgeTheme.ReducedMotion = value);
            content.Slider("UI scale", 0.75f, 1.5f, TopiaForgeTheme.UiScale, value => TopiaForgeTheme.UiScale = value);
        }
    }
}
