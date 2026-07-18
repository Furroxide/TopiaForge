using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        // Scene transitions are asynchronous ownership boundaries: reject stale actions and cancel on teardown.
        private static void ReturnToMenuLoadsTheMenuScene()
        {
            var config = FastConfig();
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            harness.Controller.TestingDamagePlayer(config.PlayerIntegrity);
            Assert(harness.Controller.TestingPhase == ZombiesPhase.GameOver,
                "depleted integrity should open game-over actions");

            var gameOver = FindSurface(harness.Context, "zombies-game-over");
            Assert(gameOver.ActivateButton("zombies-game-over-return").Succeeded
                && harness.Context.Ui.Modals.Count == 1,
                "return-to-menu requires an explicit destructive confirmation");
            harness.Context.Ui.Modals[0].Confirm();
            harness.Advance(0.01f);
            Assert(string.Equals(harness.Context.Scenes.ActiveScene, GameScenes.MainMenuSceneName, StringComparison.Ordinal)
                && harness.Controller.TestingPhase == ZombiesPhase.ReturningToMenu,
                "confirmed return loads the real main-menu scene and remains transition-safe");
        }

        private static void RestartIsRejectedDuringPendingMenuReturn()
        {
            var config = FastConfig();
            var completion = new TaskCompletionSource<OperationResult<SceneSnapshot>>();
            CancellationToken observedToken = default;
            using var harness = new Harness(
                config,
                returnToMenu: cancellationToken =>
                {
                    observedToken = cancellationToken;
                    return completion.Task;
                });
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            harness.Controller.TestingDamagePlayer(config.PlayerIntegrity);
            var gameOver = FindSurface(harness.Context, "zombies-game-over");
            gameOver.ActivateButton("zombies-game-over-return");
            harness.Context.Ui.Modals[0].Confirm();

            Assert(harness.Controller.TestingPhase == ZombiesPhase.ReturningToMenu
                && !harness.Controller.Restart().Succeeded,
                "restart is rejected while a menu transition owns the session");
            harness.Controller.Dispose();
            Assert(observedToken.IsCancellationRequested,
                "controller teardown cancels its in-flight menu-return operation");
        }

        private static void ReturningPresentationFailureSuppressesStaleRestart()
        {
            var config = FastConfig();
            var completion = new TaskCompletionSource<OperationResult<SceneSnapshot>>();
            CancellationToken observedToken = default;
            using var harness = new Harness(
                config,
                returnToMenu: cancellationToken =>
                {
                    observedToken = cancellationToken;
                    return completion.Task;
                });
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            harness.Controller.TestingDamagePlayer(config.PlayerIntegrity);
            var gameOver = FindSurface(harness.Context, "zombies-game-over");
            Assert(gameOver.TryFindNode("zombies-game-over-restart", out var restartNode)
                && restartNode is UiButton,
                "the stale-action fixture should capture the original restart callback");
            var staleRestart = (UiButton)restartNode!;
            harness.Context.Ui.FailNextContentUpdate(
                "zombies-game-over",
                ModErrorCode.External,
                "synthetic returning composition failure");
            Assert(gameOver.ActivateButton("zombies-game-over-return").Succeeded
                && harness.Context.Ui.Modals.Count == 1,
                "the fixture should reach the destructive return confirmation");
            harness.Context.Ui.Modals[0].Confirm();

            Assert(harness.Controller.TestingPhase == ZombiesPhase.ReturningToMenu
                && gameOver.ActivateButton("zombies-game-over-restart").ErrorCode == ModErrorCode.NotFound,
                "failed returning composition should dispose the old actionable surface and continue the transition");
            staleRestart.Activated();
            Assert(harness.Controller.TestingPhase == ZombiesPhase.ReturningToMenu,
                "a renderer-delivered stale restart callback must be rejected once menu return owns the session");

            harness.Controller.Dispose();
            Assert(observedToken.IsCancellationRequested,
                "the pending transition fixture should still cancel cleanly after returning UI composition fails");
        }
    }
}
