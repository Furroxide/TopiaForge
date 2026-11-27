using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>The real chat and connection views, with controlled offline states for F8 visual QA.</summary>
    internal static class SessionsPageHud
    {
        private static readonly List<Demo> Demos = new List<Demo>();

        internal static void Build(TopiaForgeContainer page)
        {
            page.SectionHeader("IN-SESSION CHAT AND CONNECTION");
            var fake = new Screen();
            var states = page.Row(TopiaForgeGap.Sm);
            states.Button("OPEN CHAT", fake.Open, TopiaForgeButtonStyle.Outline);
            states.Button("CLOSED / FADING", fake.Close, TopiaForgeButtonStyle.Outline);
            states.Button("MESSAGE", () => fake.Add("Sam", "Hello! <b>This stays plain.</b>"), TopiaForgeButtonStyle.Outline);
            states.Button("RATE LIMITED", () => fake.Add(string.Empty, "Chat is sending too quickly. Wait two seconds and try again.", true), TopiaForgeButtonStyle.Outline);
            var status = page.Row(TopiaForgeGap.Sm);
            status.Button("HOST", () => { fake.IsAvailable = true; fake.ConnectionText = "Hosting"; fake.IsReconnecting = false; }, TopiaForgeButtonStyle.Ghost);
            status.Button("CONNECTED", () => { fake.IsAvailable = true; fake.ConnectionText = "Connected"; fake.IsReconnecting = false; }, TopiaForgeButtonStyle.Ghost);
            status.Button("RECONNECTING", () => { fake.IsAvailable = true; fake.ConnectionText = "Reconnecting..."; fake.IsReconnecting = true; }, TopiaForgeButtonStyle.Ghost);
            status.Button("LEFT SESSION", () => { fake.Close(); fake.IsAvailable = false; fake.Revision++; }, TopiaForgeButtonStyle.Ghost);
            var hud = page.Host.Layer("gallery-chat-" + Demos.Count, TopiaForgeLayerBand.Hud, TopiaForgeScheme.Hud, interactive: true, persistent: true);
            var panel = hud.Panel(TopiaForgePanelStyle.Card).Dock(TopiaForgeCorner.BottomLeft).Size(600f, 330f);
            var column = panel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Sm);
            column.Stretch();
            var indicator = new ConnectionIndicator(column, fake);
            var body = column.Stack("Chat").Flex(1f, 1f);
            var chat = new ChatOverlay(body, fake);
            fake.Add("Sam", "Press OPEN CHAT to try typing. The gallery sends nothing.");
            Demos.Add(new Demo(page, hud, fake, chat, indicator));
            page.Label("Pause companion: INVITE and PLAYERS open SESSION; END SESSION or DISCONNECT asks for confirmation there.", TopiaForgeTextStyle.Caption).PlainText();
        }

        internal static void Tick()
        {
            foreach (var demo in Demos) { demo.Chat.Tick(true); demo.Connection.Tick(true); }
        }

        internal static void Detach(TopiaForgeContainer? page = null)
        {
            for (var index = Demos.Count - 1; index >= 0; index--)
            {
                var demo = Demos[index];
                if (page != null && !ReferenceEquals(demo.Page, page)) continue;
                demo.Chat.Dispose();
                demo.Hud.Destroy();
                Demos.RemoveAt(index);
            }
        }

        private sealed class Screen : IInSessionScreen
        {
            private readonly Queue<ChatLine> lines = new Queue<ChatLine>();
            public bool IsAvailable { get; set; } = true;
            public bool IsOpen { get; private set; }
            public bool IsTyping { get; private set; }
            public int Revision { get; set; }
            public string ConnectionText { get; set; } = "Connected";
            public bool IsReconnecting { get; set; }
            public void Open() { IsAvailable = true; IsOpen = true; }
            public void Close() { IsOpen = IsTyping = false; }
            public void SetTyping(bool focused) => IsTyping = IsOpen && focused;
            public bool Submit(string text) { Add("You", text); Close(); return true; }
            internal void Add(string author, string text, bool error = false)
            {
                if (lines.Count == 128) lines.Dequeue();
                lines.Enqueue(new ChatLine(author, text, error));
            }
            public bool TryTakeLine(out ChatLine line)
            {
                if (lines.Count > 0) { line = lines.Dequeue(); return true; }
                line = default;
                return false;
            }
        }

        private sealed class Demo
        {
            internal Demo(TopiaForgeContainer page, TopiaForgeContainer hud, Screen screen, ChatOverlay chat, ConnectionIndicator connection)
            { Page = page; Hud = hud; Screen = screen; Chat = chat; Connection = connection; }
            internal TopiaForgeContainer Page { get; }
            internal TopiaForgeContainer Hud { get; }
            internal Screen Screen { get; }
            internal ChatOverlay Chat { get; }
            internal ConnectionIndicator Connection { get; }
        }
    }
}
