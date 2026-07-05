using System;
using Robotopia.Mods;

namespace Robotopia.ModManager.Tests
{
    // Behavioural tests for the SDK shop primitives (Abstractions/Shop.cs): the default wallet's
    // earn/spend/reset semantics and events, and the ShopTransactions purchase arbiter's rule order
    // (sold-out → host gate → funds; debit only on success). Unity-free by construction.
    internal static class ShopTests
    {
        public static void Run()
        {
            TestWalletEarnSpendReset();
            TestWalletEvents();
            TestItemGuardsAndClamps();
            TestPurchaseRuleOrder();
            TestPurchaseDebitsOnlyOnSuccess();
            Console.WriteLine("All shop tests passed.");
        }

        private static void TestWalletEarnSpendReset()
        {
            Assert(new ShopWallet(-5).Balance == 0, "a negative starting balance clamps to zero");

            var wallet = new ShopWallet(100);
            wallet.Earn(50);
            Assert(wallet.Balance == 150, "Earn adds to the balance");
            wallet.Earn(0);
            wallet.Earn(-25);
            Assert(wallet.Balance == 150, "zero/negative earns are ignored");

            Assert(wallet.TrySpend(150) && wallet.Balance == 0, "spending the exact balance succeeds and empties it");
            Assert(!wallet.TrySpend(1) && wallet.Balance == 0, "overspending fails without mutating");
            wallet.Earn(10);
            Assert(!wallet.TrySpend(-1) && wallet.Balance == 10, "a negative spend fails without mutating");
            Assert(wallet.TrySpend(0) && wallet.Balance == 10, "a zero spend succeeds (free item) without mutating");

            wallet.Reset(75);
            Assert(wallet.Balance == 75, "Reset re-seeds the balance");
            wallet.Reset(-3);
            Assert(wallet.Balance == 0, "Reset clamps negative to zero");
        }

        private static void TestWalletEvents()
        {
            var wallet = new ShopWallet(20);
            var events = 0;
            var lastBalance = -1;
            wallet.BalanceChanged += balance =>
            {
                events++;
                lastBalance = balance;
            };

            wallet.Earn(5);
            Assert(events == 1 && lastBalance == 25, "Earn raises BalanceChanged with the new balance");
            Assert(wallet.TrySpend(10) && events == 2 && lastBalance == 15, "a successful spend raises BalanceChanged");
            Assert(!wallet.TrySpend(100) && events == 2, "a failed spend raises nothing");
            wallet.Earn(-1);
            Assert(events == 2, "an ignored earn raises nothing");
            wallet.Reset(0);
            Assert(events == 3 && lastBalance == 0, "Reset raises BalanceChanged");
        }

        private static void TestItemGuardsAndClamps()
        {
            foreach (var invalid in new[] { null, "", "  " })
            {
                try
                {
                    _ = new ShopItem(invalid!, "Name", "desc", 10);
                    Assert(false, "ShopItem must reject a blank id");
                }
                catch (ArgumentException)
                {
                }

                try
                {
                    _ = new ShopItem("id", invalid!, "desc", 10);
                    Assert(false, "ShopItem must reject a blank name");
                }
                catch (ArgumentException)
                {
                }
            }

            var item = new ShopItem("id", "Name", null!, -5, null!, -2);
            Assert(item.Description == string.Empty && item.Category == string.Empty, "null description/category become empty");
            Assert(item.Price == 0 && item.MaxPurchases == 0, "negative price/maxPurchases clamp to zero");

            var full = new ShopItem("id", "Name", "desc", 40, "HULL", 2);
            Assert(full.Price == 40 && full.Category == "HULL" && full.MaxPurchases == 2, "ShopItem keeps its fields");
        }

        private static void TestPurchaseRuleOrder()
        {
            // Sold-out wins over the gate and funds: a capped item reports SoldOut even when the gate
            // would also reject and the wallet is empty.
            var capped = new ShopItem("id", "Name", "desc", 100, maxPurchases: 1);
            var broke = new ShopWallet(0);
            Assert(ShopTransactions.TryPurchase(capped, broke, timesPurchased: 1, _ => false) == ShopPurchaseResult.SoldOut,
                "the purchase cap is checked before the gate and funds");

            // The host gate wins over funds: a vetoed item reports Rejected, not InsufficientFunds.
            Assert(ShopTransactions.TryPurchase(capped, broke, timesPurchased: 0, _ => false) == ShopPurchaseResult.Rejected,
                "the host gate is checked before funds");

            Assert(ShopTransactions.TryPurchase(capped, broke, timesPurchased: 0) == ShopPurchaseResult.InsufficientFunds,
                "an unaffordable item reports InsufficientFunds");

            var rich = new ShopWallet(100);
            Assert(ShopTransactions.TryPurchase(capped, rich, timesPurchased: 0) == ShopPurchaseResult.Purchased,
                "an affordable, uncapped, ungated item purchases");

            var unlimited = new ShopItem("id", "Name", "desc", 0);
            Assert(ShopTransactions.TryPurchase(unlimited, broke, timesPurchased: 999) == ShopPurchaseResult.Purchased,
                "maxPurchases 0 means unlimited (and a free item needs no funds)");
        }

        private static void TestPurchaseDebitsOnlyOnSuccess()
        {
            var item = new ShopItem("id", "Name", "desc", 60, maxPurchases: 1);
            var wallet = new ShopWallet(100);

            Assert(ShopTransactions.TryPurchase(item, wallet, timesPurchased: 1) == ShopPurchaseResult.SoldOut
                && wallet.Balance == 100, "SoldOut must not touch the wallet");
            Assert(ShopTransactions.TryPurchase(item, wallet, timesPurchased: 0, _ => false) == ShopPurchaseResult.Rejected
                && wallet.Balance == 100, "Rejected must not touch the wallet");
            Assert(ShopTransactions.TryPurchase(item, wallet, timesPurchased: 0) == ShopPurchaseResult.Purchased
                && wallet.Balance == 40, "Purchased debits exactly the price");
            Assert(ShopTransactions.TryPurchase(item, wallet, timesPurchased: 0) == ShopPurchaseResult.InsufficientFunds
                && wallet.Balance == 40, "InsufficientFunds must not touch the wallet");

            try
            {
                _ = ShopTransactions.TryPurchase(null!, wallet, 0);
                Assert(false, "TryPurchase must null-guard the item");
            }
            catch (ArgumentNullException)
            {
            }

            try
            {
                _ = ShopTransactions.TryPurchase(item, null!, 0);
                Assert(false, "TryPurchase must null-guard the wallet");
            }
            catch (ArgumentNullException)
            {
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
