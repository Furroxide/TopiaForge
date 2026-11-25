using System;
using TopiaForge.Mods;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        private static void InterWaveHudTracksAndFreezesControlTime()
        {
            var config = FastConfig();
            config.InterWaveDelaySeconds = 12f;
            config.ShopEnabled = true;
            using var harness = new Harness(config, withChronos: true);
            harness.Controller.TestingSetWavePhase();
            harness.Advance(.25f);

            var hud = FindSurface(harness.Context, "zombies-status");
            const string timerId = ZombiesHudPresenter.InterWaveTimerNodeId;
            Assert(harness.Controller.TestingPhase == ZombiesPhase.InterWave &&
                hud.TryFindNode(timerId, out var node) &&
                node is UiCountdown
                {
                    Format: UiCountdownFormat.MinutesSeconds,
                    Thresholds: { Warning: 10, Critical: 5, Direction: UiThresholdDirection.AtOrBelow }
                } &&
                hud.TryGetCountdownSeconds(timerId, out var seconds) && seconds == 12,
                "An inter-wave countdown starts from the real phase timer with the approved thresholds.");
            Assert(hud.TryFindNode(ZombiesHudPresenter.IntegrityNodeId, out var integrity) &&
                integrity is UiStatBar
                {
                    Value: 1000, Maximum: 1000,
                    Thresholds: { Warning: 500, Critical: 250, Direction: UiThresholdDirection.AtOrBelow }
                },
                "The status tree uses the real integrity maximum and AtOrBelow thresholds.");

            var activeTree = hud.Content;
            harness.Advance(2f);
            Assert(ReferenceEquals(hud.Content, activeTree) && hud.TryGetCountdownSeconds(timerId, out seconds) && seconds == 10,
                "Aligned clocks advance the visible countdown without replacing the tree.");

            harness.Chronos!.Advance(.25f);
            harness.Context.AdvanceFrame(TimeSpan.FromSeconds(2.25));
            Assert(!ReferenceEquals(hud.Content, activeTree) && hud.TryGetCountdownSeconds(timerId, out seconds) && seconds == 10,
                "A divergent UI clock re-anchors to the authoritative remaining whole second.");

            harness.Context.Input.SetValue("field-requisitions", 1f);
            harness.Advance(.25f);
            harness.Context.Input.SetValue("field-requisitions", 0f);
            var shop = FindSurface(harness.Context, "zombies-requisitions");
            Assert(shop.IsVisible && hud.TryFindNode(timerId, out node) && node is UiText { Text: "Next wave paused 0:10" },
                "The requisitions pause shows the exact frozen time and removes the active deadline.");
            var pausedTree = hud.Content;
            harness.Advance(8f);
            Assert(ReferenceEquals(hud.Content, pausedTree) && harness.Controller.TestingPhase == ZombiesPhase.InterWave,
                "Wall time neither rebuilds the paused readout nor advances the gameplay phase.");

            shop.Hide();
            harness.Advance(.25f);
            Assert(hud.TryFindNode(timerId, out node) && node is UiCountdown resumed &&
                resumed.EndsAt > harness.Context.Ui.ClockTime && hud.TryGetCountdownSeconds(timerId, out seconds) && seconds == 10,
                "Closing the pause resumes from the remaining gameplay time with a fresh deadline.");
            var resumedTree = hud.Content;
            harness.Advance(5f);
            Assert(ReferenceEquals(hud.Content, resumedTree) && hud.TryGetCountdownSeconds(timerId, out seconds) && seconds == 5,
                "The resumed countdown crosses the critical threshold without another authored tree.");
        }
    }
}
