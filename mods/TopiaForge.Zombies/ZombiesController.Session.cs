using System;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    internal sealed partial class ZombiesController
    {
        private void RestartCore(bool showToast)
        {
            gameOverPresenter.Close();
            ReleaseGameOverControl();
            CancelSpawnSearch();
            CancelReturnToMenu();
            RestoreNativeHealth();
            conversation.Close();
            hordeMotionSuspendedForConversation = false;
            ClearEnemies();
            shop.Reset();
            random = new Random(RandomSeed);
            playerEntity = null;
            usingPositionalPlayerFallback = false;
            startingNativeHealth = null;
            startingNativeHealthEntity = null;
            maximumIntegrity = config.PlayerIntegrity;
            integrity = maximumIntegrity;
            phaseTimer = 0f;
            spawnTimer = 0f;
            fireCooldown = 0f;
            broadcastCooldown = 0f;
            uplinkRegenTimer = 0f;
            comboTimer = 0f;
            chargeSeconds = 0f;
            wave = 0;
            pendingSpawns = 0;
            packRemaining = 0;
            consecutiveSpawnFailures = 0;
            score = 0;
            comboCount = 0;
            comboMultiplier = 1;
            uplinkCharges = MaximumUplinkCharges;
            hordePressure = 0f;
            charging = false;
            playerEntityFallbackLogged = false;
            nativeHealthWarningLogged = false;
            spawnFailureWarningLogged = false;
            phase = ZombiesPhase.WaitingForWorld;
            hud.ForceRefresh();
            if (showToast)
            {
                context.Ui.ShowToast("Zombies run restarted.", UiTone.Success);
            }
        }

        private void RestartFromUi()
        {
            if (disposed)
            {
                return;
            }

            if (phase != ZombiesPhase.GameOver)
            {
                context.Logger.Warn("Zombies ignored a stale restart action outside game over.");
                return;
            }

            RestartCore(showToast: true);
        }

        private void EnterGameOver()
        {
            if (phase == ZombiesPhase.GameOver || phase == ZombiesPhase.ReturningToMenu || disposed)
            {
                return;
            }

            shop.Close();
            CancelSpawnSearch();
            phase = ZombiesPhase.GameOver;
            charging = false;
            foreach (var enemy in enemies)
            {
                if (enemy.IsActive)
                {
                    enemy.Agent.Stop();
                }
            }

            var shown = gameOverPresenter.Show(score, wave);
            if (!shown.Succeeded)
            {
                context.Diagnostics.Report(new DiagnosticEntry(
                    "ZOMBIES_GAME_OVER_UI_FAILED",
                    "The Zombies game-over UI could not open; the run was restarted safely.",
                    DiagnosticSeverity.Error,
                    shown.ErrorMessage));
                RestartCore(showToast: false);
                return;
            }

            AcquireGameOverControl();
            context.Audio.Play(new AudioPlayRequest("zombies.failure", 0.8f));
        }

        private void BeginReturnToMenu()
        {
            if (disposed || phase != ZombiesPhase.GameOver || returnTask != null)
            {
                return;
            }

            phase = ZombiesPhase.ReturningToMenu;
            var returning = gameOverPresenter.ShowReturning();
            if (!returning.Succeeded)
            {
                context.Logger.Warn("Zombies could not present the return-to-menu state: " + returning.ErrorMessage);
            }
            // Restore while the gameplay player is still present; a successful single-scene load may destroy it
            // before session teardown gets another chance.
            RestoreNativeHealth();
            try
            {
                returnCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    context.Lifetime.StoppingToken);
                returnTask = returnToMenu(returnCancellation.Token);
            }
            catch (Exception exception)
            {
                HandleReturnFailure(exception.Message);
            }
        }

        private void ProcessReturnTask()
        {
            if (returnTask == null || !returnTask.IsCompleted)
            {
                return;
            }

            var completed = returnTask;
            returnTask = null;
            returnCancellation?.Dispose();
            returnCancellation = null;
            var result = CompletedResult(completed);
            if (!result.Succeeded)
            {
                HandleReturnFailure(result.ErrorMessage);
            }
        }

        private void HandleReturnFailure(string message)
        {
            returnTask = null;
            returnCancellation?.Dispose();
            returnCancellation = null;
            phase = ZombiesPhase.GameOver;
            context.Logger.Warn("Zombies could not return to the menu: " + message);
            context.Ui.ShowToast("Return to menu failed. The run is still paused.", UiTone.Danger);
            var shown = gameOverPresenter.Show(score, wave);
            if (!shown.Succeeded)
            {
                context.Diagnostics.Report(new DiagnosticEntry(
                    "ZOMBIES_GAME_OVER_RECOVERY_FAILED",
                    "Return-to-menu recovery could not restore the game-over actions; the run was restarted safely.",
                    DiagnosticSeverity.Error,
                    shown.ErrorMessage));
                RestartCore(showToast: false);
            }
        }

        private void AcquireGameOverControl()
        {
            ReleaseGameOverControl(resetRetryState: false);
            if (time?.IsAvailable == true)
            {
                var frozen = time.Freeze("zombies-game-over", suspendPlayer: true);
                if (frozen.TryGetValue(out var lease))
                {
                    gameOverFreeze = lease;
                    gameOverControlRetryTimer = 0f;
                    gameOverControlFailureReported = false;
                    return;
                }

                if (!gameOverControlFailureReported)
                {
                    context.Logger.Warn("Zombies game-over freeze failed: " + frozen.ErrorMessage);
                }
            }

            var control = context.LocalPlayer.AcquireControl("Zombies game over");
            if (control.TryGetValue(out var fallback))
            {
                gameOverControl = fallback;
                gameOverControlRetryTimer = 0f;
                gameOverControlFailureReported = false;
            }
            else
            {
                gameOverControlRetryTimer = GameOverControlRetrySeconds;
                if (!gameOverControlFailureReported)
                {
                    gameOverControlFailureReported = true;
                    context.Diagnostics.Report(new DiagnosticEntry(
                        "ZOMBIES_GAME_OVER_CONTROL_FAILED",
                        "Zombies could not suspend player control at game over; it will retry in the background.",
                        DiagnosticSeverity.Warning,
                        control.ErrorMessage));
                }
            }
        }

        private void EnsureGameOverControl(float controlDelta)
        {
            if (gameOverFreeze?.IsActive == true || gameOverControl?.IsActive == true)
            {
                gameOverControlRetryTimer = 0f;
                return;
            }

            gameOverControlRetryTimer = Math.Max(
                0f,
                gameOverControlRetryTimer - Math.Max(0f, controlDelta));
            if (gameOverControlRetryTimer > 0f)
            {
                return;
            }

            AcquireGameOverControl();
        }

        private void ReleaseGameOverControl(bool resetRetryState = true)
        {
            gameOverFreeze?.Dispose();
            gameOverFreeze = null;
            gameOverControl?.Dispose();
            gameOverControl = null;
            if (resetRetryState)
            {
                gameOverControlRetryTimer = 0f;
                gameOverControlFailureReported = false;
            }
        }

        private void SetupSuperhot()
        {
            if (!config.SuperhotMode || time?.IsAvailable != true)
            {
                return;
            }

            if (superhotDriver?.IsActive == true && playerExemption?.IsActive == true)
            {
                return;
            }

            superhotDriver?.Dispose();
            superhotDriver = null;
            playerExemption?.Dispose();
            playerExemption = null;

            var driverResult = time.SetDriver("zombies-superhot", new SuperhotTimeDriver());
            if (!driverResult.TryGetValue(out var driver))
            {
                context.Logger.Warn("Zombies Superhot driver is unavailable: " + driverResult.ErrorMessage);
                return;
            }

            var exemptionResult = time.ExemptPlayer("zombies-superhot-player");
            if (!exemptionResult.TryGetValue(out var exemption))
            {
                driver.Dispose();
                context.Diagnostics.Report(new DiagnosticEntry(
                    "ZOMBIES_SUPERHOT_EXEMPTION_FAILED",
                    "Superhot mode was disabled because the player could not be exempted safely.",
                    DiagnosticSeverity.Warning,
                    exemptionResult.ErrorMessage));
                return;
            }

            superhotDriver = driver;
            playerExemption = exemption;
        }

    }
}
