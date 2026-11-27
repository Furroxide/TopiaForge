using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService : IUiStingerOrigin, IRemoteUiStingerReceiver
    {
        private UnityUiStingers? stingers;
        private Dictionary<(string Owner, ulong Token), UnityUiStingers>? receivedStingers;

        public OperationResult<bool> ShowStinger(UiStingerRequest request) => ShowStinger(request, out _);

        public OperationResult<bool> ShowStinger(UiStingerRequest request, out bool acceptedNew)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            acceptedNew = false;
            if (lifetime.IsStopping) return UnityUiStingers.Stopped;

            UiThemeDescriptor? declared = null;
            themeScope?.TryDescribe((UiThemeRef?)null, out declared);
            if (stingers is not { IsDisposed: false }) stingers = CreateStingers();
            Action<TopiaForge.Mods.UnityUi.TopiaForgeStinger, double>? capture = null;
            PrepareGalleryStinger(request, declared, null, remote: false, ref capture);
            return stingers.Show(request, declared, themeScope, null, allowMissingStyle: false, out acceptedNew, capture);
        }

        public OperationResult<bool> ValidateStingerForDelivery(UiStingerRequest request, UiThemeDescriptor? declared, out UiStingerFontFit fontFit)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            fontFit = UiStingerFontFit.KitOnly;
            return lifetime.IsStopping ? UnityUiStingers.Stopped
                : UnityUiStingers.ValidateForDelivery(host, request, declared, themeScope, out fontFit);
        }

        public OperationResult<bool> ShowRemoteStinger(string ownerPackageId, ulong scopeToken, UiStingerRequest request, UiThemeDescriptor? declared,
            AdmittedUiCatalogue? catalogue, bool hasDeclaredTheme, UiStingerFontFit fontFit,
            UiWireEventEnvelope? authenticatedEnvelope = null)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (lifetime.IsStopping) return UnityUiStingers.Stopped;

            var scopes = receivedStingers ??= new Dictionary<(string Owner, ulong Token), UnityUiStingers>();
            var key = (ownerPackageId, scopeToken);
            if (!scopes.TryGetValue(key, out var scope) || scope.IsDisposed)
                scopes[key] = scope = CreateStingers();
            var assets = catalogue == null ? null : themePackages.ForPackage(lifetime, catalogue);
            Action<TopiaForge.Mods.UnityUi.TopiaForgeStinger, double>? capture = null;
            if (authenticatedEnvelope?.OwnerPackageId == ownerPackageId)
                PrepareGalleryStinger(request, declared, authenticatedEnvelope, remote: true, ref capture);
            return scope.Show(request, declared, null, assets, allowMissingStyle: hasDeclaredTheme && declared == null, out _, capture, fontFit);
        }

        partial void PrepareGalleryStinger(UiStingerRequest request, UiThemeDescriptor? declared,
            UiWireEventEnvelope? authenticatedEnvelope, bool remote, ref Action<TopiaForge.Mods.UnityUi.TopiaForgeStinger, double>? capture);

        public bool RetireRemoteStingers(string ownerPackageId, ulong scopeToken)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (receivedStingers == null || !receivedStingers.Remove((ownerPackageId, scopeToken), out var scope)) return false;
            DisposeRemoteStingerScope(scope);
            return true;
        }

        public void ResetRemoteStingers()
        {
            UnityMainThreadGuard.AssertCurrent();
            var scopes = receivedStingers;
            receivedStingers = null;
            if (scopes == null) return;
            foreach (var scope in scopes.Values) DisposeRemoteStingerScope(scope);
        }

        private void DisposeRemoteStingerScope(UnityUiStingers scope)
        {
            try { scope.Dispose(); }
            catch (Exception error)
            {
                try { logger.Error(error, "The UI host could not retire received stingers."); }
                catch { }
            }
        }

        private UnityUiStingers CreateStingers()
        {
            var created = new UnityUiStingers(host, logger);
            created.AttachLifetime(lifetime.Track(created));
            return created;
        }
    }
}
