using System;
using System.Globalization;
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
            random = CreateRandom();
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

        /// <summary>
        /// Builds the run's RNG. A configured non-zero seed replays a fixed wave and archetype sequence; the
        /// default of 0 seeds from entropy, so the mod does not ship every player the same run forever.
        /// </summary>
        private Random CreateRandom() =>
            config.Seed != 0 ? new Random(config.Seed) : new Random();

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
            if (disposed || phase != ZombiesPhase.GameOver || returnOperation.IsInFlight)
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
                // Unscaled time, because this phase holds a world freeze — a scaled clock would stop the very
                // deadline that is supposed to rescue the player from a scene load that never settles.
                returnOperation.Begin(
                    returnToMenu,
                    context.Lifetime.StoppingToken,
                    (float)context.Time.Frame.ElapsedTime);
            }
            catch (Exception exception)
            {
                HandleReturnFailure(exception.Message);
            }
        }

        private void ProcessReturnTask()
        {
            switch (returnOperation.Poll(
                (float)context.Time.Frame.ElapsedTime,
                ReturnToMenuTimeoutSeconds,
                out var result))
            {
                case PendingOperationState.Completed when !result.Succeeded:
                    HandleReturnFailure(result.ErrorMessage);
                    break;

                case PendingOperationState.TimedOut:
                    // Without this the run is stuck in ReturningToMenu forever: Restart refuses that phase, the
                    // game-over window has no controls, and the freeze is reacquired every frame.
                    HandleReturnFailure(
                        "The main menu did not finish loading within "
                        + ReturnToMenuTimeoutSeconds.ToString("0", CultureInfo.InvariantCulture)
                        + " seconds.");
                    break;

                case PendingOperationState.Abandoned:
                    // A scene load we stopped wanting still landed. Nothing to release, but never treat a late
                    // result as the live one.
                    break;
            }
        }

        private void HandleReturnFailure(string message)
        {
            returnOperation.Cancel();
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

        private void AcquireGameOverControl() => gameOverPause.Request();

        private void EnsureGameOverControl(float controlDelta) => gameOverPause.Tick(controlDelta);

        private void ReleaseGameOverControl() => gameOverPause.Release();

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
