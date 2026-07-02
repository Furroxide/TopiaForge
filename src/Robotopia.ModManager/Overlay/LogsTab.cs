using Robotopia.Mods.UnityUi;

namespace Robotopia.ModManager
{
    /// <summary>Recent manager log lines in a scroll view, newest visible.</summary>
    internal sealed class LogsTab : IManagerTab
    {
        public string Title => "LOGS";

        public void Build(QwContainer content, ManagerTabContext context)
        {
            content.Label("RECENT LOGS", QwTextStyle.Display).FixedHeight(34f);
            content.Label("Latest manager log lines.", QwTextStyle.Caption).Tone(QwTone.Muted).FixedHeight(22f);

            var actions = content.Row(QwGap.Sm);
            actions.FixedHeight(QwTokens.ControlHeight);
            actions.Button("REFRESH", context.Refresh, QwButtonStyle.Outline);
            actions.Button("OPEN LOGS", () => context.Plugin.OpenFolder(context.Plugin.Paths.Logs), QwButtonStyle.Ghost);

            var scroll = content.Scroll(QwGap.None, QwGap.Sm);
            var text = scroll.Content.Label(context.Plugin.ReadRecentLogLines(80), QwTextStyle.Caption);
            text.AlignTopLeft();
            scroll.ScrollToEnd();
        }
    }
}
