using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    internal sealed partial class ZombiesController
    {
        private void SynchronizeConversationMotionPause()
        {
            if (conversation.IsOpen)
            {
                if (!conversation.IsWorldFrozen && !hordeMotionSuspendedForConversation)
                {
                    hordeMotionSuspendedForConversation = true;
                    foreach (var enemy in enemies)
                    {
                        if (!enemy.IsActive)
                        {
                            continue;
                        }

                        enemy.Agent.Stop();
                        if (enemy.IsAlly)
                        {
                            // AdvanceAlly only issues a new Chase when the target id changes. Clear it so the first
                            // post-channel frame deterministically reacquires and resumes the nearest hostile.
                            enemy.AllyTargetId = string.Empty;
                        }
                    }
                }

                return;
            }

            if (!hordeMotionSuspendedForConversation)
            {
                return;
            }

            hordeMotionSuspendedForConversation = false;
            foreach (var enemy in enemies)
            {
                if (!enemy.IsActive)
                {
                    continue;
                }

                if (enemy.IsAlly)
                {
                    enemy.AllyTargetId = string.Empty;
                }
                else if (enemy.State == HijackState.Fleeing)
                {
                    // Hostiles and allies are rebound by AdvanceEnemies below. Fleeing robots otherwise have only
                    // their one-shot escape intent, so explicitly restore that intent after the fallback pause.
                    MoveEnemyAway(enemy);
                }
            }
        }

        private void AdvanceEnemies(float worldDelta)
        {
            if (!context.Player.TryGetSnapshot(out var player) || player == null
                || (playerEntity?.IsAlive != true && !usingPositionalPlayerFallback))
            {
                return;
            }

            for (var index = 0; index < enemies.Count; index++)
            {
                var enemy = enemies[index];
                if (!enemy.IsActive)
                {
                    continue;
                }

                enemy.AttackCooldown = Math.Max(0f, enemy.AttackCooldown - worldDelta);
                enemy.AllyAttackCooldown = Math.Max(0f, enemy.AllyAttackCooldown - worldDelta);
                enemy.AllyRetargetTimer = Math.Max(0f, enemy.AllyRetargetTimer - worldDelta);
                if (enemy.IsAlly)
                {
                    AdvanceAlly(enemy);
                }
                else if (enemy.IsHostile)
                {
                    AdvanceHostile(enemy, player, worldDelta);
                    if (phase != ZombiesPhase.Wave)
                    {
                        break;
                    }
                }
                else if (enemy.State == HijackState.Frozen)
                {
                    enemy.Agent.Stop();
                }
            }
        }

        private void AdvanceHostile(ZombieEnemy enemy, PlayerSnapshot player, float worldDelta)
        {
            if (playerEntity?.IsAlive == true)
            {
                enemy.Agent.Chase(playerEntity);
            }
            else
            {
                enemy.Agent.MoveTo(player.Position);
            }

            var distance = Vec3.Distance(enemy.Agent.Position, player.Position);
            if (enemy.TrackProgress(
                    worldDelta,
                    distance > config.ZombieAttackRange,
                    StrandedTimeoutSeconds))
            {
                context.Logger.Warn("Zombies despawned an infected robot that could not reach the player.");
                enemy.MarkExternallyDefeated();
                return;
            }

            if (enemy.AttackCooldown > 0f || distance > config.ZombieAttackRange)
            {
                return;
            }

            enemy.AttackCooldown = enemy.Archetype.AttackCooldown;
            var damage = enemy.Archetype.AttackDamage
                * (enemy.State == HijackState.Enraged ? config.EnrageDamageMult : 1f);
            DamagePlayer(damage);
        }

        private void AdvanceAlly(ZombieEnemy ally)
        {
            var target = ally.AllyRetargetTimer > 0f
                ? FindHostileById(ally.AllyTargetId)
                : null;
            if (target == null)
            {
                target = FindNearestHostile(ally);
                ally.AllyRetargetTimer = config.AllyRetargetSeconds;
            }

            if (target == null)
            {
                if (ally.AllyTargetId.Length > 0)
                {
                    ally.Agent.Stop();
                }

                ally.AllyTargetId = string.Empty;
                return;
            }

            if (!string.Equals(ally.AllyTargetId, target.Agent.Id, StringComparison.Ordinal))
            {
                ally.AllyTargetId = target.Agent.Id;
                ally.Agent.Chase(target.Agent);
            }

            if (ally.AllyAttackCooldown > 0f
                || Vec3.Distance(ally.Agent.Position, target.Agent.Position) > config.ZombieAttackRange)
            {
                return;
            }

            ally.AllyAttackCooldown = config.AllyAttackCooldownSeconds;
            if (DamageEnemy(target, config.AllyDamage, false, playerKill: false, "allied infected"))
            {
                ally.AddLoyalty(config.LoyaltyPerAssist, config);
            }
        }

        private ZombieEnemy? FindHostileById(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            for (var index = 0; index < enemies.Count; index++)
            {
                var candidate = enemies[index];
                if (candidate.IsHostile && string.Equals(candidate.Agent.Id, id, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        private ZombieEnemy? FindNearestHostile(ZombieEnemy self)
        {
            ZombieEnemy? nearest = null;
            var best = float.MaxValue;
            for (var index = 0; index < enemies.Count; index++)
            {
                var candidate = enemies[index];
                if (ReferenceEquals(candidate, self) || !candidate.IsHostile)
                {
                    continue;
                }

                var distance = (candidate.Agent.Position - self.Agent.Position).LengthSquared;
                if (distance < best)
                {
                    best = distance;
                    nearest = candidate;
                }
            }

            return nearest;
        }

    }
}
