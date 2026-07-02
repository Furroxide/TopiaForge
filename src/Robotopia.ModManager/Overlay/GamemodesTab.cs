using Robotopia.Mods.UnityUi;

namespace Robotopia.ModManager
{
    /// <summary>Gamemode cards with PLAY actions; launching closes the overlay on success.</summary>
    internal sealed class GamemodesTab : IManagerTab
    {
        public string Title => "GAMEMODES";

        public void Build(QwContainer content, ManagerTabContext context)
        {
            content.Label("SELECT GAMEMODE", QwTextStyle.Display).FixedHeight(34f);
            content.Label("Launches close this overlay so the world stays in view.", QwTextStyle.Caption).Tone(QwTone.Muted).FixedHeight(22f);

            var service = context.Plugin.GetWorldService();
            if (service == null)
            {
                content.Label("World/gamemode service unavailable. Enable Robotopia Worlds and restart.", QwTextStyle.Body).Tone(QwTone.Warning);
                return;
            }

            var entries = service.MenuEntries;
            if (entries.Count == 0)
            {
                content.Label("No gamemodes are registered yet.", QwTextStyle.Body).Tone(QwTone.Muted);
                return;
            }

            var scroll = content.Scroll(QwGap.Sm);
            foreach (var entry in entries)
            {
                var entryId = entry.Id;
                var card = scroll.Content.Panel(QwPanelStyle.Plain);
                card.FixedHeight(64f);
                var row = card.Row(QwGap.Md, QwGap.Md, expandChildWidth: false);
                row.Stretch();
                var text = row.Column(QwGap.Xs);
                text.Flex(1f, 0f);
                text.Label(entry.Title.ToUpperInvariant(), QwTextStyle.Heading);
                text.Label(entry.Description, QwTextStyle.Caption).Tone(QwTone.Muted);
                var play = row.Button("PLAY", () =>
                {
                    var (ok, message) = context.Plugin.LaunchGamemode(entryId);
                    context.SetStatus(message);
                    if (ok)
                    {
                        context.Close();
                    }
                    else
                    {
                        QwToasts.Show(message, QwTone.Danger, 5f);
                        context.Refresh();
                    }
                });
                play.Fixed(110f, QwTokens.ControlHeight);
            }
        }
    }
}
