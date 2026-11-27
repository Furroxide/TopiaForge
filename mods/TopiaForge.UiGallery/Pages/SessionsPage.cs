using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>
    /// The manager's in-game multiplayer screens with fake data: the Multiplayer tab (BROWSE, HOST, CONNECT, VOICE and
    /// SESSION), the approved privacy prompts, the destructive confirmations and the connecting overlay. The buttons above
    /// the tab switch it through its loading, empty, error, warning and destructive states. The views are the manager's
    /// own source, compiled in here; only the data behind them is made up.
    /// </summary>
    internal static class SessionsPage
    {
        private static readonly List<Demo> Demos = new List<Demo>();

        public static void Build(TopiaForgeContainer page, UiHost host)
        {
            var fake = new GallerySessionScreens();
            page.SectionHeader("MULTIPLAYER SESSIONS");
            page.Label(
                "The Multiplayer tab and its overlays, bound to made-up sessions. Nothing here contacts the network.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var states = page.Row(TopiaForgeGap.Sm);
            states.Button("MODS: IDENTIFYING", () => fake.ContentFake.State = ContentReadinessState.Pending, TopiaForgeButtonStyle.Outline);
            states.Button("MODS: UNREADABLE", () => fake.ContentFake.State = ContentReadinessState.Unreadable, TopiaForgeButtonStyle.Outline);
            states.Button("MODS: READY", () => fake.ContentFake.State = ContentReadinessState.Ready, TopiaForgeButtonStyle.Outline);
            states.Toggle("Live sessions", fake.IsAvailable, on => fake.IsAvailable = on);

            var session = page.Row(TopiaForgeGap.Sm);
            session.Button("SOLO", () => SetSession(fake, running: false, hosting: false), TopiaForgeButtonStyle.Ghost);
            session.Button("IN A GAME", () => SetSession(fake, running: true, hosting: false), TopiaForgeButtonStyle.Ghost);
            session.Button("HOSTING", () => SetSession(fake, running: true, hosting: true), TopiaForgeButtonStyle.Ghost);
            session.Toggle("Session ready", fake.SessionFake.Ready, on => fake.SessionFake.Ready = on);

            var joins = page.Row(TopiaForgeGap.Sm);
            joins.Button("JOIN", () => fake.ConnectingFake.Start("Sam's sandbox", failAt: -1), TopiaForgeButtonStyle.Outline);
            joins.Button("JOIN WITH PASSWORD", () => fake.ConnectingFake.Start("Locked session", failAt: -1, password: true), TopiaForgeButtonStyle.Outline);
            joins.Button("JOIN AND FAIL", () => fake.ConnectingFake.Start("Zombies night", failAt: (int)ConnectingStep.VerifyingMods), TopiaForgeButtonStyle.Outline);

            var prompts = page.Row(TopiaForgeGap.Sm);
            prompts.Button("DISCLOSURE", () => fake.Ask(PrivacyPrompts.Disclosure(ok => fake.Notify(ok ? "Acknowledged." : "Closed without acknowledging."))), TopiaForgeButtonStyle.Ghost);
            prompts.Button("DIRECT PROMPT", () => fake.Ask(PrivacyPrompts.Direct(ok => fake.Notify(ok ? "Allowed for this session." : "Nothing was contacted. " + MultiplayerPrivacyText.HideMyIp + " stayed on."))), TopiaForgeButtonStyle.Ghost);
            prompts.Button("LEAVE CURRENT GAME", () => fake.Ask(PrivacyPrompts.Destructive(
                "Leave current game?", "Joining this session ends the game you are playing. Anything you have not saved is lost.", "LEAVE AND JOIN",
                ok => fake.Notify(ok ? "Left the game." : "You stayed in your game."))), TopiaForgeButtonStyle.Ghost);

            var voice = page.Row(TopiaForgeGap.Sm);
            voice.Button("VOICE: READY", () => fake.VoiceFake.State = VoiceIndicator.Idle, TopiaForgeButtonStyle.Ghost);
            voice.Button("TALKING", () => fake.VoiceFake.State = VoiceIndicator.Talking, TopiaForgeButtonStyle.Ghost);
            voice.Button("MUTED BY HOST", () => fake.VoiceFake.State = VoiceIndicator.MutedByHost, TopiaForgeButtonStyle.Ghost);
            voice.Button("MIC BUSY", () => fake.VoiceFake.State = VoiceIndicator.MicBusy, TopiaForgeButtonStyle.Ghost);
            voice.Button("NO MIC", () => fake.VoiceFake.State = VoiceIndicator.NoMic, TopiaForgeButtonStyle.Ghost);

            var panel = page.Panel(TopiaForgePanelStyle.Sunken);
            panel.FixedHeight(620f);
            var content = panel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md);
            content.Stretch();
            var tab = new MultiplayerTabView(host, fake, fake.VoiceFake);
            tab.Build(content);
            Demos.Add(new Demo(page, fake, tab, new MultiplayerSessionsUi(host, fake)));
            SessionsPageHud.Build(page);
        }

        /// <summary>Advances the fake joins and binds the views; called once per frame by the gallery.</summary>
        public static void Tick(float deltaTime)
        {
            SessionsPageHud.Tick();
            var dt = float.IsNaN(deltaTime) || deltaTime < 0f ? 0f : Math.Min(deltaTime, 0.25f);
            for (var index = 0; index < Demos.Count; index++)
            {
                var demo = Demos[index];
                demo.Screens.Tick(dt);
                demo.Tab.Tick();
                demo.Overlays.Tick();
            }
        }

        /// <summary>Drops the views of a page the gallery is about to clear.</summary>
        public static void Detach(TopiaForgeContainer page)
        {
            SessionsPageHud.Detach(page);
            for (var index = Demos.Count - 1; index >= 0; index--)
            {
                if (!ReferenceEquals(Demos[index].Page, page)) continue;
                Demos[index].Overlays.Close();
                Demos.RemoveAt(index);
            }
        }

        public static void Reset()
        {
            SessionsPageHud.Detach();
            foreach (var demo in Demos) demo.Overlays.Close();
            Demos.Clear();
        }

        private static void SetSession(GallerySessionScreens fake, bool running, bool hosting)
        {
            fake.HostFake.Running = running;
            fake.HostFake.Hosting = hosting;
            fake.StatusText = hosting ? "Hosting: 4 of 8 players." : "Playing on your own.";
            fake.StatusTone = hosting ? ScreenTone.Success : ScreenTone.Muted;
        }

        private sealed class Demo
        {
            public Demo(TopiaForgeContainer page, GallerySessionScreens screens, MultiplayerTabView tab, MultiplayerSessionsUi overlays)
            {
                Page = page;
                Screens = screens;
                Tab = tab;
                Overlays = overlays;
            }

            public TopiaForgeContainer Page { get; }

            public GallerySessionScreens Screens { get; }

            public MultiplayerTabView Tab { get; }

            public MultiplayerSessionsUi Overlays { get; }
        }
    }
}
