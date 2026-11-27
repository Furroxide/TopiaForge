using System;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>Owns queued and visible announcements, including their local theme leases.</summary>
    internal sealed class UnityUiStingers : IDisposable
    {
        private static readonly OperationResult<bool> Success = OperationResult<bool>.Success(true);
        internal static readonly OperationResult<bool> Stopped =
            OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The UI scope is stopping.");
        private static readonly OperationResult<bool> Full =
            OperationResult<bool>.Failure(ModErrorCode.RateLimited, "Three stingers are already waiting in this scope.");
        private static readonly OperationResult<bool> StyleInvalid = OperationResult<bool>.Failure(ModErrorCode.InvalidArgument,
            "A named stinger style must target stinger and preserve its requested role.");

        private readonly UiHost host;
        private readonly IModLogger logger;
        private readonly Action completeExit;
        private readonly UiStingerQueue<Presentation> queue = new UiStingerQueue<Presentation>();
        private TopiaForgeContainer? layer;
        private IDisposable? lifetimeLease;
        private bool exiting;

        internal UnityUiStingers(UiHost host, IModLogger logger)
        {
            this.host = host;
            this.logger = logger;
            completeExit = CompleteExit;
            host.LayerLost += LayerLost;
        }

        internal bool IsDisposed { get; private set; }

        internal void AttachLifetime(IDisposable lease) => lifetimeLease = lease;

        internal static OperationResult<bool> ValidateForDelivery(UiHost host, UiStingerRequest request,
            UiThemeDescriptor? declared, UiThemeScope? local, out UiStingerFontFit fontFit)
        {
            fontFit = UiStingerFontFit.KitOnly;
            if (!UiLayoutDecoration.AdmitsStingerStyle(request, declared ?? UiThemePackParser.Default)) return StyleInvalid;
            UiThemeDemand? demand = null;
            TopiaForgeContainer? probe = null;
            try
            {
                demand = LeaseTheme(declared, local, remote: null);
                probe = host.KitLayer("stinger measurement", TopiaForgeLayerPriority.Stinger, TopiaForgeScheme.Hud,
                    interactive: false, persistent: false);
                var view = Stage(probe, request, declared, demand, UiStingerFontFit.BothMeasured, measureDelivery: true);
                if (view.UsesDeclaredFaces) fontFit = UiStingerFontFit.BothMeasured;
                return Success;
            }
            catch (Exception error) { return Rejected(error); }
            finally
            {
                try { if (probe != null) host.DestroyLayer(probe.Go); }
                catch (Exception cleanup) { TopiaForgeLog.Error(cleanup, "The kit could not retire a stinger measurement view."); }
                try { demand?.Dispose(); }
                catch (Exception cleanup) { TopiaForgeLog.Error(cleanup, "The kit could not release a stinger measurement theme."); }
            }
        }

        internal OperationResult<bool> Show(UiStingerRequest request, UiThemeDescriptor? declared, UiThemeScope? local,
            UiThemeAcquisitions? remote, bool allowMissingStyle, out bool acceptedNew, Action<TopiaForgeStinger, double>? capture = null,
            UiStingerFontFit fontFit = UiStingerFontFit.BothMeasured)
        {
            acceptedNew = false;
            if (IsDisposed) return Stopped;
            if (!allowMissingStyle && !UiLayoutDecoration.AdmitsStingerStyle(request, declared ?? UiThemePackParser.Default))
                return StyleInvalid;
            var decision = queue.Classify(request);
            switch (decision)
            {
                case UiStingerQueueResult.Coalesced: return Success;
                case UiStingerQueueResult.RateLimited: return Full;
                case UiStingerQueueResult.Cancelled: return Stopped;
            }

            UiThemeDemand? demand = null;
            TopiaForgeStinger? view = null;
            try
            {
                demand = LeaseTheme(declared, local, remote);
                view = Stage(EnsureLayer(), request, declared, demand, fontFit);
                var presentation = new Presentation(view, demand, capture);
                if (decision == UiStingerQueueResult.Started) view.Show();
                queue.EnqueueRetained(request, presentation, Time.unscaledTimeAsDouble);
                acceptedNew = true;
                if (decision == UiStingerQueueResult.Started) ObserveShown(presentation);
                return Success;
            }
            catch (Exception error)
            {
                if (view != null) Release(new Presentation(view, demand));
                else ReleaseTheme(demand);
                if (!queue.Active.HasValue) ReleaseLayer();
                return Rejected(error);
            }
        }

        private static UiThemeDemand? LeaseTheme(UiThemeDescriptor? declared, UiThemeScope? local, UiThemeAcquisitions? remote) =>
            declared == null || !UiThemeAcquisitions.HasNativeResources(declared) ? null
                : local == null ? remote?.Demand(declared, retryFailed: false) :
                    local.DemandSurface(declared, static _ => { }, retryFailed: false);

        private static TopiaForgeStinger Stage(TopiaForgeContainer layer, UiStingerRequest request, UiThemeDescriptor? declared,
            UiThemeDemand? demand, UiStingerFontFit fontFit, bool measureDelivery = false)
        {
            var registration = demand?.Registration;
            var effective = declared != null && (!UiThemeAcquisitions.HasNativeResources(declared) || registration != null)
                ? declared : UiThemePackParser.Default;
            var faces = fontFit == UiStingerFontFit.BothMeasured ? ReadyFaces(request, declared, registration) : default;
            return TopiaForgeStinger.Stage(layer, request, declared, effective, faces.Banner, faces.Subtitle,
                checkCurrentViewport: !measureDelivery, measureDelivery);
        }

        private static (TMP_FontAsset? Banner, TMP_FontAsset? Subtitle) ReadyFaces(UiStingerRequest request,
            UiThemeDescriptor? declared, UiThemeRegistration? registration)
        {
            if (declared == null || registration == null) return default;
            var banner = UiStingerLayout.NeedsDeclaredFace(declared, UiTextStyle.Banner);
            var subtitle = request.Subtitle.Length != 0 && UiStingerLayout.NeedsDeclaredFace(declared, UiTextStyle.Title);
            var bannerFace = banner ? registration.Font<TMP_FontAsset>(UiTextStyle.Banner) : null;
            var subtitleFace = subtitle ? registration.Font<TMP_FontAsset>(UiTextStyle.Title) : null;
            return banner && bannerFace == null || subtitle && subtitleFace == null ? default : (bannerFace, subtitleFace);
        }

        private static OperationResult<bool> Rejected(Exception error) => error is ArgumentException
            ? OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, error.Message)
            : OperationResult<bool>.Failure(ModErrorCode.External, "The UI host could not create the stinger.");

        internal void Tick()
        {
            if (queue.Active is not { } active) return;
            try
            {
                active.Presentation.View.RefreshViewport();
                if (exiting || !queue.IsExpired(Time.unscaledTimeAsDouble)) return;
                exiting = true;
                active.Presentation.View.Exit(completeExit);
            }
            catch (Exception error) { StopAfterFailure(error); }
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            host.LayerLost -= LayerLost;
            while (queue.TryDrain(out var entry)) Release(entry.Presentation);
            try { ReleaseLayer(); }
            finally
            {
                var lease = lifetimeLease;
                lifetimeLease = null;
                lease?.Dispose();
            }
        }

        private TopiaForgeContainer EnsureLayer()
        {
            if (layer != null) return layer;
            var created = host.KitLayer("stinger", TopiaForgeLayerPriority.Stinger, TopiaForgeScheme.Hud,
                interactive: false, persistent: true);
            try
            {
                created.Go.AddComponent<UnityUiStingerFrame>().Initialize(this);
                return layer = created;
            }
            catch
            {
                host.DestroyLayer(created.Go);
                throw;
            }
        }

        private void CompleteExit()
        {
            exiting = false;
            if (!queue.Advance(Time.unscaledTimeAsDouble, out var expired)) return;
            Release(expired!.Value.Presentation);
            try
            {
                if (queue.Active is { } next)
                {
                    next.Presentation.View.Show();
                    ObserveShown(next.Presentation);
                }
                else ReleaseLayer();
            }
            catch (Exception error) { StopAfterFailure(error); }
        }

        private void ObserveShown(Presentation presentation)
        {
            if (presentation.Capture == null) return;
            try { presentation.Capture(presentation.View, queue.ShownAt); }
            catch (Exception error)
            {
                try { logger.Warn("Gallery stinger capture could not attach (" + error.GetType().Name + ")."); }
                catch { }
            }
        }

        private void LayerLost(GameObject lost)
        {
            if (!ReferenceEquals(layer?.Go, lost)) return;
            layer = null;
            Dispose();
        }

        private void ReleaseLayer()
        {
            var previous = layer;
            layer = null;
            if (previous != null) host.DestroyLayer(previous.Go);
        }

        private void Release(Presentation presentation)
        {
            try
            {
                if (presentation.View.Go != null) presentation.View.SetShown(false);
                presentation.View.Destroy();
            }
            catch (Exception error) { LogCleanup(error); }
            ReleaseTheme(presentation.Theme);
        }

        private void ReleaseTheme(UiThemeDemand? theme)
        {
            try { theme?.Dispose(); }
            catch (Exception error) { LogCleanup(error); }
        }

        private void StopAfterFailure(Exception error)
        {
            LogCleanup(error);
            try { Dispose(); }
            catch (Exception cleanup) { LogCleanup(cleanup); }
        }

        private void LogCleanup(Exception error)
        {
            try { logger.Error(error, "The UI host could not retire a stinger."); }
            catch { }
        }

        private readonly struct Presentation
        {
            internal Presentation(TopiaForgeStinger view, UiThemeDemand? theme, Action<TopiaForgeStinger, double>? capture = null)
            { View = view; Theme = theme; Capture = capture; }
            internal TopiaForgeStinger View { get; }
            internal UiThemeDemand? Theme { get; }
            internal Action<TopiaForgeStinger, double>? Capture { get; }
        }
    }

    internal sealed class UnityUiStingerFrame : MonoBehaviour
    {
        private UnityUiStingers? owner;
        internal void Initialize(UnityUiStingers scope) => owner = scope;
        private void Update() => owner?.Tick();
        private void OnDestroy() => owner = null;
    }
}
