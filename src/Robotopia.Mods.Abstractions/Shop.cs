using System;
using System.Collections.Generic;

namespace Robotopia.Mods
{
    /// <summary>
    /// One purchasable entry in a shop catalog. Pure data: what the item does when bought is the
    /// consumer's business (subscribe to the shop UI's purchase event and switch on <see cref="Id"/>).
    /// </summary>
    public sealed class ShopItem
    {
        public ShopItem(string id, string name, string description, int price, string category = "", int maxPurchases = 0)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A shop item needs a non-empty id.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A shop item needs a non-empty name.", nameof(name));
            }

            Id = id;
            Name = name;
            Description = description ?? string.Empty;
            Price = Math.Max(0, price);
            Category = category ?? string.Empty;
            MaxPurchases = Math.Max(0, maxPurchases);
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public int Price { get; }

        /// <summary>Short display chip (e.g. "WEAPON"); empty for none.</summary>
        public string Category { get; }

        /// <summary>How many times the item can be bought per run; 0 means unlimited.</summary>
        public int MaxPurchases { get; }
    }

    /// <summary>A spendable balance a shop UI can observe and debit.</summary>
    public interface IShopWallet
    {
        int Balance { get; }

        /// <summary>Debits <paramref name="amount"/> and returns true, or returns false without
        /// mutating when the balance is insufficient or the amount is negative.</summary>
        bool TrySpend(int amount);

        /// <summary>Raised with the new balance after any change.</summary>
        event Action<int>? BalanceChanged;
    }

    /// <summary>Trivial default wallet for consumers that don't need their own backing store.</summary>
    public sealed class ShopWallet : IShopWallet
    {
        private int balance;

        public ShopWallet(int balance = 0)
        {
            this.balance = Math.Max(0, balance);
        }

        public int Balance => balance;

        public event Action<int>? BalanceChanged;

        /// <summary>Credits <paramref name="amount"/>; negative amounts are ignored.</summary>
        public void Earn(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            balance += amount;
            BalanceChanged?.Invoke(balance);
        }

        public bool TrySpend(int amount)
        {
            if (amount < 0 || amount > balance)
            {
                return false;
            }

            balance -= amount;
            BalanceChanged?.Invoke(balance);
            return true;
        }

        /// <summary>Resets the balance (a new run); negative values clamp to zero.</summary>
        public void Reset(int balance = 0)
        {
            this.balance = Math.Max(0, balance);
            BalanceChanged?.Invoke(this.balance);
        }
    }

    public enum ShopPurchaseResult
    {
        Purchased,
        InsufficientFunds,

        /// <summary>The item's <see cref="ShopItem.MaxPurchases"/> cap has been reached.</summary>
        SoldOut,

        /// <summary>The host's purchase gate declined (e.g. "integrity already full").</summary>
        Rejected
    }

    /// <summary>
    /// The one purchase arbiter, so shop UIs and game logic agree on rule order: sold-out is checked
    /// first, then the host gate, then funds — and the wallet is debited only on a successful purchase.
    /// </summary>
    public static class ShopTransactions
    {
        public static ShopPurchaseResult TryPurchase(ShopItem item, IShopWallet wallet, int timesPurchased, Func<ShopItem, bool>? canPurchase = null)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (wallet == null)
            {
                throw new ArgumentNullException(nameof(wallet));
            }

            if (item.MaxPurchases > 0 && timesPurchased >= item.MaxPurchases)
            {
                return ShopPurchaseResult.SoldOut;
            }

            if (canPurchase != null && !canPurchase(item))
            {
                return ShopPurchaseResult.Rejected;
            }

            if (!wallet.TrySpend(item.Price))
            {
                return ShopPurchaseResult.InsufficientFunds;
            }

            return ShopPurchaseResult.Purchased;
        }
    }
}
