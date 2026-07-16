using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        // Every modal/freeze path must recover after dependency failure and release ownership on teardown.
        private static void GameOverUiFailureRestartsSafely()
        {
            var config = FastConfig();
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            var blocker = harness.Context.Ui.CreateSurface(new UiSurfaceRequest(
                "zombies-game-over",
                "Occupied",
                "Regression conflict",
                UiSurfaceKind.Window));
            Assert(blocker.Succeeded, "the game-over conflict fixture should be installed");

            harness.Controller.TestingDamagePlayer(config.PlayerIntegrity);
            var reported = false;
            foreach (var diagnostic in harness.Context.Diagnostics.GetSnapshot())
            {
                if (string.Equals(diagnostic.Entry.Code, "ZOMBIES_GAME_OVER_UI_FAILED", StringComparison.Ordinal))
                {
                    reported = true;
                    break;
                }
            }

            Assert(reported
                && harness.Controller.TestingPhase == ZombiesPhase.WaitingForWorld
                && harness.Controller.TestingIntegrity == config.PlayerIntegrity
                && harness.Context.Player.ActiveControlLeaseCount == 0,
                "a failed game-over surface reports diagnostics and recovers instead of trapping a frozen run");
        }

        private static void StaleGameOverFreezeRecoversAfterChronosReset()
        {
            var config = FastConfig();
            using var harness = new Harness(config, withChronos: true);
            var chronos = harness.Chronos!;
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            harness.Controller.TestingDamagePlayer(config.PlayerIntegrity);
            Assert(harness.Controller.TestingPhase == ZombiesPhase.GameOver
                && chronos.ActiveLeaseCount == 1
                && chronos.IsFrozen,
                "game over should own one Chronos freeze");

            chronos.ForceReset();
            Assert(chronos.ActiveLeaseCount == 0 && !chronos.IsFrozen,
                "the reset fixture should invalidate the retained game-over handle");
            harness.Advance(0.01f);

            Assert(chronos.ActiveLeaseCount == 1 && chronos.IsFrozen,
                "game over must detect and reacquire a stale Chronos freeze");
        }

        private static void GameOverControlRetryDoesNotSpamDiagnostics()
        {
            var config = FastConfig();
            using var harness = new Harness(config, withChronos: true);
            harness.Chronos!.IsAvailable = false;
            harness.Context.Player.AcquireControlErrorCode = ModErrorCode.Unavailable;
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            harness.Controller.TestingDamagePlayer(config.PlayerIntegrity);

            for (var frame = 0; frame < 20; frame++)
            {
                harness.Advance(0.1f);
            }

            Assert(CountDiagnostics(harness.Context, "ZOMBIES_GAME_OVER_CONTROL_FAILED") == 1,
                "background control retries should report one actionable diagnostic instead of one per frame");

            harness.Context.Player.AcquireControlErrorCode = ModErrorCode.None;
            for (var frame = 0; frame < 6; frame++)
            {
                harness.Advance(0.1f);
            }

            Assert(harness.Context.Player.ActiveControlLeaseCount == 1,
                "the bounded retry loop should recover through the player-control fallback when it becomes available");
        }

        private static void StaleShopFreezeRecoversAfterChronosReset()
        {
            var config = FastConfig();
            config.ShopEnabled = true;
            config.StartingCountdownSeconds = 10f;
            using var harness = new Harness(config, withChronos: true);
            var chronos = harness.Chronos!;
            harness.Advance(0.01f);
            harness.Context.Input.SetValue("field-requisitions", 1f);
            harness.Advance(0.01f);
            harness.Context.Input.SetValue("field-requisitions", 0f);
            Assert(FindSurface(harness.Context, "zombies-requisitions").IsVisible
                && chronos.ActiveLeaseCount == 1
                && chronos.IsFrozen,
                "open requisitions should own one Chronos freeze");

            chronos.ForceReset();
            harness.Advance(0.01f);
            Assert(chronos.ActiveLeaseCount == 1 && chronos.IsFrozen,
                "an open requisitions window must replace its stale freeze after a Chronos reset");
        }

        private static void PauseFailuresAreBackedOffAndReportedOnce()
        {
            var shopConfig = FastConfig();
            shopConfig.ShopEnabled = true;
            shopConfig.StartingCountdownSeconds = 10f;
            using (var shopHarness = new Harness(shopConfig))
            {
                shopHarness.Advance(0.01f);
                shopHarness.Context.Player.AcquireControlErrorCode = ModErrorCode.Unavailable;
                shopHarness.Context.Input.SetValue("field-requisitions", 1f);
                shopHarness.Advance(0.01f);
                shopHarness.Context.Input.SetValue("field-requisitions", 0f);
                for (var frame = 0; frame < 20; frame++)
                {
                    shopHarness.Advance(0.05f);
                }

                Assert(CountDiagnostics(shopHarness.Context, "ZOMBIES_REQUISITIONS_PAUSE_FAILED") == 1,
                    "requisitions pause failures should produce one diagnostic across bounded background retries");
                shopHarness.Context.Player.AcquireControlErrorCode = ModErrorCode.None;
                shopHarness.Advance(0.6f);
                Assert(shopHarness.Context.Player.ActiveControlLeaseCount == 1,
                    "requisitions should reacquire its fallback control lease after the retry backoff");
            }

            var conversationConfig = FastConfig();
            conversationConfig.OverrideEnabled = true;
            conversationConfig.UseLiveBrain = true;
            conversationConfig.ConversationEnabled = true;
            using (var conversationHarness = new Harness(conversationConfig))
            {
                conversationHarness.Advance(0.01f);
                conversationHarness.Controller.TestingSetWavePhase();
                Assert(conversationHarness.Controller.TestingSpawn(
                        ZombieKind.Grunt,
                        new Vec3(0f, 0f, 1f)),
                    "a JACK IN pause-retry target should spawn");
                AimAtProxy(
                    conversationHarness,
                    (FakeRobotAgent)conversationHarness.Robots.Agents.ActiveAgents[0]);
                conversationHarness.Context.Player.AcquireControlErrorCode = ModErrorCode.Unavailable;
                conversationHarness.Context.Input.SetValue("jack-in", 1f);
                conversationHarness.Advance(0.01f);
                conversationHarness.Context.Input.SetValue("jack-in", 0f);
                for (var frame = 0; frame < 20; frame++)
                {
                    conversationHarness.Advance(0.05f);
                }

                Assert(CountDiagnostics(conversationHarness.Context, "ZOMBIES_JACK_IN_PAUSE_FAILED") == 1,
                    "JACK IN pause failures should produce one diagnostic across bounded background retries");
                conversationHarness.Context.Player.AcquireControlErrorCode = ModErrorCode.None;
                conversationHarness.Advance(0.6f);
                Assert(conversationHarness.Context.Player.ActiveControlLeaseCount == 1,
                    "JACK IN should reacquire its fallback control lease after the retry backoff");
            }
        }

        private static void StaleSuperhotLeasesReacquireAfterRestart()
        {
            var config = FastConfig();
            config.SuperhotMode = true;
            using var harness = new Harness(config, withChronos: true);
            harness.Advance(0.01f);
            Assert(harness.Chronos!.ActiveLeaseCount == 2,
                "Superhot should own one driver and one player-exemption lease");

            harness.Chronos.ForceReset();
            Assert(harness.Controller.Restart().Succeeded,
                "a Superhot run should restart after its Chronos leases are invalidated");
            harness.Advance(0.01f);

            Assert(harness.Chronos.ActiveLeaseCount == 2,
                "restart must replace both stale Superhot leases");
        }

        private static void HudSkipsSteadyStateBodyWrites()
        {
            var context = new FakeModContext();
            var presenter = new ZombiesHudPresenter(context);
            var snapshot = new ZombiesHudSnapshot(
                ZombiesPhase.Wave,
                3,
                7,
                1,
                2,
                84,
                100,
                420,
                25,
                2,
                2,
                3,
                0,
                0,
                false,
                false);
            presenter.Update(snapshot, null, null, null, null);
            var surface = context.Ui.Surfaces[0];
            var firstBody = surface.Body;
            presenter.Update(snapshot, null, null, null, null);
            Assert(ReferenceEquals(firstBody, surface.Body),
                "an unchanged HUD snapshot must not build or assign another body string");

            presenter.ForceRefresh();
            presenter.Update(snapshot, null, null, null, null);
            Assert(!ReferenceEquals(firstBody, surface.Body),
                "the reference assertion detects a real forced body refresh");
            presenter.Dispose();
            context.Dispose();
            context.AssertNoLeaks();
        }
    }
}
