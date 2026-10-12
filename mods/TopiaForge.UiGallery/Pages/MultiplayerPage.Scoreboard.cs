using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    internal static partial class MultiplayerPage
    {
        private const int ScoreColumn = 3;

        private static void BuildScoreboard(TopiaForgeContainer page)
        {
            page.SectionHeader("SCOREBOARD TABLE");
            page.Label(
                "Click a header to sort and again to flip; the chevron marks the column and direction. Ties keep their supplied order "
                + "(Ada, Cy and Ed share 1200). Selection follows the player through re-sorts and through new data.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var players = CreatePlayers();
            var empty = false;
            var random = new Random(20261013);
            var status = page.Label("Nothing selected.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            var columns = new[]
            {
                TopiaForgeTableColumn<DemoPlayer>.Custom(string.Empty, cell => cell.TalkIndicator(16f).HideWhenIdle(), (glyph, player) => glyph.SetState(player.Talk))
                    .Width(28f).Align(TopiaForgeColumnAlignment.Center),
                TopiaForgeTableColumn<DemoPlayer>.Text("PLAYER", player => player.Name).Flex(2f).Sortable(),
                TopiaForgeTableColumn<DemoPlayer>.Custom("ROLE", cell => cell.Badge("HOST", TopiaForgeTone.Accent), (badge, player) => badge.SetVisible(player.IsHost))
                    .Width(64f),
                TopiaForgeTableColumn<DemoPlayer>.Number("SCORE", player => player.Score).Width(72f).Sortable().DescendingFirst(),
                TopiaForgeTableColumn<DemoPlayer>.Number("KILLS", player => player.Kills).Width(60f).Sortable().DescendingFirst(),
                TopiaForgeTableColumn<DemoPlayer>.Number("DEATHS", player => player.Deaths).Width(68f).Sortable(),
                TopiaForgeTableColumn<DemoPlayer>.Custom("PING", cell => cell.SignalBars().ShowMilliseconds(), (bars, player) => bars.SetPing(player.Ping))
                    .Width(96f).SortBy(player => player.Ping),
            };

            var table = page.TableView(columns, 32f)
                .Keyed(player => player.Id)
                .EmptyText("No players in this session.")
                .FixedHeight(300f);
            table.OnSelected(index => status.SetText("Selected: " + players[index].Name + "  (supplied index " + index + ")"));
            table.SetItems(players);
            table.SortBy(ScoreColumn, TopiaForgeSortDirection.Descending);

            var actions = page.Row(TopiaForgeGap.Sm);
            actions.Button("PREV", () => table.MoveSelection(-1), TopiaForgeButtonStyle.Outline);
            actions.Button("NEXT", () => table.MoveSelection(1), TopiaForgeButtonStyle.Outline);
            actions.Button("JITTER PINGS", () =>
            {
                foreach (var player in players)
                {
                    player.Ping = player.Ping < 0 ? -1 : 10 * random.Next(2, 45);
                }

                table.Refresh();
            }, TopiaForgeButtonStyle.Outline);
            actions.Button("+100 SELECTED", () =>
            {
                var selected = table.SelectedItem;
                if (selected != null)
                {
                    selected.Score += 100;
                    table.Refresh();
                }
            }, TopiaForgeButtonStyle.Outline);
            actions.Button("NEW DATA", () =>
            {
                // A fresh list in a different order: the keyed selection stays on the same player.
                var reordered = new DemoPlayer[players.Length];
                for (var index = 0; index < players.Length; index++)
                {
                    reordered[index] = players[players.Length - 1 - index];
                }

                players = reordered;
                empty = false;
                table.SetItems(players);
            }, TopiaForgeButtonStyle.Ghost);
            actions.Button("CLEAR SORT", table.ClearSort, TopiaForgeButtonStyle.Ghost);
            actions.Button("EMPTY", () =>
            {
                empty = !empty;
                table.SetItems(empty ? Array.Empty<DemoPlayer>() : players);
            }, TopiaForgeButtonStyle.Ghost);
        }

        private static DemoPlayer[] CreatePlayers()
        {
            return new[]
            {
                new DemoPlayer("ada", "Ada", 1200, 14, 3, 20, host: true, TopiaForgeTalkState.Talking),
                new DemoPlayer("bo", "Bo", 950, 11, 6, 60, host: false, TopiaForgeTalkState.Idle),
                new DemoPlayer("cy", "Cy", 1200, 13, 4, 110, host: false, TopiaForgeTalkState.Muted),
                new DemoPlayer("di", "Di", 400, 4, 9, 230, host: false, TopiaForgeTalkState.MutedByHost),
                new DemoPlayer("ed", "Ed", 1200, 15, 2, 380, host: false, TopiaForgeTalkState.Idle),
                new DemoPlayer("fi", "Fi", 780, 8, 7, -1, host: false, TopiaForgeTalkState.NoMic),
                new DemoPlayer("gil", "Gil", 1530, 19, 1, 40, host: false, TopiaForgeTalkState.Talking),
                new DemoPlayer("hal", "Hal", 0, 0, 12, 160, host: false, TopiaForgeTalkState.MicBusy),
                new DemoPlayer("ivy", "Ivy", 620, 6, 5, 90, host: false, TopiaForgeTalkState.Idle),
                new DemoPlayer("jo", "Jo", 950, 10, 8, 70, host: false, TopiaForgeTalkState.Idle),
                new DemoPlayer("vex", "Professor Vex Thunderbolt the Unreasonably Long", 1100, 12, 6, 130, host: false, TopiaForgeTalkState.Idle),
                new DemoPlayer("mal", "<color=red><size=40>Mal</size></color>", 300, 3, 10, 300, host: false, TopiaForgeTalkState.Idle),
            };
        }

        private sealed class DemoPlayer
        {
            public DemoPlayer(string id, string name, int score, int kills, int deaths, int ping, bool host, TopiaForgeTalkState talk)
            {
                Id = id;
                Name = name;
                Score = score;
                Kills = kills;
                Deaths = deaths;
                Ping = ping;
                IsHost = host;
                Talk = talk;
            }

            public string Id { get; }

            public string Name { get; }

            public int Score { get; set; }

            public int Kills { get; }

            public int Deaths { get; }

            public int Ping { get; set; }

            public bool IsHost { get; }

            public TopiaForgeTalkState Talk { get; }
        }
    }
}
