using System;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    internal sealed partial class ZombiesController
    {
        private void CaptureNativeHealth()
        {
            if (startingNativeHealth != null
                || !robots.TryGetPlayerEntity(out var identity)
                || identity == null
                || !identity.IsAlive
                || !context.Player.TryGetHealth(out var health)
                || health == null)
            {
                return;
            }

            startingNativeHealth = health;
            startingNativeHealthEntity = identity;
        }

        private void RestoreNativeHealth()
        {
            var starting = startingNativeHealth;
            if (starting == null)
            {
                return;
            }

            if (!TryGetOwnedNativeHealth(out var current) || current == null)
            {
                context.Logger.Warn(
                    "Zombies skipped native-health restoration because the original player entity is unavailable.");
                return;
            }

            var missing = starting.Current - current.Current;
            if (missing > 0.001f)
            {
                var healed = context.Player.Heal(missing, "Zombies run cleanup");
                if (!healed.Succeeded)
                {
                    context.Logger.Warn("Zombies could not restore native player health: " + healed.ErrorMessage);
                }
            }
            else if (missing < -0.001f)
            {
                var damaged = context.Player.Damage(new PlayerDamageRequest(
                    Math.Min(-missing, Math.Max(0f, current.Current - starting.Current)),
                    "Zombies run cleanup"));
                if (!damaged.Succeeded)
                {
                    context.Logger.Warn("Zombies could not remove temporary native player health: " + damaged.ErrorMessage);
                }
            }
        }

        private void DamagePlayer(float damage)
        {
            integrity = Math.Max(0f, integrity - damage);
            CaptureNativeHealth();
            if (TryGetOwnedNativeHealth(out var native) && native != null)
            {
                var scaledDamage = maximumIntegrity <= 0f
                    ? damage
                    : damage / maximumIntegrity * native.Maximum;
                // Integrity is the gamemode's death authority. Mirror damage into native health for honest
                // health-dependent integrations, but retain a small reserve so the base game's death flow cannot
                // race the Zombies game-over/restart UI.
                var reserve = Math.Min(1f, native.Maximum * 0.05f);
                var nativeDamage = Math.Min(
                    Math.Max(0f, scaledDamage),
                    Math.Max(0f, native.Current - reserve));
                var result = nativeDamage > 0.001f
                    ? context.Player.Damage(new PlayerDamageRequest(nativeDamage, "infected robot"))
                    : OperationResult<PlayerHealthSnapshot>.Success(native);
                if (!result.Succeeded && !nativeHealthWarningLogged)
                {
                    nativeHealthWarningLogged = true;
                    context.Logger.Warn("Zombies could not apply native player damage: " + result.ErrorMessage);
                }
            }
            else if (!nativeHealthWarningLogged)
            {
                nativeHealthWarningLogged = true;
                context.Logger.Warn("Zombies native player health is unavailable; using gamemode integrity only.");
            }

            if (integrity <= 0f)
            {
                EnterGameOver();
            }
        }

        private void SyncNativeHealthToIntegrity()
        {
            CaptureNativeHealth();
            if (!TryGetOwnedNativeHealth(out var health) || health == null || maximumIntegrity <= 0f)
            {
                return;
            }

            var target = health.Maximum * Math.Max(0f, Math.Min(1f, integrity / maximumIntegrity));
            if (target > health.Current + 0.001f)
            {
                var healed = context.Player.Heal(target - health.Current, "Zombies field repair");
                if (!healed.Succeeded && !nativeHealthWarningLogged)
                {
                    nativeHealthWarningLogged = true;
                    context.Logger.Warn("Zombies could not synchronize native player health: " + healed.ErrorMessage);
                }
            }
        }

        private bool TryGetOwnedNativeHealth(out PlayerHealthSnapshot? health)
        {
            health = null;
            if (startingNativeHealthEntity == null
                || !robots.TryGetPlayerEntity(out var currentPlayer)
                || currentPlayer == null
                || !currentPlayer.IsAlive
                || !ReferenceEquals(currentPlayer, startingNativeHealthEntity))
            {
                return false;
            }

            return context.Player.TryGetHealth(out health) && health != null;
        }

        private void CancelSpawnSearch()
        {
            if (spawnCancellation != null)
            {
                try { spawnCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                spawnCancellation.Dispose();
                spawnCancellation = null;
            }

            spawnSearch = null;
        }

        private void CancelReturnToMenu()
        {
            if (returnCancellation != null)
            {
                try { returnCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                returnCancellation.Dispose();
                returnCancellation = null;
            }

            returnTask = null;
        }

        private void ClearEnemies()
        {
            for (var index = enemies.Count - 1; index >= 0; index--)
            {
                enemies[index].Dispose();
            }

            enemies.Clear();
        }

        private int CountActiveNonAllies()
        {
            var count = 0;
            for (var index = 0; index < enemies.Count; index++)
            {
                if (enemies[index].IsActive && !enemies[index].IsAlly)
                {
                    count++;
                }
            }

            return count;
        }

        private int CountHostiles()
        {
            var count = 0;
            for (var index = 0; index < enemies.Count; index++)
            {
                if (enemies[index].IsHostile)
                {
                    count++;
                }
            }

            return count;
        }

        private int CountAllies()
        {
            var count = 0;
            for (var index = 0; index < enemies.Count; index++)
            {
                if (enemies[index].IsAlly)
                {
                    count++;
                }
            }

            return count;
        }

        private int CountKind(ZombieKind kind)
        {
            var count = 0;
            for (var index = 0; index < enemies.Count; index++)
            {
                if (enemies[index].IsActive && !enemies[index].IsAlly
                    && enemies[index].Archetype.Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }

        private float WorldDelta(float fallback)
        {
            var value = time?.IsAvailable == true ? time.WorldDeltaTime : fallback;
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? 0f : value;
        }

        private float ControlDelta(float fallback)
        {
            var value = time?.IsAvailable == true
                ? time.ControlDeltaTime
                : context.Time.Frame.UnscaledDeltaTime;
            if (value <= 0f && fallback > 0f)
            {
                value = fallback;
            }

            return float.IsNaN(value) || float.IsInfinity(value) || value < 0f ? 0f : value;
        }

        private static OperationResult<T> CompletedResult<T>(Task<OperationResult<T>> task) where T : notnull
        {
            try
            {
                return task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                return OperationResult<T>.Failure(ModErrorCode.External, exception.Message);
            }
        }

        private int EffectiveAliveCap => Math.Min(
            160,
            config.MaxAliveZombies
                + (int)Math.Ceiling(config.MaxAliveZombies * hordePressure * config.PressureSpawnBoost));
        private float EffectiveSpawnInterval => Math.Max(
            0.05f,
            config.SpawnIntervalSeconds * (1f - (hordePressure * config.PressureSpawnBoost)));
        private int MaximumUplinkCharges => Math.Max(1, config.OverrideCharges + shop.Upgrades.BonusUplinkCharges);

        private OverrideTuning OverrideTuning() => new OverrideTuning(
            config.SuggestibilityMin,
            config.SuggestibilityMax,
            config.LoyaltyMin,
            config.LoyaltyMax,
            config.CorruptionBase,
            config.CorruptionPerWave,
            config.BiasAmplitude,
            config.OverrideDifficulty);

        private ConversationTuning ConversationTuningValues() => new ConversationTuning(
            config.ConvSeedBias,
            config.ConvertThreshold,
            config.ConvertResistanceWeight,
            config.ConvertNudge,
            config.StandDownNudge,
            config.FleeNudge,
            config.RefuseNudge,
            config.EnrageDispositionFloor);
    }
}
