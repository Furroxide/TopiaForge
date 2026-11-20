using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal interface IUiThemeScopeHost
    {
        void ConfigureThemeScope(AdmittedUiCatalogue? catalogue, IUiService? parent, string packageDisplayName);
    }

    internal delegate OperationResult<bool> PrepareUiThemeSelection(
        UiThemeScope changed, UiThemeRef? proposed, out Action? commit);

    /// <summary>Package-local selection shared by local surfaces and authoritative wire publication.</summary>
    internal sealed class UiThemeScope : IUiThemeService, IDisposable
    {
        private static readonly OperationResult<bool> Success = OperationResult<bool>.Success(true);
        private static readonly OperationResult<bool> Stopped =
            OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The UI scope is stopping.");
        private static readonly OperationResult<bool> Missing =
            OperationResult<bool>.Failure(ModErrorCode.NotFound, "This package does not declare the requested UI theme.");
        private static readonly OperationResult<bool> AssetsUnavailable =
            OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The theme's bundled UI resources could not be registered.");
        private static readonly Task<OperationResult<bool>> Preloaded = Task.FromResult(Success);
        private static readonly Task<OperationResult<bool>> Cancelled = Task.FromResult(Stopped);
        private static readonly Task<OperationResult<bool>> NotFound = Task.FromResult(Missing);
        private static readonly Task<OperationResult<bool>> Unavailable = Task.FromResult(AssetsUnavailable);

        private readonly AdmittedUiCatalogue? catalogue;
        private readonly UiThemeScope? parent;
        private readonly IModLifetime lifetime;
        private readonly List<UiThemeScope> children = new List<UiThemeScope>();
        private bool disposed;

        internal UiThemeScope(IModLifetime lifetime, AdmittedUiCatalogue? catalogue, UiThemeScope? parent = null)
        {
            this.lifetime = lifetime;
            this.catalogue = catalogue ?? parent?.catalogue;
            this.parent = parent;
            if (parent != null)
            {
                parent.Changed += InheritedThemeChanged;
                parent.children.Add(this);
            }
        }

        internal event Action? Changed;

        // The presentation routes stage all affected revisions before committing the new scope selection.
        internal PrepareUiThemeSelection? PrepareSelection { get; set; }
        internal Func<UiThemeScope, UiThemeRef?, OperationResult<bool>>? ValidateLocalSelection { get; set; }

        public UiThemeRef? SelectedTheme { get; private set; }

        public OperationResult<bool> UseTheme(UiThemeRef? theme)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed || lifetime.IsStopping) return Stopped;
            if (theme != null && (catalogue == null || !catalogue.TryGet(theme.Id, out _))) return Missing;
            if (Equals(SelectedTheme, theme)) return Success;
            var local = ValidateSelections(this, theme);
            if (!local.Succeeded) return local;
            Action? commit = null;
            if (PrepareSelection != null)
            {
                var prepared = PrepareSelection(this, theme, out commit);
                if (!prepared.Succeeded) return prepared;
            }

            SelectedTheme = theme;
            commit?.Invoke();
            Changed?.Invoke();
            return Success;
        }

        private OperationResult<bool> ValidateSelections(UiThemeScope changed, UiThemeRef? proposed)
        {
            var validation = ValidateLocalSelection?.Invoke(changed, proposed) ?? Success;
            if (!validation.Succeeded) return validation;
            foreach (var child in children)
            {
                if (child.disposed || child.lifetime.IsStopping || child.SelectedTheme != null) continue;
                validation = child.ValidateSelections(changed, proposed);
                if (!validation.Succeeded) return validation;
            }
            return Success;
        }

        public Task<OperationResult<bool>> PreloadAsync(UiThemeRef theme, CancellationToken cancellationToken = default)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            if (disposed || lifetime.IsStopping || cancellationToken.IsCancellationRequested) return Cancelled;
            if (catalogue == null || !catalogue.TryGet(theme.Id, out var descriptor)) return NotFound;
            return HasNativeResources(descriptor) ? Unavailable : Preloaded;
        }

        internal bool TryDescribe(
            UiThemeRef? surfaceTheme, out UiThemeDescriptor? descriptor,
            UiThemeScope? changed = null, UiThemeRef? proposedSelection = null)
        {
            var theme = surfaceTheme;
            for (UiThemeScope? scope = this; theme == null && scope != null; scope = scope.parent)
                theme = ReferenceEquals(scope, changed) ? proposedSelection : scope.SelectedTheme;

            var id = theme?.Id ?? catalogue?.DefaultId;
            if (id == null)
            {
                descriptor = null;
                return true;
            }

            if (catalogue != null && catalogue.TryGet(id, out var declared))
            {
                descriptor = declared;
                return true;
            }

            descriptor = null;
            return false;
        }

        internal bool TryDescribe(string id, out UiThemeDescriptor? descriptor)
        {
            if (catalogue != null && catalogue.TryGet(id, out var declared))
            {
                descriptor = declared;
                return true;
            }

            descriptor = null;
            return false;
        }

        internal static UiThemeDescriptor? Ready(UiThemeDescriptor? descriptor) =>
            descriptor != null && HasNativeResources(descriptor) ? null : descriptor;

        private static bool HasNativeResources(UiThemeDescriptor descriptor) =>
            descriptor.Images.Count != 0 || descriptor.Fonts.Count != 0;

        private void InheritedThemeChanged()
        {
            if (!disposed && !lifetime.IsStopping && SelectedTheme == null) Changed?.Invoke();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (parent != null)
            {
                parent.Changed -= InheritedThemeChanged;
                parent.children.Remove(this);
            }
            Changed = null;
            PrepareSelection = null;
            ValidateLocalSelection = null;
            children.Clear();
            SelectedTheme = null;
        }
    }
}
