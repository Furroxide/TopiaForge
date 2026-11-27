using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Run-upgrade application and dirty-checked HUD projection.</summary>
    internal sealed partial class ZombiesController
    {
        private double? interWaveEndsAt;
        private bool IsRequisitionsPhase => phase is ZombiesPhase.Starting or ZombiesPhase.InterWave;

        private bool CanPurchaseShopItem(ShopItem item)
        {
            switch (item.Id)
            {
                case ZombiesShopCatalog.RepairId:
                    return integrity < maximumIntegrity;
                case ZombiesShopCatalog.UplinkSurgeId:
                    return uplinkCharges < MaximumUplinkCharges;
                default:
                    return true;
            }
        }

        private void ApplyShopItem(ShopItem item)
        {
            switch (item.Id)
            {
                case ZombiesShopCatalog.RepairId:
                    integrity = Math.Min(maximumIntegrity, integrity + config.ShopRepairAmount);
                    SyncNativeHealthToIntegrity();
                    break;
                case ZombiesShopCatalog.PlatingId:
                    shop.Upgrades.BonusMaxIntegrity += config.ShopPlatingBonus;
                    maximumIntegrity = config.PlayerIntegrity + shop.Upgrades.BonusMaxIntegrity;
                    integrity = Math.Min(maximumIntegrity, integrity + config.ShopPlatingBonus);
                    SyncNativeHealthToIntegrity();
                    break;
                case ZombiesShopCatalog.ZapperGainId:
                    shop.Upgrades.ZapperDamageMult *= config.ShopZapperGainMult;
                    break;
                case ZombiesShopCatalog.RapidCoilsId:
                    shop.Upgrades.ZapperCooldownMult *= config.ShopRapidCoilsMult;
                    break;
                case ZombiesShopCatalog.UplinkCellId:
                    shop.Upgrades.BonusUplinkCharges++;
                    uplinkCharges = Math.Min(MaximumUplinkCharges, uplinkCharges + 1);
                    break;
                case ZombiesShopCatalog.UplinkSurgeId:
                    uplinkCharges = MaximumUplinkCharges;
                    uplinkRegenTimer = 0f;
                    break;
                case ZombiesShopCatalog.ComboStabilizerId:
                    shop.Upgrades.ComboWindowBonusSeconds += config.ShopComboWindowBonusSeconds;
                    break;
            }
        }

        private void RefreshHud(bool timerPaused = false)
        {
            var seconds = phase == ZombiesPhase.Starting || (phase == ZombiesPhase.InterWave && timerPaused)
                ? (int)Math.Ceiling(phaseTimer)
                : 0;
            if (phase != ZombiesPhase.InterWave || timerPaused)
            {
                interWaveEndsAt = null;
            }
            else
            {
                var now = context.Ui.ClockTime;
                // The gameplay timer remains authoritative if the two unscaled clocks diverge.
                var remaining = Math.Ceiling(phaseTimer);
                if (interWaveEndsAt is not double deadline || Math.Ceiling(Math.Max(0d, deadline - now)) != remaining)
                    interWaveEndsAt = now + phaseTimer;
            }
            var chargePercent = charging && config.ChargeShotSeconds > 0f
                ? (int?)Math.Round(Math.Min(1f, chargeSeconds / config.ChargeShotSeconds) * 100f)
                : null;
            var maximumCharges = MaximumUplinkCharges;
            var refillPercent = config.OverrideEnabled && uplinkCharges < maximumCharges
                ? Math.Clamp((int)(uplinkRegenTimer / config.OverrideChargeRegenSeconds * 100f), 0, 99)
                : 0;
            var snapshot = new ZombiesHudSnapshot(
                phase,
                wave,
                CountHostiles(),
                CountAllies(),
                pendingSpawns,
                (int)Math.Ceiling(integrity),
                (int)Math.Ceiling(maximumIntegrity),
                score,
                shop.Balance,
                comboMultiplier,
                uplinkCharges,
                maximumCharges,
                seconds,
                chargePercent,
                config.OverrideEnabled,
                config.ShopEnabled,
                interWaveEndsAt,
                refillPercent,
                shopReady: config.ShopEnabled && IsRequisitionsPhase && !timerPaused);
            hud.Update(snapshot, fireAction, overrideAction, broadcastAction, shopAction);
        }
    }
}
