using System;
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
            var scope = new UiThemeScope(lifetime, catalogue, parent?.Themes as UiThemeScope);
            lifetime.Track(scope);
            scope.Changed += RefreshScopeThemes;
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

        private static UiThemeDescriptor? ResolveRemoteTheme(UiRemoteThemeSelection selection)
            => UiPresentationTheme.ViewerTheme(selection);

        private sealed partial class UnityUiSurface : IRemoteThemedSurface
        {
            private static readonly UiNode EmptyContent = new UiColumn();
            private UiThemeRef? themeOverride;
            private bool usesRemoteTheme;

            internal void InitializeTheme(UiThemeRef? theme, bool remote, UiThemeDescriptor? descriptor)
            {
                themeOverride = theme;
                usesRemoteTheme = remote;
                remoteComposition = remote;
                declaredTheme = descriptor;
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

            OperationResult<bool> IRemoteThemedSurface.SetRemotePresentation(UiNode? content, UiRemoteThemeSelection theme)
            {
                var descriptor = ResolveRemoteTheme(theme);
                var result = SetComposition(content ?? EmptyContent, descriptor, remote: true,
                    changingTheme: !ReferenceEquals(descriptor, declaredTheme));
                if (result.Succeeded)
                {
                    usesRemoteTheme = true;
                    ApplySurfaceTheme(descriptor);
                }

                return result;
            }

            private void ApplySurfaceTheme(UiThemeDescriptor? descriptor)
            {
                if (widget == null || CanvasRoot == null || !presence.IsLive) return;
                var effective = UiThemeScope.Ready(descriptor);
                widget.Host.SetSurfaceTheme(CanvasRoot, effective);
                if (compositionRoot != null && compositionRoot.Go.GetComponent<TopiaForgeLayerSlot>() != null)
                    widget.Host.SetSurfaceTheme(compositionRoot.Go, effective ?? UiThemePackParser.Default);
            }
        }
    }
}
