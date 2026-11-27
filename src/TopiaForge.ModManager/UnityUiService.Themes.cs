using System;
using TMPro;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService : IUiThemeScopeHost, IUiPresentationThemeSource, IRemoteThemedUiReceiver
    {
        private static readonly OperationResult<bool> ThemeResolved = OperationResult<bool>.Success(true);
        private static readonly OperationResult<bool> ThemeMissing =
            OperationResult<bool>.Failure(ModErrorCode.NotFound, "This package does not declare the requested UI theme.");
        private UiThemeScope? themeScope;
        private string? hudNoticePackageName;

        public IUiThemeService Themes => (IUiThemeService?)themeScope ?? UnavailableUiThemes.Instance;

        UiThemeScope? IUiPresentationThemeSource.PresentationThemeScope => themeScope;

        void IUiThemeScopeHost.ConfigureThemeScope(AdmittedUiCatalogue? catalogue, IUiService? parent, string packageDisplayName)
        {
            if (themeScope != null) throw new InvalidOperationException("The UI scope is already configured.");
            var acquisitions = catalogue == null ? null : themePackages.ForPackage(lifetime, catalogue);
            var scope = new UiThemeScope(lifetime, catalogue, parent?.Themes as UiThemeScope, acquisitions);
            lifetime.Track(scope);
            scope.Changed += RefreshScopeThemes;
            scope.ValidateLocalSelection = ValidateScopeThemes;
            themeScope = scope;
            hudNoticePackageName = packageDisplayName;
        }

        public OperationResult<bool> DescribePresentationTheme(UiSurfaceRequest request, out UiThemeDescriptor? theme)
        {
            if (themeScope != null)
                return themeScope.TryDescribe(request.Theme, out theme) ? ThemeResolved : ThemeMissing;
            theme = null;
            return request.Theme == null ? ThemeResolved : ThemeMissing;
        }

        private void RefreshScopeThemes()
        {
            var scope = themeScope;
            if (scope == null) return;
            for (var index = 0; index < surfaces.Count; index++)
                surfaces[index].RefreshTheme(scope);
        }

        private OperationResult<bool> ValidateScopeThemes(UiThemeScope changed, UiThemeRef? proposed)
        {
            var scope = themeScope!;
            foreach (var surface in surfaces)
            {
                var result = surface.ValidateThemeSelection(scope, changed, proposed);
                if (!result.Succeeded) return result;
            }
            return ThemeResolved;
        }

        private static UiThemeDescriptor? ResolveRemoteTheme(UiRemoteThemeSelection selection)
            => UiPresentationTheme.ViewerTheme(selection);

        private sealed partial class UnityUiSurface : IRemoteThemedSurface
        {
            private static readonly UiNode EmptyContent = new UiColumn();
            private UiThemeRef? themeOverride;
            private bool usesRemoteTheme;
            private UiThemeScope? localThemeScope;
            private UnityUiThemePackages? packages;
            private AdmittedUiCatalogue? remoteCatalogue;
            private UiThemeAcquisitions? remoteAcquisitions;
            private UiThemeDemand? themeDemand;
            private UiThemeRegistration? appliedRegistration;
            private Func<UiTextStyle, TMP_FontAsset?>? fontLookup;

            internal void InitializeTheme(UiThemeRef? theme, bool remote, UiThemeDescriptor? descriptor,
                UiThemeScope? local, UnityUiThemePackages packages, AdmittedUiCatalogue? catalogue)
            {
                themeOverride = theme;
                usesRemoteTheme = remote;
                remoteComposition = remote;
                localThemeScope = local;
                this.packages = packages;
                remoteCatalogue = catalogue;
                remoteAcquisitions = RemoteAssets(catalogue);
                declaredTheme = descriptor;
                if (descriptor != null && UiThemeAcquisitions.HasNativeResources(descriptor))
                {
                    if (remote) themeDemand = AcquireRemoteTheme(descriptor, remoteAcquisitions);
                    else if (theme != null)
                        themeDemand = local?.DemandSurface(descriptor, _ => ApplySurfaceTheme(declaredTheme));
                }
                ApplySurfaceTheme(descriptor);
            }

            internal void RefreshTheme(UiThemeScope scope)
            {
                if (!usesRemoteTheme && scope.TryDescribe(themeOverride, out var descriptor))
                {
                    declaredTheme = descriptor;
                    ApplySurfaceTheme(descriptor);
                }
            }

            internal OperationResult<bool> ValidateThemeSelection(UiThemeScope scope, UiThemeScope changed, UiThemeRef? proposed)
            {
                if (usesRemoteTheme || themeOverride != null || admitted == null) return ThemeResolved;
                if (!scope.TryDescribe(null, out var candidate, changed, proposed)) return ThemeMissing;
                return ReferenceEquals(candidate, declaredTheme) ? ThemeResolved : UiPresentationTheme.StylesValid(admitted, candidate);
            }

            OperationResult<bool> IRemoteThemedSurface.SetRemotePresentation(UiNode? content, UiRemoteThemeSelection theme)
            {
                var descriptor = ResolveRemoteTheme(theme);
                var catalogue = theme.Package?.Manifest.AdmittedUi;
                var assets = RemoteAssets(catalogue);
                var changingTheme = !usesRemoteTheme || !ReferenceEquals(descriptor, declaredTheme) ||
                    !ReferenceEquals(assets, remoteAcquisitions);
                if (!changingTheme)
                {
                    var committed = SetComposition(content ?? EmptyContent, descriptor, remote: true);
                    if (committed.Succeeded) ObserveRemoteGalleryCommit(theme.Envelope);
                    return committed;
                }

                var nextDemand = AcquireRemoteTheme(descriptor, assets);
                var adopted = false;
                try
                {
                    var registration = descriptor == null ? null : assets?.Registration(descriptor);
                    Func<UiTextStyle, TMP_FontAsset?>? fonts = registration == null ? null : registration.Font<TMP_FontAsset>;
                    var ready = UiThemeScope.Ready(descriptor, assets);
                    var result = SetComposition(content ?? EmptyContent, descriptor, remote: true,
                        prepareTheme: stage => PrepareStagedTheme(stage, ready, fonts),
                        prepareArtwork: tree => PrepareArtwork(tree, true, catalogue, assets));
                    if (!result.Succeeded) return result;

                    var previous = themeDemand;
                    usesRemoteTheme = true;
                    remoteCatalogue = catalogue;
                    remoteAcquisitions = assets;
                    themeDemand = nextDemand;
                    adopted = true;
                    try { ApplySurfaceTheme(descriptor); }
                    finally { previous?.Dispose(); }
                    ObserveRemoteGalleryCommit(theme.Envelope);
                    return result;
                }
                finally { if (!adopted) nextDemand?.Dispose(); }
            }

            private UiThemeAcquisitions? RemoteAssets(AdmittedUiCatalogue? catalogue) =>
                catalogue == null ? null : packages?.ForPackage(lifetime, catalogue);

            private UiThemeDemand? AcquireRemoteTheme(UiThemeDescriptor? descriptor, UiThemeAcquisitions? assets)
            {
                if (descriptor == null || assets == null || !UiThemeAcquisitions.HasNativeResources(descriptor)) return null;
                return assets.Demand(descriptor, _ =>
                {
                    if (ReferenceEquals(remoteAcquisitions, assets) && ReferenceEquals(declaredTheme, descriptor))
                        ApplySurfaceTheme(descriptor);
                });
            }

            private void ApplySurfaceTheme(UiThemeDescriptor? descriptor)
            {
                if (widget == null || CanvasRoot == null || !presence.IsLive) return;
                var effective = usesRemoteTheme ? UiThemeScope.Ready(descriptor, remoteAcquisitions)
                    : localThemeScope?.ReadyDescriptor(descriptor) ?? UiThemeScope.Ready(descriptor);
                var registration = effective == null ? null : usesRemoteTheme ? remoteAcquisitions?.Registration(effective)
                    : localThemeScope?.Registration(effective);
                if (!ReferenceEquals(registration, appliedRegistration))
                {
                    appliedRegistration = registration;
                    fontLookup = registration == null ? null : registration.Font<TMP_FontAsset>;
                }
                widget.Host.SetSurfaceTheme(CanvasRoot, effective, fontLookup);
                if (compositionRoot != null && compositionRoot.Go.GetComponent<TopiaForgeLayerSlot>() != null)
                    widget.Host.SetSurfaceTheme(compositionRoot.Go, effective ?? UiThemePackParser.Default, fontLookup);
            }

            private void ReleaseThemeResources()
            {
                var demand = themeDemand;
                themeDemand = null;
                localThemeScope = null;
                packages = null;
                remoteCatalogue = null;
                remoteAcquisitions = null;
                appliedRegistration = null;
                fontLookup = null;
                try { demand?.Dispose(); }
                catch (Exception error)
                {
                    try { logger.Error(error, "TopiaForgeUi could not retire a surface theme."); }
                    catch { }
                }
            }
        }
    }
}
