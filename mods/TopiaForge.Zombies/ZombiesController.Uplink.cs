using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Deterministic and opt-in live-brain uplink mechanics.</summary>
    internal sealed partial class ZombiesController
    {
        private void TryJackIn()
        {
            if (!config.OverrideEnabled || phase != ZombiesPhase.Wave)
            {
                return;
            }

            if (!context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
            {
                context.Ui.ShowToast("JACK IN unavailable: player tracking is offline.", UiTone.Warning);
                return;
            }

            if (!TryGetAimTarget(player.AimRay, out var enemy, out _) || enemy == null)
            {
                context.Ui.ShowToast("No infected robot is targeted for JACK IN.", UiTone.Warning);
                return;
            }

            // A fully loyal ally has nothing left to stabilize. Keep this acknowledgement free even when the
            // battery is empty; otherwise checking a healthy ally can silently waste a scarce combat charge.
            if (enemy.IsAlly && enemy.Loyalty >= 1f)
            {
                context.Ui.ShowToast("Ally uplink is already fully stabilized.", UiTone.Success);
                return;
            }

            if (uplinkCharges <= 0)
            {
                context.Ui.ShowToast("No uplink charge is available for JACK IN.", UiTone.Warning);
                return;
            }

            if (enemy.IsAlly)
            {
                SpendUplinkCharge();
                enemy.AddLoyalty(0.25f, config);
                context.Ui.ShowToast("Ally uplink stabilized.", UiTone.Success);
                return;
            }

            if (conversation.IsAvailable)
            {
                var opened = conversation.Open(enemy, wave);
                if (opened.Succeeded)
                {
                    SpendUplinkCharge();
                    return;
                }

                context.Logger.Warn("Zombies live JACK IN could not open: " + opened.ErrorMessage);
            }

            SpendUplinkCharge();
            var resolution = OverrideDecision.Resolve(
                OverrideCommand.JoinMe,
                enemy.Mind,
                enemy.Archetype.BaseResistance,
                config.OverrideDifficulty);
            if (resolution.Outcome == HijackOutcome.Convert && CountAllies() >= config.MaxConvertedAllies)
            {
                resolution = new OverrideResolution(HijackOutcome.Freeze, false);
            }

            ApplyOverride(enemy, resolution, standDown: false);
        }

        private void SpendUplinkCharge()
        {
            uplinkCharges = Math.Max(0, uplinkCharges - 1);
            uplinkRegenTimer = 0f;
        }

        private void ResolveConversation(
            ZombieEnemy enemy,
            ConversationDecision decision,
            float disposition)
        {
            if (!enemy.IsActive)
            {
                return;
            }

            switch (decision)
            {
                case ConversationDecision.Convert:
                    if (CountAllies() < config.MaxConvertedAllies)
                    {
                        enemy.Convert(config, disposition);
                        context.Ui.ShowToast(enemy.Archetype.DisplayName + " chose to fight with you.", UiTone.Success);
                    }
                    else
                    {
                        enemy.Freeze(config.FreezeSeconds, standDown: false);
                        context.Ui.ShowToast("Ally link full; target is temporarily pacified.", UiTone.Warning);
                    }

                    break;
                case ConversationDecision.StandDown:
                    enemy.Freeze(config.StandDownSeconds, standDown: true);
                    context.Ui.ShowToast("Target powered down by choice.", UiTone.Success);
                    break;
                case ConversationDecision.Flee:
                    enemy.Flee(config.FleeSeconds);
                    MoveEnemyAway(enemy);
                    context.Ui.ShowToast("Target abandoned the swarm.", UiTone.Success);
                    break;
                case ConversationDecision.Refuse:
                    if (disposition <= config.EnrageDispositionFloor)
                    {
                        enemy.Enrage(config.EnrageSeconds, config);
                        context.Ui.ShowToast("Negotiation collapsed — target enraged.", UiTone.Danger);
                    }
                    else
                    {
                        context.Ui.ShowToast("Target refused. The channel is closed.", UiTone.Warning);
                    }

                    break;
                default:
                    ApplyOverride(
                        enemy,
                        OverrideDecision.Resolve(
                            OverrideCommand.JoinMe,
                            enemy.Mind,
                            enemy.Archetype.BaseResistance,
                            config.OverrideDifficulty),
                        standDown: false);
                    break;
            }
        }

        private OperationResult<string> TryBroadcastStandDown(bool showFeedback)
        {
            if (disposed || !config.OverrideEnabled || phase != ZombiesPhase.Wave)
            {
                return BroadcastFailure(
                    ModErrorCode.InvalidState,
                    "Stand-down is only available during an active wave.",
                    showFeedback);
            }

            if (broadcastCooldown > 0f)
            {
                return BroadcastFailure(
                    ModErrorCode.InvalidState,
                    "The stand-down transmitter is cooling down.",
                    showFeedback);
            }

            if (uplinkCharges < config.BroadcastChargeCost)
            {
                return BroadcastFailure(
                    ModErrorCode.Unavailable,
                    "Not enough uplink charge for stand-down.",
                    showFeedback);
            }

            if (!context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
            {
                return BroadcastFailure(
                    ModErrorCode.Unavailable,
                    "Stand-down unavailable: player tracking is offline.",
                    showFeedback);
            }

            var affected = 0;
            for (var index = 0; index < enemies.Count; index++)
            {
                var enemy = enemies[index];
                if (!enemy.IsHostile
                    || Vec3.Distance(enemy.Agent.Position, player.Position) > config.BroadcastRadius)
                {
                    continue;
                }

                var resolution = OverrideDecision.Resolve(
                    OverrideCommand.StandDown,
                    enemy.Mind,
                    enemy.Archetype.BaseResistance,
                    config.OverrideDifficulty);
                if (resolution.Outcome == HijackOutcome.Freeze)
                {
                    ApplyOverride(enemy, resolution, standDown: true);
                    affected++;
                }
            }

            if (affected == 0)
            {
                // The transmitter fired and found nothing, so the charge is not spent — but the cooldown still
                // applies. Without it this path costs nothing at all, and the player can hold the key as a free
                // proximity scanner until a robot wanders into range.
                broadcastCooldown = config.BroadcastCooldownSeconds;
                return BroadcastFailure(
                    ModErrorCode.NotFound,
                    "No hostile infected robots answered within broadcast range.",
                    showFeedback);
            }

            uplinkCharges -= config.BroadcastChargeCost;
            uplinkRegenTimer = 0f;
            broadcastCooldown = config.BroadcastCooldownSeconds;
            context.Audio.Play(new AudioPlayRequest("zombies.broadcast", 0.8f));
            var message = affected + " infected robot" + (affected == 1 ? " stood" : "s stood") + " down.";
            if (showFeedback)
            {
                context.Ui.ShowToast(message, UiTone.Success);
            }

            return OperationResult<string>.Success(message);
        }

        private OperationResult<string> BroadcastFailure(
            ModErrorCode errorCode,
            string message,
            bool showFeedback)
        {
            if (showFeedback)
            {
                context.Ui.ShowToast(message, UiTone.Warning);
            }

            return OperationResult<string>.Failure(errorCode, message);
        }

        private void ApplyOverride(ZombieEnemy enemy, OverrideResolution resolution, bool standDown)
        {
            switch (resolution.Outcome)
            {
                case HijackOutcome.Convert:
                    if (CountAllies() < config.MaxConvertedAllies)
                    {
                        enemy.Convert(config, ConversationDirector.SeedDisposition(enemy.Mind, enemy.Archetype.BaseResistance, ConversationTuningValues()));
                        context.Ui.ShowToast(enemy.Archetype.DisplayName + " joined your side.", UiTone.Success);
                    }
                    else
                    {
                        enemy.Freeze(config.FreezeSeconds, standDown: false);
                        context.Ui.ShowToast("Ally link full; target is temporarily pacified.", UiTone.Warning);
                    }

                    break;
                case HijackOutcome.Freeze:
                    enemy.Freeze(standDown ? config.StandDownSeconds : config.FreezeSeconds, standDown);
                    context.Ui.ShowToast(standDown ? "Stand-down acknowledged." : "Uplink interrupted the target.", UiTone.Warning);
                    break;
                case HijackOutcome.Flee:
                    enemy.Flee(config.FleeSeconds);
                    MoveEnemyAway(enemy);
                    context.Ui.ShowToast("Target broke from the swarm.", UiTone.Warning);
                    break;
                default:
                    if (resolution.Enraged)
                    {
                        enemy.Enrage(config.EnrageSeconds, config);
                    }

                    context.Ui.ShowToast(resolution.Enraged ? "Uplink rejected — target enraged." : "Uplink rejected.", UiTone.Danger);
                    break;
            }
        }

        private void MoveEnemyAway(ZombieEnemy enemy)
        {
            if (!context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
            {
                return;
            }

            var away = enemy.Agent.Position - player.Position;
            if (away.LengthSquared <= 0.0001f)
            {
                away = new Vec3(1f, 0f, 0f);
            }

            enemy.Agent.MoveTo(enemy.Agent.Position + (away.Normalized * config.BroadcastRadius));
        }
    }
}
