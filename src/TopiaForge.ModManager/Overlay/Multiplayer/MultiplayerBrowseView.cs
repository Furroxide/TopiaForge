using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// BROWSE's kit view: the list chooser, searching the local network, the mods readiness line, and a sortable, virtualized
    /// table with the lock, name, gamemode, world, players, ping and mods columns, then JOIN, INSTALL &amp; JOIN IN LAUNCHER
    /// and the favorite toggle for the selection.
    /// </summary>
    internal sealed class MultiplayerBrowseView : IMultiplayerSubView
    {
        private static readonly string[] Lists = { "LAN", "INTERNET", "FAVORITES", "HISTORY" };

        private readonly IBrowseScreen screen;
        private readonly TopiaForgeTabs lists;
        private readonly TopiaForgeButton search;
        private readonly TopiaForgeButton refresh;
        private readonly ContentReadinessStrip readiness;
        private readonly TopiaForgeTableView<ServerRow> table;
        private readonly TopiaForgeLabel selection;
        private readonly TopiaForgeButton join;
        private readonly TopiaForgeButton install;
        private readonly TopiaForgeButton favorite;
        private IReadOnlyList<ServerRow>? shownRows;

        internal MultiplayerBrowseView(TopiaForgeContainer parent, IBrowseScreen screen)
        {
            this.screen = screen;
            var bar = parent.Row(TopiaForgeGap.Sm);
            bar.FixedHeight(TopiaForgeTokens.ControlHeight);
            lists = bar.Tabs(Lists);
            lists.Select((int)screen.List);
            lists.OnSelected(index => screen.SelectList((BrowseList)index));
            bar.Spacer().Flex(1f, 0f);
            search = bar.Button("SEARCH LAN", ToggleSearch, TopiaForgeButtonStyle.Outline);
            refresh = bar.Button("REFRESH", screen.Refresh, TopiaForgeButtonStyle.Ghost);
            readiness = new ContentReadinessStrip(parent, screen.Content);

            var columns = new[]
            {
                TopiaForgeTableColumn<ServerRow>.Text("LOCK", row => row.LockText).Width(96f).Sortable(),
                TopiaForgeTableColumn<ServerRow>.Text("NAME", row => row.Name).Flex(2f).Sortable(),
                TopiaForgeTableColumn<ServerRow>.Text("GAMEMODE", row => row.Gamemode).Flex(1f).Sortable(),
                TopiaForgeTableColumn<ServerRow>.Text("WORLD", row => row.World).Flex(1f).Sortable(),
                TopiaForgeTableColumn<ServerRow>.Text("PLAYERS", row => row.Players).Width(96f).SortBy(row => row.PlayerCount).DescendingFirst(),
                TopiaForgeTableColumn<ServerRow>.Custom("PING", cell => cell.SignalBars().ShowMilliseconds(), BindPing).Width(120f).SortBy(row => row.PingMilliseconds ?? int.MaxValue),
                TopiaForgeTableColumn<ServerRow>.Text("MODS", row => row.ReadinessText).Width(150f).Sortable()
            };
            table = parent.TableView(columns).Keyed(row => row.Key).EmptyText(screen.EmptyText);
            table.OnSelected(index =>
            {
                var rows = screen.Rows;
                screen.Select(index >= 0 && index < rows.Count ? rows[index].Key : null);
            });

            selection = MultiplayerViewKit.Note(parent);
            var actions = parent.Row(TopiaForgeGap.Sm);
            actions.FixedHeight(TopiaForgeTokens.ControlHeight);
            join = actions.Button("JOIN", screen.Join);
            install = actions.Button("INSTALL & JOIN IN LAUNCHER", screen.InstallInLauncher, TopiaForgeButtonStyle.Outline);
            actions.Spacer().Flex(1f, 0f);
            favorite = actions.Button("ADD TO FAVORITES", screen.ToggleFavorite, TopiaForgeButtonStyle.Ghost);
            Tick();
        }

        public void Tick()
        {
            readiness.Bind();
            lists.Select((int)screen.List);
            search.SetText(screen.IsSearching ? "STOP SEARCHING" : "SEARCH LAN");
            search.SetEnabled(screen.IsSearching || screen.CanSearch);
            refresh.SetEnabled(screen.IsSearching);
            var rows = screen.Rows;
            if (!ReferenceEquals(rows, shownRows))
            {
                shownRows = rows;
                table.SetItems(rows);
            }

            table.EmptyText(screen.EmptyText);
            selection.SetText(screen.SelectionText);
            join.SetEnabled(screen.CanJoin);
            install.SetEnabled(screen.CanInstallInLauncher);
            favorite.SetText(screen.IsFavorite ? "REMOVE FAVORITE" : "ADD TO FAVORITES");
            favorite.SetEnabled(screen.CanToggleFavorite);
        }

        private void ToggleSearch()
        {
            if (screen.IsSearching) screen.StopSearch();
            else screen.StartSearch();
        }

        private static void BindPing(TopiaForgeSignalBars bars, ServerRow row)
        {
            if (row.PingMilliseconds.HasValue) bars.SetPing(row.PingMilliseconds.Value);
            else bars.SetUnknown();
        }
    }
}
