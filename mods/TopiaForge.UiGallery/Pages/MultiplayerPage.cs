using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>
    /// The multiplayer kit widgets in their states: a sortable scoreboard table, signal bars, the
    /// joining phase stepper, a chat log, talk indicators, a microphone level meter driven by a
    /// synthetic voice, and name tags anchored to world points around the camera. Animated demos
    /// tick from the gallery's update callback; each built page registers its own demo and drops
    /// it when the tab changes.
    /// </summary>
    internal static partial class MultiplayerPage
    {
        private static readonly List<Demo> Demos = new List<Demo>();

        public static void Build(TopiaForgeContainer page, ILocalPlayerService player)
        {
            var demo = new Demo(page);
            Demos.Add(demo);

            page.SectionHeader("MULTIPLAYER KIT");
            page.Label(
                "Widgets for sessions, voice and chat. Every value below updates through dirty-checked setters; "
                + "toggle reduced motion and high contrast above to check the halo, stepper and fades.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            BuildScoreboard(page);
            BuildSignals(page);
            BuildStepper(page, demo);
            BuildChat(page);
            BuildVoice(page, demo);
            BuildNametags(page, player);
        }

        /// <summary>Advances every live demo; called once per frame by the gallery.</summary>
        public static void Tick(float deltaTime)
        {
            var dt = float.IsNaN(deltaTime) || deltaTime < 0f ? 0f : Math.Min(deltaTime, 0.25f);
            for (var index = 0; index < Demos.Count; index++)
            {
                Demos[index].Tick(dt);
            }

            TickNametags(dt);
        }

        /// <summary>Drops the demos of a page whose widgets are about to be cleared.</summary>
        public static void Detach(TopiaForgeContainer page)
        {
            for (var index = Demos.Count - 1; index >= 0; index--)
            {
                if (ReferenceEquals(Demos[index].Page, page))
                {
                    Demos.RemoveAt(index);
                }
            }
        }

        public static void Reset()
        {
            Demos.Clear();
            DestroyNametags();
        }

        private static void BuildSignals(TopiaForgeContainer page)
        {
            page.SectionHeader("SIGNAL BARS");
            page.Label(
                "Four bars toned by strength; unknown draws flat stubs, so it differs from zero bars by shape.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var levels = page.Row(TopiaForgeGap.Lg);
            for (var bars = 4; bars >= -1; bars--)
            {
                var cell = levels.Column(TopiaForgeGap.Xs, TopiaForgeGap.None, expandChildWidth: false);
                cell.SignalBars().SetBars(bars);
                cell.Label(bars < 0 ? "UNKNOWN" : bars + (bars == 1 ? " BAR" : " BARS"), TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            }

            var live = page.Row(TopiaForgeGap.Md);
            var bar = live.SignalBars().ShowMilliseconds();
            bar.SetPing(48);
            live.Slider("Ping", 0f, 500f, 48f, value => bar.SetPing((int)Math.Round(value / 10f) * 10)).Flex(1f, 0f);
            live.Button("UNKNOWN", bar.SetUnknown, TopiaForgeButtonStyle.Ghost);
        }

        private sealed partial class Demo
        {
            public Demo(TopiaForgeContainer page)
            {
                Page = page;
            }

            public TopiaForgeContainer Page { get; }

            public void Tick(float deltaTime)
            {
                TickStepper(deltaTime);
                TickVoice(deltaTime);
            }
        }
    }
}
