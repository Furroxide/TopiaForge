using System;
using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>HUD scoreboard: bounded pooled rows in a scroll view, with four score columns per page.</summary>
    internal sealed class ScoreboardView
    {
        private const int PageSize = 4;
        private readonly IScoreboardScreen screen;
        private readonly TopiaForgeLabel note, page;
        private readonly TopiaForgeButton previous, next, nameSort;
        private readonly TopiaForgeLabel[] titles = new TopiaForgeLabel[PageSize];
        private readonly TopiaForgeButton[] sort = new TopiaForgeButton[PageSize];
        private readonly TopiaForgeContainer[] headers = new TopiaForgeContainer[PageSize];
        private readonly List<Row> rows = new List<Row>(32);
        private readonly TopiaForgeContainer list;
        private int first;

        internal ScoreboardView(TopiaForgeContainer parent, IScoreboardScreen screen)
        {
            this.screen = screen;
            var toolbar = parent.Row(TopiaForgeGap.Sm);
            toolbar.FixedHeight(TopiaForgeTokens.ControlHeight);
            nameSort = toolbar.Button("NAME A–Z", () => screen.Sort(-1), TopiaForgeButtonStyle.Outline);
            page = toolbar.Label("", TopiaForgeTextStyle.Caption).PlainText().Flex(1f, 0f);
            previous = toolbar.Button("PREVIOUS", () => first = Math.Max(0, first - PageSize), TopiaForgeButtonStyle.Ghost);
            next = toolbar.Button("NEXT", () => first += PageSize, TopiaForgeButtonStyle.Ghost);
            note = parent.Label("", TopiaForgeTextStyle.Caption).PlainText().FixedHeight(34f);
            var header = parent.Row(TopiaForgeGap.Sm);
            header.FixedHeight(72f);
            for (var i = 0; i < PageSize; i++)
            {
                var slot = i;
                headers[i] = header.Column(TopiaForgeGap.Xs).FixedWidth(0f).Flex(1f, 0f);
                titles[i] = headers[i].Label("", TopiaForgeTextStyle.Caption).PlainText().Ellipsis().FixedHeight(24f);
                sort[i] = headers[i].Button("SORT", () => screen.Sort(first + slot), TopiaForgeButtonStyle.Ghost);
            }
            var scroll = parent.Scroll(TopiaForgeGap.Sm);
            scroll.Flex(1f, 1f); list = scroll.Content;
            Tick();
        }

        internal void Focus() => nameSort.Focus();
        internal void Tick()
        {
            first = Math.Min(first, Math.Max(0, ((screen.Columns.Count - 1) / PageSize) * PageSize));
            page.SetText("Score columns: ", screen.Columns.Count == 0 ? 0 : first + 1, "–", Math.Min(first + PageSize, screen.Columns.Count));
            previous.SetEnabled(first > 0); next.SetEnabled(first + PageSize < screen.Columns.Count);
            nameSort.SetText(screen.SortColumn == -1 ? screen.Descending ? "NAME Z–A" : "NAME A–Z" : "SORT NAME");
            note.SetText(screen.Note); note.SetShown(screen.Note.Length != 0);
            for (var i = 0; i < PageSize; i++)
            {
                var column = first + i; var exists = column < screen.Columns.Count;
                headers[i].SetVisible(exists);
                if (!exists) continue;
                titles[i].SetText(screen.Columns[column]);
                sort[i].SetText(screen.SortColumn == column ? screen.Descending ? "HIGH FIRST" : "LOW FIRST" : "SORT");
            }
            while (rows.Count < Math.Min(32, screen.Rows.Count)) rows.Add(new Row(list, screen));
            for (var i = 0; i < rows.Count; i++) rows[i].Bind(i < screen.Rows.Count ? screen.Rows[i] : null, first, screen.Columns.Count);
        }

        private sealed class Row
        {
            private readonly TopiaForgeContainer root, moderation;
            private readonly TopiaForgeTalkIndicator talk;
            private readonly TopiaForgeLabel name;
            private readonly TopiaForgeBadge host, local;
            private readonly TopiaForgeSignalBars signal;
            private readonly TopiaForgeButton mute, serverMute;
            private readonly TopiaForgeSlider volume;
            private readonly TopiaForgeLabel[] values = new TopiaForgeLabel[PageSize];
            private ScoreboardRow? bound;
            internal Row(TopiaForgeContainer parent, IScoreboardScreen screen)
            {
                root = parent.Column(TopiaForgeGap.Xs, TopiaForgeGap.Sm).Dynamic().FixedHeight(132f);
                var identity = root.Row(TopiaForgeGap.Sm).FixedHeight(32f);
                talk = identity.TalkIndicator();
                name = identity.Label("", TopiaForgeTextStyle.Body).PlainText().Ellipsis().FixedWidth(0f).Flex(1f, 0f);
                host = identity.Badge("HOST", TopiaForgeTone.Primary);
                local = identity.Badge("YOU");
                signal = identity.SignalBars();
                var scores = root.Row(TopiaForgeGap.Sm).FixedHeight(24f);
                for (var i = 0; i < PageSize; i++) values[i] = scores.Label("", TopiaForgeTextStyle.Caption).PlainText().Ellipsis().FixedWidth(0f).Flex(1f, 0f);
                var actions = root.Row(TopiaForgeGap.Sm).FixedHeight(TopiaForgeTokens.ControlHeight);
                mute = actions.Button("MUTE", () => { if (bound != null) screen.Mute(bound); }, TopiaForgeButtonStyle.Outline);
                volume = actions.Slider("Volume %", 0, 100, 100, value => { if (bound != null) screen.Volume(bound, value / 100f); });
                volume.Flex(1f, 0f);
                moderation = actions.Row(TopiaForgeGap.Xs);
                serverMute = moderation.Button("SERVER MUTE", () => { if (bound != null) screen.ServerMute(bound); }, TopiaForgeButtonStyle.Outline);
                moderation.Button("KICK", () => { if (bound != null) screen.Kick(bound); }, TopiaForgeButtonStyle.Outline);
                moderation.Button("BAN", () => { if (bound != null) screen.Ban(bound); }, TopiaForgeButtonStyle.Danger);
            }
            internal void Bind(ScoreboardRow? row, int first, int columns)
            {
                // A live re-sort must not leave keyboard focus on a different player's action.
                if (!ReferenceEquals(bound, row)) root.ReleaseFocus();
                root.SetShown(row != null); bound = row;
                if (row == null) return;
                name.SetText(row.Name); host.SetShown(row.IsHost); local.SetShown(row.IsLocal);
                talk.SetState((TopiaForgeTalkState)row.Talk); signal.SetLevel(row.Signal, 4);
                mute.SetText(row.Muted ? "UNMUTE" : "MUTE"); mute.SetEnabled(row.CanHear);
                volume.SetValue(row.Volume * 100f); volume.SetEnabled(row.CanHear);
                moderation.SetShown(row.CanModerate);
                serverMute.SetText(row.ServerMuted ? "SERVER UNMUTE" : "SERVER MUTE");
                for (var i = 0; i < PageSize; i++) values[i].SetText(first + i < columns ? row.Values[first + i] ?? "—" : "");
            }
        }
    }
}
