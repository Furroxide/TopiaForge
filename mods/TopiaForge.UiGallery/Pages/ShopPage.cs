using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>
    /// The shop pane and window (SDK ShopItem/ShopWallet + TopiaForgeShopPane): affordability
    /// dimming, price/MAX badges, a host purchase gate, sold-out caps, and wallet events
    /// driving live re-tint. GRANT 500 exercises BalanceChanged; the gated item stays
    /// disabled to show the CanPurchase seam.
    /// </summary>
    internal static class ShopPage
    {
        private static TopiaForgeShopWindow? window;

        public static void Build(TopiaForgeContainer page)
        {
            page.SectionHeader("SHOP PANE");
            page.Label(
                "A catalog grid bound to a wallet. Cards dim when unaffordable, capped items flip to MAX, "
                + "purchases toast, and the balance label tracks the wallet live.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var wallet = new ShopWallet(1200);
            var catalog = new[]
            {
                new ShopItem("gallery.repair", "FIELD REPAIR", "Restore some integrity.", 400, "HULL"),
                new ShopItem("gallery.plating", "PLATING", "One-time armor bolt-on.", 900, "HULL", maxPurchases: 1),
                new ShopItem("gallery.gain", "ZAPPER GAIN", "More damage per level.", 700, "WEAPON", maxPurchases: 3),
                new ShopItem("gallery.coils", "RAPID COILS", "Shorter cooldown per level.", 700, "WEAPON", maxPurchases: 3),
                new ShopItem("gallery.surge", "UPLINK SURGE", "Gated: CanPurchase returns false here.", 500, "UPLINK"),
                new ShopItem("gallery.pricey", "PROTOTYPE CORE", "Priced above the starting wallet.", 5000, "SYSTEMS"),
            };

            var status = page.Label("Nothing bought yet.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            var pane = new TopiaForgeShopPane(page, catalog, wallet, new TopiaForgeShopPaneOptions { CurrencyLabel = "CREDITS" })
            {
                // The gate demo: the host can veto an item without it being sold out or unaffordable.
                CanPurchase = item => item.Id != "gallery.surge",
            };
            pane.Purchased += item => status.SetText("Bought: " + item.Name + " (" + wallet.Balance + " left)");
            pane.PurchaseFailed += (item, result) => status.SetText(item.Name + " failed: " + result);

            var controls = page.Row(TopiaForgeGap.Sm);
            controls.Button("GRANT 500", () => wallet.Earn(500), TopiaForgeButtonStyle.Outline);
            controls.Button("RESET RUN", () =>
            {
                wallet.Reset(1200);
                pane.ResetPurchases();
                status.SetText("Run reset: purchases cleared, wallet re-seeded.");
            }, TopiaForgeButtonStyle.Outline);

            page.SectionHeader("SHOP WINDOW");
            page.Label("The ten-line consumer path: the same pane hosted in a standard kit window.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            page.Button("OPEN SHOP WINDOW", () =>
            {
                window ??= page.Host.ShopWindow("gallery-shop", "GALLERY SHOP", catalog, wallet);
                window.Show();
            }, TopiaForgeButtonStyle.Outline);
        }

        public static void Reset()
        {
            window?.Window.Destroy();
            window = null;
        }
    }
}
