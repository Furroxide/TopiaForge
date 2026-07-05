using Robotopia.Mods.UnityUi;

namespace Robotopia.Zombies
{
    /// <summary>
    /// The FIELD REQUISITIONS window between rounds: the kit's shop window bound to the
    /// controller's wallet and catalog. The controller owns the flow (Shopping state, the
    /// Chronos freeze, purchase gates and effects); this module only renders, forwards
    /// purchases, and keeps the window in lockstep with Controller.Shopping — so ESC and
    /// the X button resume the round through exactly one seam (CloseShopFromHud).
    /// </summary>
    internal sealed class ShopModal
    {
        private readonly HudContext context;
        private readonly QwShopWindow? shop;
        private int lastRunSerial;

        public ShopModal(HudContext context)
        {
            this.context = context;
            if (!context.Config.ShopEnabled)
            {
                return; // no window; Tick is a no-op and the controller never opens the shop either
            }

            // Persistent for the same reason as the sandbox spawn menu: the session root survives
            // scene swaps (DontDestroyOnLoad), so the window canvas must too.
            shop = context.Ui.ShopWindow(
                "zombies-shop",
                "FIELD REQUISITIONS",
                context.Controller.ShopCatalog,
                context.Controller.Wallet,
                new QwShopPaneOptions { CurrencyLabel = "CREDITS" },
                persistent: true);
            shop.Pane.CanPurchase = context.Controller.CanPurchaseShopItem;
            shop.Pane.Purchased += context.Controller.ApplyShopItem;
            shop.Closed += context.Controller.CloseShopFromHud;
            lastRunSerial = context.Controller.RunSerial;
        }

        public void Tick()
        {
            if (shop == null)
            {
                return;
            }

            var controller = context.Controller;
            if (controller.RunSerial != lastRunSerial)
            {
                lastRunSerial = controller.RunSerial;
                shop.Pane.ResetPurchases();
            }

            // Mirror the controller's state onto the window; the window's own open/closing guards
            // make the Closed → CloseShopFromHud → mirror loop re-entrancy safe in both directions.
            if (controller.Shopping && !shop.IsOpen)
            {
                shop.Show();
            }
            else if (!controller.Shopping && shop.IsOpen)
            {
                shop.Close();
            }

            shop.Tick();
        }
    }
}
