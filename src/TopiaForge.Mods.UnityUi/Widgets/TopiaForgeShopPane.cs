using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Tuning for <see cref="TopiaForgeShopPane"/>; defaults suit a Paper window.</summary>
    public sealed class TopiaForgeShopPaneOptions
    {
        /// <summary>Display name of the currency ("CREDITS", "SCRAP", …).</summary>
        public string CurrencyLabel { get; set; } = "CREDITS";

        /// <summary>Gets or sets cell width.</summary>
        public float CellWidth { get; set; } = 118f;
        /// <summary>Gets or sets cell height.</summary>
        public float CellHeight { get; set; } = 148f;

        /// <summary>Show a search field that filters the catalog by name/id.</summary>
        public bool ShowSearch { get; set; }

        /// <summary>Toast purchase results ("Bought …" / "Not enough …"). On by default.</summary>
        public bool ToastOnPurchase { get; set; } = true;
    }

    /// <summary>
    /// A ready-made shop: balance readout over a scrollable card grid of <see cref="ShopItem"/>s with
    /// price badges, affordability dimming, per-run purchase caps, and toast feedback. The pane owns
    /// presentation and the purchase *transaction* (via <see cref="ShopTransactions"/>); what a purchase
    /// *does* is the host's business — subscribe to <see cref="Purchased"/> and switch on the item id.
    /// Purchase counts are per-pane state; call <see cref="ResetPurchases"/> when a new run starts.
    /// </summary>
    public sealed class TopiaForgeShopPane
    {
        private readonly IShopWallet wallet;
        private readonly TopiaForgeShopPaneOptions options;
        private readonly TopiaForgeContainer root;
        private readonly TopiaForgeLabel balance;
        private readonly TopiaForgeLabel empty;
        private readonly TopiaForgeContainer grid;
        private readonly string balancePrefix;
        private readonly List<TopiaForgeCard> cards = new List<TopiaForgeCard>();
        private readonly List<ShopItem> visible = new List<ShopItem>();
        private readonly Dictionary<string, int> purchaseCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> priceTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        private IReadOnlyList<ShopItem> catalog;
        private string filter = string.Empty;
        private bool walletHooked;

        /// <summary>Creates a shop pane.</summary>
        public TopiaForgeShopPane(TopiaForgeContainer parent, IReadOnlyList<ShopItem> catalog, IShopWallet wallet, TopiaForgeShopPaneOptions? options = null)
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            this.wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.options = options ?? new TopiaForgeShopPaneOptions();
            balancePrefix = this.options.CurrencyLabel + "  ";

            root = parent.Column(TopiaForgeGap.Sm).Flex(1f, 1f);
            var header = root.Row(TopiaForgeGap.Sm);
            balance = header.Label(TopiaForgeTextStyle.Numeral).Tone(TopiaForgeTone.Accent);
            header.Spacer();
            if (this.options.ShowSearch)
            {
                root.SearchInput("Search…", value =>
                {
                    filter = value ?? string.Empty;
                    Rebind();
                });
            }

            empty = root.Label("Nothing for sale right now.", TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Muted);
            grid = root.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.Xs).Flex(1f, 1f).Content
                .Grid(this.options.CellWidth, this.options.CellHeight, TopiaForgeGap.Sm);

            wallet.BalanceChanged += OnBalanceChanged;
            walletHooked = true;
            Rebind();
        }

        /// <summary>Raised after a successful purchase (wallet already debited, count already bumped).</summary>
        public event Action<ShopItem>? Purchased;

        /// <summary>Raised when a click could not complete (sold out / rejected / insufficient funds).</summary>
        public event Action<ShopItem, ShopPurchaseResult>? PurchaseFailed;

        /// <summary>Optional host gate consulted before funds (e.g. "integrity already full").</summary>
        public Func<ShopItem, bool>? CanPurchase { get; set; }

        /// <summary>Gets the purchase count.</summary>
        public int GetPurchaseCount(string itemId)
        {
            return purchaseCounts.TryGetValue(itemId, out var count) ? count : 0;
        }

        /// <summary>Clears per-run purchase counts (a new run started).</summary>
        public void ResetPurchases()
        {
            purchaseCounts.Clear();
            Rebind();
        }

        /// <summary>Swaps the catalog and rebinds the pooled cards.</summary>
        public void SetCatalog(IReadOnlyList<ShopItem> catalog)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            Rebind();
        }

        /// <summary>
        /// Dirty-checked refresh of balance/affordability/gates; free at steady state, so call it
        /// every frame while the pane is visible (host gates can flip without a wallet event).
        /// </summary>
        public void Tick()
        {
            if (Dead())
            {
                return;
            }

            RefreshStates();
        }

        // Structural rebind: card membership, titles, tooltips. Allocates; call only when the
        // catalog, filter, or purchase counts change — never per frame.
        private void Rebind()
        {
            if (Dead())
            {
                return;
            }

            visible.Clear();
            foreach (var item in catalog)
            {
                if (filter.Length == 0
                    || item.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                    || item.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    visible.Add(item);
                }
            }

            for (var index = 0; index < visible.Count; index++)
            {
                var item = visible[index];
                TopiaForgeCard card;
                if (index < cards.Count)
                {
                    card = cards[index];
                }
                else
                {
                    // The pool slot index always equals the visible index while bound (SpawnMenu pattern).
                    var poolIndex = cards.Count;
                    card = grid.Card(string.Empty, () => HandleClick(poolIndex));
                    cards.Add(card);
                }

                card.Go.SetActive(true);
                card.SetTitle(item.Name);
                if (!priceTexts.ContainsKey(item.Id))
                {
                    priceTexts[item.Id] = item.Price.ToString();
                }

                card.Tooltip(TooltipText(item));
            }

            for (var index = visible.Count; index < cards.Count; index++)
            {
                cards[index].Go.SetActive(false);
            }

            empty.SetVisible(visible.Count == 0);
            RefreshStates();
        }

        // Per-frame-safe state pass: balance text, price/MAX badges, enabled dimming. Everything
        // it touches is dirty-checked by the widgets and the badge strings are cached, so a Tick
        // with no changes allocates nothing.
        private void RefreshStates()
        {
            balance.SetText(balancePrefix, wallet.Balance);
            for (var index = 0; index < visible.Count; index++)
            {
                var item = visible[index];
                var card = cards[index];
                var capped = item.MaxPurchases > 0 && GetPurchaseCount(item.Id) >= item.MaxPurchases;
                var gated = !capped && CanPurchase != null && !CanPurchase(item);
                if (capped)
                {
                    card.SetBadge("MAX", TopiaForgeTone.Neutral);
                }
                else
                {
                    card.SetBadge(priceTexts[item.Id], TopiaForgeTone.Warning);
                }

                card.SetEnabled(!capped && !gated && wallet.Balance >= item.Price);
            }
        }

        private void HandleClick(int poolIndex)
        {
            if (poolIndex >= visible.Count)
            {
                return;
            }

            var item = visible[poolIndex];
            var result = ShopTransactions.TryPurchase(item, wallet, GetPurchaseCount(item.Id), CanPurchase);
            if (result == ShopPurchaseResult.Purchased)
            {
                purchaseCounts[item.Id] = GetPurchaseCount(item.Id) + 1;
                if (options.ToastOnPurchase)
                {
                    TopiaForgeToasts.Success("Bought " + item.Name + ".");
                }

                TopiaForgeCallbacks.Invoke(Purchased, item, "Shop Purchased");
                Rebind(); // counts changed → tooltips/badges may flip to MAX
                return;
            }

            if (options.ToastOnPurchase)
            {
                switch (result)
                {
                    case ShopPurchaseResult.InsufficientFunds:
                        TopiaForgeToasts.Error("Not enough " + options.CurrencyLabel.ToLowerInvariant() + ".");
                        break;
                    case ShopPurchaseResult.SoldOut:
                        TopiaForgeToasts.Show(item.Name + " is sold out.", TopiaForgeTone.Neutral);
                        break;
                    default:
                        TopiaForgeToasts.Show(item.Name + " is unavailable right now.", TopiaForgeTone.Neutral);
                        break;
                }
            }

            TopiaForgeCallbacks.Invoke(PurchaseFailed, item, result, "Shop PurchaseFailed");
            RefreshStates();
        }

        private string TooltipText(ShopItem item)
        {
            var text = item.Name + "\n" + item.Description + "\n" + options.CurrencyLabel + " " + item.Price;
            if (item.Category.Length > 0)
            {
                text += "  ·  " + item.Category;
            }

            if (item.MaxPurchases > 0)
            {
                text += "\nOwned " + GetPurchaseCount(item.Id) + "/" + item.MaxPurchases;
            }

            return text;
        }

        private void OnBalanceChanged(int newBalance)
        {
            if (Dead())
            {
                return;
            }

            RefreshStates();
        }

        // The wallet usually outlives the UI (a controller owns it; the host owns the canvas). Once the
        // pane's root GameObject is gone the widgets are dead — drop the wallet subscription so later
        // earns/spends can't touch destroyed objects.
        private bool Dead()
        {
            if (root.Go == null)
            {
                if (walletHooked)
                {
                    walletHooked = false;
                    wallet.BalanceChanged -= OnBalanceChanged;
                }

                return true;
            }

            return false;
        }
    }
}
