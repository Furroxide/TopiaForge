using System.Linq;
using Robotopia.Mods.UnityUi;

namespace Robotopia.ModManager
{
    /// <summary>Runtime status, manager paths, and UI accessibility settings.</summary>
    internal sealed class SettingsTab : IManagerTab
    {
        public string Title => "SETTINGS";

        public void Build(QwContainer content, ManagerTabContext context)
        {
            content.Label("RUNTIME STATUS", QwTextStyle.Display).FixedHeight(34f);
            content.Label("Manager paths and restart state.", QwTextStyle.Caption).Tone(QwTone.Muted).FixedHeight(22f);

            var restart = context.Plugin.State.Mods.Any(m => m.RestartRequired || m.UninstallPending);
            var statusRow = content.Row(QwGap.Sm);
            statusRow.FixedHeight(26f);
            statusRow.Label("Restart required:", QwTextStyle.Body).Tone(QwTone.Muted);
            statusRow.Badge(restart ? "YES" : "NO", restart ? QwTone.Warning : QwTone.Success);

            content.KeyValueRow("Mode", "trusted local packages");
            content.KeyValueRow("Loaded mods", context.Plugin.LoadedModIds.Count.ToString());
            content.KeyValueRow("Manager root", context.Plugin.Paths.Root);
            content.KeyValueRow("Package inbox", context.Plugin.Paths.PackageInbox);
            content.KeyValueRow("Logs", context.Plugin.Paths.Logs);

            var actions = content.Row(QwGap.Sm);
            actions.FixedHeight(QwTokens.ControlHeight);
            actions.Button("OPEN ROOT", () => context.Plugin.OpenFolder(context.Plugin.Paths.Root), QwButtonStyle.Outline);
            actions.Button("OPEN CONFIG", () => context.Plugin.OpenFolder(context.Plugin.Paths.Config), QwButtonStyle.Outline);
            actions.Button("OPEN LOGS", () => context.Plugin.OpenFolder(context.Plugin.Paths.Logs), QwButtonStyle.Outline);

            content.SectionHeader("INTERFACE");
            content.Toggle("High contrast UI", QwTheme.HighContrast, value => QwTheme.HighContrast = value);
            content.Toggle("Reduced motion", QwTheme.ReducedMotion, value => QwTheme.ReducedMotion = value);
            content.Slider("UI scale", 0.75f, 1.5f, QwTheme.UiScale, value => QwTheme.UiScale = value);
        }
    }
}
