using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Zapper input, hit resolution, custom health, and scoring.</summary>
    internal sealed partial class ZombiesController
    {
        private const float ChargedShotRadius = 1.1f;

        /// <summary>
        /// Advances the timers that gate <em>player</em> actions. These stay on the control clock because the
        /// player is deliberately exempt from Chronos scaling, so their inputs must keep responding while the world
        /// is slowed or frozen.
        /// </summary>
        private void AdvanceControlTimers(float controlDelta)
        {
            fireCooldown = Math.Max(0f, fireCooldown - controlDelta);
            if (comboTimer > 0f)
            {
                comboTimer = Math.Max(0f, comboTimer - controlDelta);
                if (comboTimer <= 0f)
                {
                    comboCount = 0;
                    comboMultiplier = 1;
                }
            }
        }

        /// <summary>
        /// Advances the uplink economy. This is world-scaled on purpose: a stand-down broadcast freezes the world,
        /// and on the control clock its own cooldown and charge regeneration kept running through the freeze it
        /// created, so the player could chain broadcasts and never face a moving horde.
        /// </summary>
        private void AdvanceWorldTimers(float worldDelta)
        {
            broadcastCooldown = Math.Max(0f, broadcastCooldown - worldDelta);

            var maximum = MaximumUplinkCharges;
            uplinkCharges = Math.Min(uplinkCharges, maximum);
            if (uplinkCharges >= maximum)
            {
                uplinkRegenTimer = 0f;
                return;
            }

            uplinkRegenTimer += worldDelta;
            while (uplinkRegenTimer >= config.OverrideChargeRegenSeconds && uplinkCharges < maximum)
            {
                uplinkRegenTimer -= config.OverrideChargeRegenSeconds;
                uplinkCharges++;
            }
        }

        private void UpdateCombatInput(float controlDelta)
        {
            if (phase != ZombiesPhase.Wave)
            {
                charging = false;
                chargeSeconds = 0f;
                return;
            }

            if (config.ChargeShotEnabled)
            {
                if (fireAction?.WasPressed == true && fireCooldown <= 0f)
                {
                    charging = true;
                    chargeSeconds = 0f;
                }

                if (charging && fireAction?.IsHeld == true)
                {
                    chargeSeconds = Math.Min(config.ChargeShotSeconds, chargeSeconds + controlDelta);
                }

                if (charging && fireAction?.WasReleased == true)
                {
                    var charged = chargeSeconds >= config.ChargeShotSeconds;
                    charging = false;
                    chargeSeconds = 0f;
                    if (fireCooldown <= 0f)
                    {
                        FireZapper(charged);
                    }
                }
                else if (charging && fireAction?.IsHeld != true)
                {
                    // UI focus or a modal can consume the release edge. Never leave a stale charge latched.
                    CancelCharge();
                }
            }
            else if (fireAction?.WasPressed == true && fireCooldown <= 0f)
            {
                FireZapper(charged: false);
            }

            if (overrideAction?.WasPressed == true)
            {
                TryJackIn();
            }

            if (broadcastAction?.WasPressed == true)
            {
                TryBroadcastStandDown(showFeedback: true);
            }
        }

        private void CancelCharge()
        {
            charging = false;
            chargeSeconds = 0f;
        }

        private void FireZapper(bool charged)
        {
            if (!context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
            {
                return;
            }

            var damage = (charged ? config.ChargeShotDamage : config.ZapperDamage)
                * shop.Upgrades.ZapperDamageMult;
            fireCooldown = (charged ? config.ChargeShotCooldownSeconds : config.ZapperCooldownSeconds)
                * shop.Upgrades.ZapperCooldownMult;
            context.Audio.Play(new AudioPlayRequest(charged ? "zombies.zapper.charged" : "zombies.zapper", 0.7f));

            if (charged && config.ChargeShotPierces)
            {
                FirePiercingShot(player.AimRay, damage);
                return;
            }

            if (!TryGetAimTarget(player.AimRay, out var enemy, out var hit) || enemy == null || hit == null)
            {
                return;
            }

            var headshot = ZombiesRuntimeMath.IsHeadshot(enemy.Agent, enemy.Archetype, hit.Point);
            if (headshot)
            {
                damage *= config.HeadshotDamageMultiplier;
            }

            DamageEnemy(enemy, damage, headshot, playerKill: true, "SDK zapper");
            ApplyHitReaction(enemy, player.AimRay.Direction, damage, charged);
        }

        private void FirePiercingShot(Ray ray, float damage)
        {
            for (var index = 0; index < enemies.Count; index++)
            {
                var enemy = enemies[index];
                if (!enemy.IsActive
                    || !ZombiesRuntimeMath.IsNearRay(
                        ray,
                        enemy.Agent.HeadPosition,
                        config.ZapperRange,
                        ChargedShotRadius * enemy.Archetype.Scale))
                {
                    continue;
                }

                DamageEnemy(enemy, damage, headshot: false, playerKill: true, "charged SDK zapper");
                ApplyHitReaction(enemy, ray.Direction, damage, charged: true);
            }
        }

        private void ApplyHitReaction(ZombieEnemy enemy, Vec3 direction, float damage, bool charged)
        {
            if (!enemy.IsActive)
            {
                return;
            }

            if (charged || damage >= enemy.Archetype.Health * config.BigHitRagdollFraction)
            {
                enemy.Agent.Ragdoll();
            }
            else if (!enemy.Archetype.IgnoreLightKnockback)
            {
                enemy.Agent.Knockback(direction * config.ZapperImpactForce);
            }
        }

        private bool TryGetAimTarget(Ray ray, out ZombieEnemy? enemy, out PhysicsHit? hit)
        {
            enemy = null;
            hit = null;
            if (!context.Physics.TryRaycast(ray, config.ZapperRange, out var found)
                || found == null
                || !robots.TryGetRobot(found.Entity, out var agent)
                || agent == null)
            {
                return false;
            }

            for (var index = 0; index < enemies.Count; index++)
            {
                var candidate = enemies[index];
                if (candidate.IsActive
                    && (ReferenceEquals(candidate.Agent, agent)
                        || string.Equals(candidate.Agent.Id, agent.Id, StringComparison.Ordinal)))
                {
                    enemy = candidate;
                    hit = found;
                    return true;
                }
            }

            return false;
        }

        private bool DamageEnemy(
            ZombieEnemy enemy,
            float damage,
            bool headshot,
            bool playerKill,
            string source)
        {
            if (!enemy.IsActive)
            {
                return false;
            }

            if (enemy.IsAlly && playerKill)
            {
                enemy.PenalizeLoyalty(config.LoyaltyShotPenalty, config);
            }

            if (!enemy.ApplyDamage(damage, playerKill))
            {
                return false;
            }

            enemy.MarkDefeated(RobotDamageType.Electricity, source);
            if (playerKill && !enemy.Scored)
            {
                enemy.Scored = true;
                AwardKill(enemy, headshot);
            }

            return true;
        }

        private void AwardKill(ZombieEnemy enemy, bool headshot)
        {
            comboCount = ZombiesRuntimeMath.SaturatingAdd(comboCount, 1);
            var previousMultiplier = comboMultiplier;
            comboMultiplier = ZombiesRuntimeMath.ComboMultiplier(
                comboCount,
                config.ComboKillsPerTier,
                config.ComboMaxMultiplier);
            comboTimer = config.ComboWindowSeconds + shop.Upgrades.ComboWindowBonusSeconds;
            var baseScore = ZombiesRuntimeMath.SaturatingMultiply(enemy.Archetype.Score, comboMultiplier);
            var awarded = ZombiesRuntimeMath.SaturatingAdd(
                baseScore,
                headshot ? config.HeadshotFlatBonusScore : 0);
            score = ZombiesRuntimeMath.SaturatingAdd(score, awarded);
            shop.AwardScore(awarded);
            if (comboMultiplier > previousMultiplier)
            {
                context.Ui.ShowToast("Kill chain x" + comboMultiplier + " online.", UiTone.Success);
            }
        }
    }
}
