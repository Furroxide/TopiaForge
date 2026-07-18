using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    // The FIELD REQUISITIONS catalog: what the between-rounds shop sells, priced/tuned from config.
    // Items are pure data (SDK ShopItem); ZombiesController.ApplyShopItem switches on these ids to
    // apply the effects, and CanPurchaseShopItem gates the ones that can be redundant (full integrity,
    // full charges). Rebuild after a config change; the instance is otherwise immutable per session.
    internal static class ZombiesShopCatalog
    {
        public const string RepairId = "zombies.shop.repair";
        public const string PlatingId = "zombies.shop.plating";
        public const string ZapperGainId = "zombies.shop.zapper-gain";
        public const string RapidCoilsId = "zombies.shop.rapid-coils";
        public const string UplinkCellId = "zombies.shop.uplink-cell";
        public const string UplinkSurgeId = "zombies.shop.uplink-surge";
        public const string ComboStabilizerId = "zombies.shop.combo-stabilizer";

        public static IReadOnlyList<ShopItem> Build(ZombiesConfig config)
        {
            var items = new List<ShopItem>
            {
                new ShopItem(
                    RepairId,
                    "FIELD REPAIR",
                    "Patch your chassis: +" + (int)config.ShopRepairAmount + " integrity.",
                    config.ShopRepairPrice,
                    "HULL"),
                new ShopItem(
                    PlatingId,
                    "PLATING UPGRADE",
                    "Bolt on armor: +" + (int)config.ShopPlatingBonus + " max integrity, restored on install.",
                    config.ShopPlatingPrice,
                    "HULL",
                    maxPurchases: 2),
                new ShopItem(
                    ZapperGainId,
                    "ZAPPER GAIN",
                    "Overdrive the emitter: +" + (int)((config.ShopZapperGainMult - 1f) * 100f) + "% zapper damage per level.",
                    config.ShopZapperGainPrice,
                    "WEAPON",
                    maxPurchases: 3),
                new ShopItem(
                    RapidCoilsId,
                    "RAPID COILS",
                    "Faster recovery coils: zapper cooldown x" + config.ShopRapidCoilsMult.ToString("0.##") + " per level.",
                    config.ShopRapidCoilsPrice,
                    "WEAPON",
                    maxPurchases: 3),
                new ShopItem(
                    ComboStabilizerId,
                    "COMBO STABILIZER",
                    "Kill-chain capacitor: +" + config.ShopComboWindowBonusSeconds.ToString("0.##") + "s combo window per level.",
                    config.ShopComboStabilizerPrice,
                    "SYSTEMS",
                    maxPurchases: 2),
            };

            if (config.OverrideEnabled)
            {
                items.Insert(items.Count - 1, new ShopItem(
                    UplinkCellId,
                    "UPLINK CELL",
                    "An extra uplink battery: +1 max charge for JACK-IN and broadcasts.",
                    config.ShopUplinkCellPrice,
                    "UPLINK",
                    maxPurchases: 2));
                items.Insert(items.Count - 1, new ShopItem(
                    UplinkSurgeId,
                    "UPLINK SURGE",
                    "Instantly refill every uplink charge.",
                    config.ShopUplinkSurgePrice,
                    "UPLINK"));
            }

            return items.AsReadOnly();
        }
    }
}
