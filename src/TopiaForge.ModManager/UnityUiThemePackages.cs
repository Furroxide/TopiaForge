using System.Runtime.CompilerServices;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Shares acquisitions only among scopes using the same immutable admitted package catalogue.</summary>
    internal sealed class UnityUiThemePackages
    {
        private readonly IHostDispatcher dispatcher;
        private readonly ConditionalWeakTable<AdmittedUiCatalogue, UiThemeAcquisitions> packages =
            new ConditionalWeakTable<AdmittedUiCatalogue, UiThemeAcquisitions>();

        internal UnityUiThemePackages(IHostDispatcher dispatcher) { this.dispatcher = dispatcher; }

        internal UiThemeAcquisitions ForPackage(IModLifetime owner, AdmittedUiCatalogue catalogue)
        {
            if (packages.TryGetValue(catalogue, out var found)) return found;
            var created = new UiThemeAcquisitions(dispatcher, owner, new UnityUiThemeNativeLoader(dispatcher, catalogue));
            packages.Add(catalogue, created);
            return created;
        }
    }
}
