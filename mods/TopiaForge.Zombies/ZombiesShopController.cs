using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Between-wave requisitions built entirely from safe declarative UI and shop contracts.</summary>
    internal sealed class ZombiesShopController : IDisposable
    {
        private const float PauseRetrySeconds = 0.5f;

        private readonly IModContext context;
        private readonly ZombiesConfig config;
        private readonly ITimeControlService? time;
        private readonly IReadOnlyList<ShopItem> catalog;
        private readonly Dictionary<string, int> purchases = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Func<ShopItem, bool> canPurchase;
        private readonly Action<ShopItem> applyPurchase;
        private IUiSurface? surface;
        private ITimeLease? freeze;
        private IPlayerControlLease? control;
        private string selectedId;
        private float pauseRetryTimer;
        private bool pauseFailureReported;
        private bool disposed;

        public ZombiesShopController(
            IModContext context,
            ZombiesConfig config,
            ITimeControlService? time,
            Func<ShopItem, bool> canPurchase,
            Action<ShopItem> applyPurchase)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.time = time;
            this.canPurchase = canPurchase ?? throw new ArgumentNullException(nameof(canPurchase));
            this.applyPurchase = applyPurchase ?? throw new ArgumentNullException(nameof(applyPurchase));
            catalog = ZombiesShopCatalog.Build(config);
            selectedId = catalog.Count > 0 ? catalog[0].Id : string.Empty;
        }

        public ShopWallet Wallet { get; } = new ShopWallet();
        public ZombiesRunUpgrades Upgrades { get; } = new ZombiesRunUpgrades();
        public int Balance => Wallet.Balance;
        public bool IsOpen => surface != null;

        public OperationResult<string> Open()
        {
            if (disposed)
            {
                return OperationResult<string>.Failure(ModErrorCode.InvalidState, "The Zombies shop is unavailable.");
            }

            if (!config.ShopEnabled)
            {
                return OperationResult<string>.Failure(ModErrorCode.Unavailable, "Requisitions are disabled.");
            }

            if (surface != null)
            {
                surface.Show();
                return OperationResult<string>.Success("Requisitions are already open.");
            }

            var created = context.Ui.CreateSurface(new UiSurfaceRequest(
                "zombies-requisitions",
                "FIELD REQUISITIONS",
                BalanceText(),
                UiSurfaceKind.Window,
                660f,
                520f));
            if (!created.TryGetValue(out var window) || window == null)
            {
                return OperationResult<string>.Failure(
                    created.ErrorCode,
                    "Requisitions could not open: " + created.ErrorMessage);
            }

            surface = window;
            var content = RebuildContent();
            if (!content.Succeeded)
            {
                Close();
                return OperationResult<string>.Failure(content.ErrorCode, content.ErrorMessage);
            }

            AcquirePause();
            window.Show();
            context.Ui.ShowToast("Requisitions open. Wave countdown paused.", UiTone.Warning);
            return OperationResult<string>.Success("Requisitions opened.");
        }

        public void Tick(float controlDelta)
        {
            if (surface != null && !surface.IsVisible)
            {
                Close();
                return;
            }

            if (surface != null)
            {
                EnsurePause(controlDelta);
            }
        }

        public void AwardScore(int awardedScore)
        {
            if (!config.ShopEnabled)
            {
                return;
            }

            Wallet.Earn(ZombiesRuntimeMath.ScoreCredits(awardedScore, config.ShopCreditsPerScore));
        }

        public void Reset()
        {
            Close();
            purchases.Clear();
            Wallet.Reset();
            Upgrades.Reset();
            selectedId = catalog.Count > 0 ? catalog[0].Id : string.Empty;
        }

        public void Close()
        {
            var window = surface;
            surface = null;
            window?.Dispose();
            freeze?.Dispose();
            freeze = null;
            control?.Dispose();
            control = null;
            pauseRetryTimer = 0f;
            pauseFailureReported = false;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Close();
        }

        private void AcquirePause()
        {
            var chronosError = string.Empty;
            if (time?.IsAvailable == true)
            {
                var result = time.Freeze("zombies-requisitions", suspendPlayer: true);
                if (result.TryGetValue(out var lease))
                {
                    freeze = lease;
                    PauseAcquired();
                    return;
                }

                chronosError = result.ErrorMessage;
            }

            var fallback = context.LocalPlayer.AcquireControl("Zombies requisitions");
            if (fallback.TryGetValue(out var playerControl))
            {
                control = playerControl;
                PauseAcquired();
            }
            else
            {
                pauseRetryTimer = PauseRetrySeconds;
                if (!pauseFailureReported)
                {
                    pauseFailureReported = true;
                    context.Diagnostics.Report(new DiagnosticEntry(
                        "ZOMBIES_REQUISITIONS_PAUSE_FAILED",
                        "Requisitions could not pause gameplay; pause acquisition will retry in the background.",
                        DiagnosticSeverity.Warning,
                        string.IsNullOrWhiteSpace(chronosError)
                            ? fallback.ErrorMessage
                            : "Chronos: " + chronosError + " Player fallback: " + fallback.ErrorMessage));
                }
            }
        }

        private void EnsurePause(float controlDelta)
        {
            if (freeze?.IsActive == true || control?.IsActive == true)
            {
                PauseAcquired();
                return;
            }

            pauseRetryTimer = Math.Max(0f, pauseRetryTimer - Math.Max(0f, controlDelta));
            if (pauseRetryTimer > 0f)
            {
                return;
            }

            freeze?.Dispose();
            freeze = null;
            control?.Dispose();
            control = null;
            AcquirePause();
        }

        private void PauseAcquired()
        {
            pauseRetryTimer = 0f;
            pauseFailureReported = false;
        }

        private OperationResult<bool> RebuildContent()
        {
            var window = surface;
            if (window == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Requisitions are closed.");
            }

            var rows = new UiListItem[catalog.Count];
            ShopItem? selected = null;
            for (var index = 0; index < catalog.Count; index++)
            {
                var item = catalog[index];
                var bought = PurchaseCount(item.Id);
                var soldOut = item.MaxPurchases > 0 && bought >= item.MaxPurchases;
                var badge = soldOut
                    ? "SOLD OUT"
                    : item.Price.ToString(CultureInfo.InvariantCulture) + " CR";
                rows[index] = new UiListItem(item.Id, item.Name, item.Description, badge);
                if (string.Equals(item.Id, selectedId, StringComparison.Ordinal))
                {
                    selected = item;
                }
            }

            if (selected == null && catalog.Count > 0)
            {
                selected = catalog[0];
                selectedId = selected.Id;
            }

            var selectedText = selected == null
                ? new UiText("No requisitions are available.", UiTextStyle.Body, UiTone.Warning)
                : new UiText(
                    selected.Name + "\n" + selected.Description,
                    UiTextStyle.Body,
                    canPurchase(selected) ? UiTone.Neutral : UiTone.Warning);
            var buyEnabled = selected != null
                && !IsSoldOut(selected)
                && canPurchase(selected)
                && Balance >= selected.Price;
            var selectedItem = selected;
            var tree = new UiColumn(
                new UiText("Spend credits earned from combat. Upgrades last for this run only.", UiTextStyle.Caption),
                new UiVirtualList(
                    "requisitions-list",
                    rows,
                    Select,
                    selectedId,
                    visibleRows: 7),
                selectedText,
                new UiRow(
                    new UiButton(
                        "requisitions-buy",
                        selected == null ? "BUY" : "BUY // " + selected.Price.ToString(CultureInfo.InvariantCulture) + " CR",
                        () => Purchase(selectedItem),
                        UiButtonStyle.Primary,
                        buyEnabled),
                    new UiButton(
                        "requisitions-close",
                        "CLOSE",
                        Close,
                        UiButtonStyle.Ghost)));
            window.SetBody(BalanceText());
            return window.SetContent(tree);
        }

        private void Select(string itemId)
        {
            selectedId = itemId;
            var result = RebuildContent();
            if (!result.Succeeded)
            {
                context.Logger.Warn("Zombies requisitions could not update selection: " + result.ErrorMessage);
            }
        }

        private void Purchase(ShopItem? item)
        {
            if (item == null || surface == null)
            {
                return;
            }

            var result = ShopTransactions.TryPurchase(
                item,
                Wallet,
                PurchaseCount(item.Id),
                canPurchase);
            switch (result)
            {
                case ShopPurchaseResult.Purchased:
                    purchases[item.Id] = PurchaseCount(item.Id) + 1;
                    applyPurchase(item);
                    context.Ui.ShowToast(item.Name + " installed.", UiTone.Success);
                    break;
                case ShopPurchaseResult.InsufficientFunds:
                    context.Ui.ShowToast("Not enough credits.", UiTone.Warning);
                    break;
                case ShopPurchaseResult.SoldOut:
                    context.Ui.ShowToast("That upgrade is sold out for this run.", UiTone.Warning);
                    break;
                default:
                    context.Ui.ShowToast("That requisition cannot be used right now.", UiTone.Warning);
                    break;
            }

            var rebuilt = RebuildContent();
            if (!rebuilt.Succeeded)
            {
                context.Logger.Warn("Zombies requisitions could not refresh: " + rebuilt.ErrorMessage);
                Close();
            }
        }

        private int PurchaseCount(string itemId) =>
            purchases.TryGetValue(itemId, out var count) ? count : 0;

        private bool IsSoldOut(ShopItem item) =>
            item.MaxPurchases > 0 && PurchaseCount(item.Id) >= item.MaxPurchases;

        private string BalanceText() =>
            "AVAILABLE CREDITS  " + Balance.ToString(CultureInfo.InvariantCulture);
    }
}
