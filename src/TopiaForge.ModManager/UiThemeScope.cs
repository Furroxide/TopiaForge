using System;
using System.Collections.Generic;
using System.Threading;
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
    internal sealed partial class UiThemeScope : IUiThemeService, IDisposable
    {
        private static readonly OperationResult<bool> Success = OperationResult<bool>.Success(true);
        private static readonly OperationResult<bool> Stopped =
            OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The UI scope is stopping.");
        private static readonly OperationResult<bool> Missing =
            OperationResult<bool>.Failure(ModErrorCode.NotFound, "This package does not declare the requested UI theme.");
        private readonly AdmittedUiCatalogue? catalogue;
        private readonly UiThemeScope? parent;
        private readonly IModLifetime lifetime;
        private readonly UiThemeAcquisitions? acquisitions;
        private readonly CancellationTokenRegistration stopping;
        private readonly List<UiThemeScope> children = new List<UiThemeScope>();
        private HashSet<UiThemeDemand>? surfaceDemands;
        private UiThemeDemand? selectionDemand;
        private bool disposed;

        internal UiThemeScope(IModLifetime lifetime, AdmittedUiCatalogue? catalogue, UiThemeScope? parent = null,
            UiThemeAcquisitions? acquisitions = null)
        {
            this.lifetime = lifetime;
            this.catalogue = catalogue ?? parent?.catalogue;
            this.parent = parent;
            this.acquisitions = acquisitions ?? parent?.acquisitions;
            if (parent != null)
            {
                parent.Changed += InheritedThemeChanged;
                parent.children.Add(this);
            }
            if (this.acquisitions != null)
                stopping = lifetime.StoppingToken.Register(() => this.acquisitions.OnMain(Dispose));
            if (!IsStopping && parent == null)
                selectionDemand = DemandSelection(null);
        }

        internal event Action? Changed;

        // The presentation routes stage all affected revisions before committing the new scope selection.
        internal PrepareUiThemeSelection? PrepareSelection { get; set; }
        internal Func<UiThemeScope, UiThemeRef?, OperationResult<bool>>? ValidateLocalSelection { get; set; }

        public UiThemeRef? SelectedTheme { get; private set; }
        internal bool IsStopping => disposed || lifetime.IsStopping;

        public OperationResult<bool> UseTheme(UiThemeRef? theme)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (IsStopping) return Stopped;
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

            var previous = selectionDemand;
            SelectedTheme = theme;
            selectionDemand = DemandSelection(theme);
            try
            {
                commit?.Invoke();
                Changed?.Invoke();
            }
            finally
            {
                previous?.Dispose();
            }
            return Success;
        }

        private OperationResult<bool> ValidateSelections(UiThemeScope changed, UiThemeRef? proposed)
        {
            var validation = ValidateLocalSelection?.Invoke(changed, proposed) ?? Success;
            if (!validation.Succeeded) return validation;
            foreach (var child in children)
            {
                if (child.IsStopping || child.SelectedTheme != null) continue;
                validation = child.ValidateSelections(changed, proposed);
                if (!validation.Succeeded) return validation;
            }
            return Success;
        }

        private UiThemeDemand? DemandSelection(UiThemeRef? theme)
        {
            var id = theme?.Id ?? (parent == null ? catalogue?.DefaultId : null);
            if (id == null || catalogue == null || !catalogue.TryGet(id, out var descriptor)) return null;
            return acquisitions?.Demand(descriptor, outcome =>
            {
                if (outcome.Succeeded && !IsStopping && ReferenceEquals(selectionDemand?.Descriptor, descriptor))
                    Changed?.Invoke();
            });
        }

        internal UiThemeDemand? DemandSurface(UiThemeDescriptor? descriptor, Action<OperationResult<bool>> settled,
            bool retryFailed = true)
        {
            if (IsStopping || descriptor == null) return null;
            var demand = acquisitions?.Demand(descriptor, settled, RemoveSurfaceDemand, retryFailed);
            if (demand != null) (surfaceDemands ??= new HashSet<UiThemeDemand>()).Add(demand);
            return demand;
        }

        private void RemoveSurfaceDemand(UiThemeDemand demand) => surfaceDemands?.Remove(demand);

        internal UiThemeRegistration? Registration(UiThemeDescriptor? descriptor) =>
            !IsStopping && descriptor != null ? acquisitions?.Registration(descriptor) : null;

        internal UiThemeDescriptor? ReadyDescriptor(UiThemeDescriptor? descriptor) =>
            !IsStopping ? Ready(descriptor, acquisitions) : null;

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

        internal static UiThemeDescriptor? Ready(UiThemeDescriptor? descriptor, UiThemeAcquisitions? acquisitions = null) =>
            descriptor != null && (!UiThemeAcquisitions.HasNativeResources(descriptor) || acquisitions?.Registration(descriptor) != null)
                ? descriptor : null;

        private void InheritedThemeChanged()
        {
            if (!IsStopping && SelectedTheme == null) Changed?.Invoke();
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
            stopping.Dispose();
            SelectedTheme = null;
            try { Changed?.Invoke(); }
            finally
            {
                Changed = null;
                PrepareSelection = null;
                ValidateLocalSelection = null;
                children.Clear();
                selectionDemand?.Dispose();
                selectionDemand = null;
                StopPreloads();
                var demands = surfaceDemands;
                surfaceDemands = null;
                if (demands != null)
                    foreach (var demand in demands) demand.Dispose();
            }
        }
    }
}
