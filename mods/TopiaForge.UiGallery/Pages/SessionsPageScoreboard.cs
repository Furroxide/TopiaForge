using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>The production scoreboard view, including sixteen rows, literal text, column paging and host/client states.</summary>
    internal static class SessionsPageScoreboard
    {
        private static readonly List<Demo> demos = new List<Demo>();
        internal static void Build(TopiaForgeContainer page)
        {
            page.SectionHeader("HOLD-TAB SCOREBOARD");
            page.Label("The live scoreboard's view. Check 16 players at UI scale 1.5, keyboard/controller focus and long literal text. ONLINE's NAME TAGS demonstrates the same pooled tag renderer.", TopiaForgeTextStyle.Caption);
            var screen = new Screen(page.Host);
            var window = page.Host.Window("gallery-scoreboard", "PLAYERS", 1060f, 580f, TopiaForgeScheme.Hud, persistent: true);
            var states = page.Row(TopiaForgeGap.Sm);
            states.Button("OPEN SCOREBOARD", window.Show);
            states.Button("HOST · 16 PLAYERS", () => screen.Set(16, true, false));
            states.Button("CLIENT", () => screen.Set(16, false, false));
            states.Button("RECONNECTING", () => screen.Set(16, false, true));
            states.Button("EMPTY", () => screen.Set(0, false, false));
            var body = window.Content;
            screen.Set(16, true, false);
            demos.Add(new Demo(page, window, new ScoreboardView(body, screen)));
        }
        internal static void Tick() { foreach (var demo in demos) demo.View.Tick(); }
        internal static void Detach(TopiaForgeContainer? page = null)
        { for (var i = demos.Count - 1; i >= 0; i--) if (page == null || ReferenceEquals(page, demos[i].Page)) { demos[i].Window.Close(); demos[i].Window.Destroy(); demos.RemoveAt(i); } }
        private sealed class Demo
        {
            internal Demo(TopiaForgeContainer page, TopiaForgeWindow window, ScoreboardView view) { Page = page; Window = window; View = view; }
            internal readonly TopiaForgeWindow Window;
            internal readonly TopiaForgeContainer Page;
            internal readonly ScoreboardView View;
        }
        private sealed class Screen : IScoreboardScreen
        {
            private readonly UiHost host;
            private readonly List<ScoreboardRow> rows = new List<ScoreboardRow>();
            private readonly List<string> columns = new List<string>();
            internal Screen(UiHost host)
            {
                this.host = host;
                for (var i = 0; i < 64; i++) columns.Add(i == 0 ? "<b>Literal title</b>" : "Score column " + (i + 1));
            }
            public IReadOnlyList<ScoreboardRow> Rows => rows;
            public IReadOnlyList<string> Columns => columns;
            public string Note { get; private set; } = string.Empty;
            public int SortColumn { get; private set; } = -1;
            public bool Descending { get; private set; }
            internal void Set(int count, bool hosting, bool reconnecting)
            {
                rows.Clear();
                for (var i = 0; i < count; i++)
                {
                    var row = new ScoreboardRow(i == 2 ? "<size=80>Literal player</size>" : "Player " + (i + 1), i == 0, i == (hosting ? 0 : 1))
                    {
                        CanModerate = hosting && i != 0 && !reconnecting,
                        CanHear = i != (hosting ? 0 : 1) && !reconnecting,
                        Signal = i % 5 == 0 ? -1 : i % 4 + 1,
                        Talk = (VoiceIndicator)(i % 6),
                        ServerMuted = i % 6 == 3
                    };
                    for (var c = 0; c < 64; c++) row.Values[c] = c % 2 == 0 ? "<b>123</b>" : "12345678901234567890123456789012";
                    rows.Add(row);
                }
                Note = reconnecting ? "Reconnecting. Player actions wait for the current roster." : count == 0 ? "Waiting for the host's player list." : string.Empty;
            }
            public void Sort(int column) { Descending = SortColumn == column && !Descending; SortColumn = column; rows.Reverse(); }
            public void Mute(ScoreboardRow row) { row.Muted = !row.Muted; Notice("Local mute changed."); }
            public void Volume(ScoreboardRow row, float value) => row.Volume = value;
            public void ServerMute(ScoreboardRow row) { row.ServerMuted = !row.ServerMuted; Notice("Server mute changed."); }
            public void Kick(ScoreboardRow row) => Confirm(row, "REMOVE");
            public void Ban(ScoreboardRow row) => Confirm(row, "BAN");
            private void Confirm(ScoreboardRow row, string action) => host.Modal.Destructive(action + " " + row.Name + "?", "Gallery only: no player is affected.", action,
                () => Notice("Confirmed in the gallery."), () => { }, plainText: true, persistent: true);
            private static void Notice(string text) => TopiaForgeToasts.ShowPlain(text);
        }
    }
}
