using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// VOICE's players: each other player the host lists, with a talking glyph, their name as they chose it, a host badge,
    /// and a MUTE or UNMUTE button for this player alone. The rows are rebuilt only when the screen hands over a new list;
    /// each frame only their glyphs and buttons are bound. Names render as plain text. Kit only.
    /// </summary>
    internal sealed class MultiplayerVoicePlayersView
    {
        private readonly IVoiceScreen screen;
        private readonly TopiaForgeLabel note;
        private readonly TopiaForgeContainer list;
        private readonly List<Row> rows = new List<Row>();
        private IReadOnlyList<VoicePlayerRow>? shown;

        internal MultiplayerVoicePlayersView(TopiaForgeContainer parent, IVoiceScreen screen)
        {
            this.screen = screen;
            note = MultiplayerViewKit.Note(parent, screen.PlayersNote);
            list = parent.Column(TopiaForgeGap.Xs);
            Tick();
        }

        internal void Tick()
        {
            var visible = screen.PlayersVisible;
            note.SetText(screen.PlayersNote);
            note.SetShown(!visible);
            list.SetShown(visible);
            if (!ReferenceEquals(screen.Players, shown)) Rebuild();
            for (var index = 0; index < rows.Count; index++) rows[index].Bind(screen);
        }

        private void Rebuild()
        {
            shown = screen.Players;
            list.Host.Clear(list);
            rows.Clear();
            foreach (var player in shown) rows.Add(new Row(list, screen, player));
        }

        private sealed class Row
        {
            private readonly string key;
            private readonly TopiaForgeTalkIndicator talking;
            private readonly TopiaForgeButton mute;

            internal Row(TopiaForgeContainer parent, IVoiceScreen screen, VoicePlayerRow player)
            {
                key = player.Key;
                var row = parent.Row(TopiaForgeGap.Sm);
                row.FixedHeight(TopiaForgeTokens.ControlHeight);
                talking = row.TalkIndicator();
                row.Label(player.Name, TopiaForgeTextStyle.Body).PlainText().Ellipsis().Flex(1f, 0f);
                if (player.IsHost) row.Badge("HOST", TopiaForgeTone.Primary);
                mute = row.Button("MUTE", () => screen.ToggleMute(key), TopiaForgeButtonStyle.Outline);
                mute.SetEnabled(player.CanMute);
                Bind(screen);
            }

            internal void Bind(IVoiceScreen screen)
            {
                var muted = screen.IsMuted(key);
                talking.SetState(muted ? TopiaForgeTalkState.Muted : screen.IsSpeaking(key) ? TopiaForgeTalkState.Talking : TopiaForgeTalkState.Idle);
                mute.SetText(muted ? "UNMUTE" : "MUTE");
            }
        }
    }
}
