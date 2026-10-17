using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// SESSION's kit view: the session, the invite, a virtualized player table shown once the host has listed its players, the
    /// host's kick, ban and mute, and "End session for everyone" or "Leave server".
    /// </summary>
    internal sealed class MultiplayerSessionView : IMultiplayerSubView
    {
        private readonly UiHost host;
        private readonly ISessionScreen screen;
        private readonly TopiaForgeLabel title;
        private readonly TopiaForgeLabel subtitle;
        private readonly TopiaForgeLabel invite;
        private readonly TopiaForgeContainer list;
        private readonly TopiaForgeButton kick;
        private readonly TopiaForgeButton ban;
        private readonly TopiaForgeButton mute;
        private readonly TopiaForgeButton end;
        private readonly TopiaForgeButton leave;
        private TopiaForgeTableView<PlayerRow>? table;
        private TopiaForgeLabel? note;
        private IReadOnlyList<PlayerRow>? shownPlayers;
        private bool shownVisible;

        internal MultiplayerSessionView(UiHost host, TopiaForgeContainer parent, ISessionScreen screen)
        {
            this.host = host;
            this.screen = screen;
            title = parent.Label(screen.Title, TopiaForgeTextStyle.Heading).PlainText().Ellipsis();
            subtitle = MultiplayerViewKit.Note(parent, screen.Subtitle);
            invite = MultiplayerViewKit.Note(parent, screen.InviteText);
            parent.SectionHeader("PLAYERS");
            list = parent.Column(TopiaForgeGap.Xs);
            list.Flex(1f, 1f);
            BuildList();

            var actions = parent.Row(TopiaForgeGap.Sm);
            actions.FixedHeight(TopiaForgeTokens.ControlHeight);
            kick = actions.Button("KICK", screen.Kick, TopiaForgeButtonStyle.Outline);
            ban = actions.Button("BAN", screen.Ban, TopiaForgeButtonStyle.Danger);
            mute = actions.Button("MUTE", screen.ToggleMute, TopiaForgeButtonStyle.Ghost);
            actions.Spacer().Flex(1f, 0f);
            end = actions.Button("END SESSION FOR EVERYONE", screen.EndForEveryone, TopiaForgeButtonStyle.Danger);
            leave = actions.Button("LEAVE SERVER", screen.Leave, TopiaForgeButtonStyle.Danger);
            Tick();
        }

        public void Tick()
        {
            title.SetText(screen.Title);
            subtitle.SetText(screen.Subtitle);
            invite.SetText(screen.InviteText);
            if (screen.PlayersVisible != shownVisible)
            {
                host.Clear(list);
                BuildList();
            }

            if (table != null && !ReferenceEquals(screen.Players, shownPlayers))
            {
                shownPlayers = screen.Players;
                table.SetItems(shownPlayers);
            }

            note?.SetText(screen.PlayersNote);
            kick.SetEnabled(screen.CanKick);
            ban.SetEnabled(screen.CanBan);
            mute.SetEnabled(screen.CanMute);
            mute.SetText(screen.IsSelectedMuted ? "UNMUTE" : "MUTE");
            end.SetVisible(screen.CanEnd);
            end.SetEnabled(screen.CanEnd);
            leave.SetVisible(screen.CanLeave);
            leave.SetEnabled(screen.CanLeave);
        }

        /// <summary>Players show once the host has listed them; before that, only why.</summary>
        private void BuildList()
        {
            shownVisible = screen.PlayersVisible;
            table = null;
            note = null;
            shownPlayers = null;
            if (!shownVisible)
            {
                note = MultiplayerViewKit.Note(list, screen.PlayersNote);
                return;
            }

            var columns = new[]
            {
                TopiaForgeTableColumn<PlayerRow>.Text("NAME", row => row.Name).Flex(2f).Sortable(),
                TopiaForgeTableColumn<PlayerRow>.Text("ROLE", row => row.RoleText).Width(96f).Sortable(),
                TopiaForgeTableColumn<PlayerRow>.Custom("PING", cell => cell.SignalBars().ShowMilliseconds(), BindPing).Width(120f).SortBy(row => row.PingMilliseconds ?? int.MaxValue),
                TopiaForgeTableColumn<PlayerRow>.Text("MUTED", row => row.Muted ? "Muted" : string.Empty).Width(96f)
            };
            table = list.TableView(columns).Keyed(row => row.Key).EmptyText("No other players yet.");
            table.OnSelected(index =>
            {
                var players = screen.Players;
                screen.Select(index >= 0 && index < players.Count ? players[index].Key : null);
            });
        }

        private static void BindPing(TopiaForgeSignalBars bars, PlayerRow row)
        {
            if (row.PingMilliseconds.HasValue) bars.SetPing(row.PingMilliseconds.Value);
            else bars.SetUnknown();
        }
    }
}
