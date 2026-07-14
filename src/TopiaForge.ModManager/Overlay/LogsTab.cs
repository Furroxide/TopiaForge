using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Recent manager log lines in a scroll view, newest visible.</summary>
    internal sealed class LogsTab : IManagerTab
    {
        public string Title => "LOGS";

        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            content.Label("RECENT LOGS", TopiaForgeTextStyle.Display).FixedHeight(34f);
            content.Label("Latest manager log lines.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(22f);

            var actions = content.Row(TopiaForgeGap.Sm);
            actions.FixedHeight(TopiaForgeTokens.ControlHeight);
            actions.Button("REFRESH", context.Refresh, TopiaForgeButtonStyle.Outline);
            actions.Button("OPEN LOGS", () => context.Plugin.OpenFolder(context.Plugin.Paths.Logs), TopiaForgeButtonStyle.Ghost);

            var scroll = content.Scroll(TopiaForgeGap.None, TopiaForgeGap.Sm);
            var text = scroll.Content.Label(context.Plugin.ReadRecentLogLines(80), TopiaForgeTextStyle.Caption);
            text.AlignTopLeft();
            scroll.ScrollToEnd();
        }
    }
}
