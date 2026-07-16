using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    internal sealed partial class ZombiesController
    {
        private void Update(float eventDeltaTime)
        {
            if (disposed)
            {
                return;
            }

            var worldDelta = WorldDelta(eventDeltaTime);
            var controlDelta = ControlDelta(eventDeltaTime);
            ProcessReturnTask();
            if (phase == ZombiesPhase.ReturningToMenu)
            {
                EnsureGameOverControl(controlDelta);
                gameOverPresenter.Tick();
                RefreshHud();
                return;
            }

            if (phase == ZombiesPhase.WaitingForWorld)
            {
                if (IsWorldReady())
                {
                    BeginStartingCountdown();
                }

                RefreshHud();
                return;
            }

            if (phase == ZombiesPhase.GameOver)
            {
                EnsureGameOverControl(controlDelta);
                gameOverPresenter.Tick();
                RefreshHud();
                return;
            }

            shop.Tick(controlDelta);
            conversation.Tick(controlDelta);
            SynchronizeConversationMotionPause();
            if (conversation.IsOpen)
            {
                CancelCharge();
                hordePressure = Math.Min(1f, hordePressure + (config.PressureRampPerSecond * controlDelta));
                RefreshHud();
                return;
            }

            if (shop.IsOpen)
            {
                CancelCharge();
                RefreshHud();
                return;
            }

            if (!RefreshPlayerEntity())
            {
                CancelCharge();
                RefreshHud();
                return;
            }

            AdvanceControlTimers(controlDelta);
            UpdateCombatInput(controlDelta);
            SynchronizeConversationMotionPause();
            if (conversation.IsOpen)
            {
                CancelCharge();
                hordePressure = Math.Min(1f, hordePressure + (config.PressureRampPerSecond * controlDelta));
                RefreshHud();
                return;
            }

            UpdateEnemyLifecycles(worldDelta);

            if (phase == ZombiesPhase.Starting || phase == ZombiesPhase.InterWave)
            {
                if (shopAction?.WasPressed == true)
                {
                    var opened = shop.Open();
                    if (!opened.Succeeded)
                    {
                        context.Ui.ShowToast(opened.ErrorMessage, UiTone.Warning);
                    }
                }

                if (!shop.IsOpen)
                {
                    phaseTimer = Math.Max(0f, phaseTimer - controlDelta);
                    if (phaseTimer <= 0f)
                    {
                        BeginNextWave();
                    }
                }
            }
            else if (phase == ZombiesPhase.Wave)
            {
                if (worldDelta > 0f)
                {
                    AdvanceSpawning(worldDelta);
                    AdvanceEnemies(worldDelta);
                }

                if (phase == ZombiesPhase.Wave
                    && pendingSpawns <= 0 && spawnSearch == null && CountActiveNonAllies() == 0)
                {
                    BeginInterWave();
                }
            }

            RefreshHud();
        }

        private bool IsWorldReady()
        {
            if (!context.Scenes.TryGetActive(out var active) || active == null
                || (!string.IsNullOrWhiteSpace(session.SceneName)
                    && !string.Equals(active.Name, session.SceneName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (!robots.IsAvailable
                || !context.Player.TryGetSnapshot(out var player) || player == null
                || !RefreshPlayerEntity())
            {
                return false;
            }
            return true;
        }

        private bool RefreshPlayerEntity()
        {
            if (playerEntity?.IsAlive == true)
            {
                return true;
            }

            if (!(robots is IRobotPlayerEntitySource))
            {
                playerEntity = null;
                usingPositionalPlayerFallback = true;
                if (!playerEntityFallbackLogged)
                {
                    playerEntityFallbackLogged = true;
                    context.Diagnostics.Report(new DiagnosticEntry(
                        "ZOMBIES_PLAYER_ENTITY_FALLBACK",
                        "RobotKit does not expose stable player entity tracking; Zombies is using positional pursuit.",
                        DiagnosticSeverity.Warning,
                        "Update RobotKit for moving-target chase and native-health restoration."));
                }

                return true;
            }

            if (!robots.TryGetPlayerEntity(out var livePlayer) || livePlayer == null || !livePlayer.IsAlive)
            {
                playerEntity = null;
                usingPositionalPlayerFallback = false;
                return false;
            }

            playerEntity = livePlayer;
            usingPositionalPlayerFallback = false;
            for (var index = 0; index < enemies.Count; index++)
            {
                if (enemies[index].IsHostile)
                {
                    enemies[index].Agent.Chase(livePlayer);
                }
            }

            return true;
        }

        private void BeginStartingCountdown()
        {
            CaptureNativeHealth();
            SetupSuperhot();
            phase = ZombiesPhase.Starting;
            phaseTimer = config.StartingCountdownSeconds;
            context.Ui.ShowToast("Arena linked. Systems online.", UiTone.Success);
        }

    }
}
