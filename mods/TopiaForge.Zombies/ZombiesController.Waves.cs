using System;
using System.Globalization;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    internal sealed partial class ZombiesController
    {
        private void BeginNextWave()
        {
            shop.Close();
            phase = ZombiesPhase.Wave;
            wave++;
            pendingSpawns = ZombiesRuntimeMath.WaveSize(
                config.BaseZombiesPerWave,
                config.ZombiesPerWaveIncrement,
                wave);
            packRemaining = 0;
            spawnTimer = 0f;
            consecutiveSpawnFailures = 0;
            spawnFailureWarningLogged = false;
            context.Ui.ShowToast("Wave " + wave.ToString(CultureInfo.InvariantCulture), UiTone.Warning);
        }

        private void BeginInterWave()
        {
            phase = ZombiesPhase.InterWave;
            phaseTimer = config.InterWaveDelaySeconds;
            CancelSpawnSearch();
            context.Ui.ShowToast(
                config.ShopEnabled ? "Wave clear. Requisitions available." : "Wave clear.",
                UiTone.Success);
        }

        private void AdvanceSpawning(float worldDelta)
        {
            // The reachable-spawn search is a bounded candidate loop pumped by RobotKit's own tick and a stalled
            // one is recoverable through Restart, so it relies on cancellation rather than a deadline.
            switch (spawnSearch.Poll(
                (float)context.Time.Frame.ElapsedTime,
                float.PositiveInfinity,
                out var result))
            {
                case PendingOperationState.Completed:
                    if (result.TryGetValue(out var placement)
                        && placement != null
                        && TrySpawnEnemy(placement.Position, requestedSpawnKind))
                    {
                        pendingSpawns = Math.Max(0, pendingSpawns - 1);
                        if (packRemaining > 0)
                        {
                            packRemaining--;
                        }

                        consecutiveSpawnFailures = 0;
                        hordePressure = Math.Max(0f, hordePressure - 0.05f);
                        spawnTimer = EffectiveSpawnInterval;
                    }
                    else
                    {
                        HandleSpawnFailure(result.ErrorMessage);
                    }

                    break;

                case PendingOperationState.Waiting:
                    return;
            }

            if (spawnSearch.IsInFlight || pendingSpawns <= 0 || CountActiveNonAllies() >= EffectiveAliveCap)
            {
                return;
            }

            spawnTimer = Math.Max(0f, spawnTimer - worldDelta);
            if (spawnTimer > 0f
                || !context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
            {
                return;
            }

            requestedSpawnKind = ChooseSpawnKind();
            spawnSearch.Begin(
                token => robots.FindReachableSpawnAsync(
                    new ReachableSpawnRequest(
                        player.Position,
                        player.Position,
                        config.MinSpawnDistance,
                        config.SpawnRadius,
                        config.SpawnSearchAttempts,
                        verticalScan: 4f,
                        groundProbeDepth: 16f,
                        heightOffset: config.SpawnHeightOffset),
                    token),
                context.Lifetime.StoppingToken,
                (float)context.Time.Frame.ElapsedTime);
        }

        private ZombieKind ChooseSpawnKind()
        {
            if (packRemaining > 0)
            {
                return packKind;
            }

            var selected = config.ArchetypesEnabled ? roster.PickKind(wave, random) : ZombieKind.Grunt;
            if (selected == ZombieKind.Brute && CountKind(ZombieKind.Brute) >= config.BruteMaxAlive)
            {
                selected = ZombieKind.Grunt;
            }

            var archetype = roster.Get(selected);
            if (archetype.IsPack)
            {
                var size = random.Next(archetype.PackMin, archetype.PackMax + 1);
                packKind = selected;
                packRemaining = Math.Min(pendingSpawns, size);
            }

            return selected;
        }

        private bool TrySpawnEnemy(Vec3 position, ZombieKind kind)
        {
            if (!robots.IsAvailable
                || (playerEntity?.IsAlive != true && !usingPositionalPlayerFallback))
            {
                return false;
            }

            var archetype = roster.Get(kind);
            var request = new RobotAgentSpawnRequest(
                position,
                brainMode: RobotBrainMode.Dormant,
                gait: archetype.Gait,
                moveSpeed: archetype.MoveSpeed,
                turnSpeed: config.ZombieTurnSpeed,
                stopDistance: archetype.StopDistance,
                tint: archetype.Tint,
                name: "Infected " + archetype.DisplayName,
                scale: archetype.Scale,
                interaction: RobotInteractionOptions.DisableNativeTalk());
            var spawned = robots.Spawn(request);
            if (!spawned.TryGetValue(out var agent) || agent == null)
            {
                context.Logger.Warn("Zombies could not spawn " + archetype.DisplayName + ": " + spawned.ErrorMessage);
                return false;
            }

            var mind = RobotMind.Seed(random, wave, OverrideTuning());
            var enemy = new ZombieEnemy(agent, archetype, mind, config.EnableEnemyEmotes);
            enemies.Add(enemy);
            if (config.EnableEnemyEmotes)
            {
                agent.SetEmote(archetype.Emote);
            }

            if (playerEntity?.IsAlive == true)
            {
                agent.Chase(playerEntity);
            }
            else if (usingPositionalPlayerFallback
                && context.LocalPlayer.TryGetSnapshot(out var player) && player != null)
            {
                agent.MoveTo(player.Position);
            }

            return true;
        }

        private void HandleSpawnFailure(string message)
        {
            consecutiveSpawnFailures++;
            spawnTimer = config.SpawnIntervalSeconds;
            if (consecutiveSpawnFailures < MaximumConsecutiveSpawnFailures)
            {
                return;
            }

            pendingSpawns = Math.Max(0, pendingSpawns - 1);
            if (packRemaining > 0)
            {
                packRemaining--;
            }

            consecutiveSpawnFailures = 0;
            if (!spawnFailureWarningLogged)
            {
                spawnFailureWarningLogged = true;
                context.Diagnostics.Report(new DiagnosticEntry(
                    "ZOMBIES_SPAWN_SKIPPED",
                    "Zombies skipped an unreachable spawn so the wave could continue.",
                    DiagnosticSeverity.Warning,
                    message));
                context.Ui.ShowToast("An unreachable horde route was skipped.", UiTone.Warning);
            }
        }

        private void UpdateEnemyLifecycles(float worldDelta)
        {
            for (var index = enemies.Count - 1; index >= 0; index--)
            {
                var enemy = enemies[index];
                var wasActive = enemy.IsActive;
                if (enemy.Tick(worldDelta, config))
                {
                    if (wasActive && !enemy.Scored)
                    {
                        context.Logger.Info("Zombies removed an externally defeated or missing infected robot.");
                    }

                    enemy.Dispose();
                    enemies.RemoveAt(index);
                }
            }
        }
    }
}
