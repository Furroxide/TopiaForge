using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Manifest targets launch through the manager's resolver and authoritative session.</summary>
    internal sealed class GamemodesTab : IManagerTab
    {
        public string Title => "GAMEMODES";
        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            content.Label("LAUNCH TARGETS", TopiaForgeTextStyle.Display).FixedHeight(34f);
            content.Label("Each target selects its declared mode and world. Launch completes when gameplay is ready.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(30f);
            var entries = context.Plugin.GetLaunchTargets();
            var session = context.Plugin.GetSessionService()?.Current;
            if (session != null)
                content.Label("Session: " + session.Phase, TopiaForgeTextStyle.Caption).FixedHeight(24f);
            var remembered = context.Plugin.ReadWorldLaunchSettings();
            if (!string.IsNullOrEmpty(remembered.SelectedGamemodeId))
                content.Label("A legacy selection is saved. Choose a target explicitly; it has not been remapped.",
                    TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Warning).FixedHeight(32f);
            if (entries.Count == 0)
                content.Label("No launch targets are declared by the enabled packages. Check Mods and restart after changing packages.",
                    TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Warning);
            var scroll = content.Scroll(TopiaForgeGap.Sm);
            foreach (var entry in entries)
            {
                var targetId = entry.Id;
                var card = scroll.Content.Panel(TopiaForgePanelStyle.Plain);
                var row = card.Row(TopiaForgeGap.Md, TopiaForgeGap.Md, expandChildWidth: true);
                var details = row.Column(TopiaForgeGap.Xs);
                details.Flex(1f, 0f);
                details.Label(entry.Title, TopiaForgeTextStyle.Heading).FixedHeight(28f);
                details.Label(entry.Description ?? targetId, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(40f);
                var play = row.Button("PLAY", async () =>
                {
                    var (ok, message) = await context.Plugin.LaunchTarget(targetId);
                    context.SetStatus(message);
                    if (ok) context.Close();
                    else { TopiaForgeToasts.Show(message, TopiaForgeTone.Danger, 5f); context.Refresh(); }
                });
                play.Fixed(110f, TopiaForgeTokens.ControlHeight);
            }
            content.Button("MAIN MENU", async () =>
            {
                var (ok, message) = await context.Plugin.ReturnToMainMenu();
                context.SetStatus(message);
                if (ok) context.Close();
                else TopiaForgeToasts.Show(message, TopiaForgeTone.Danger, 5f);
            }).FixedHeight(TopiaForgeTokens.ControlHeight);
        }
    }
}
