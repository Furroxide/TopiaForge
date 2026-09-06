using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private RuntimeBindingRegistry? sessionBindings;
        private RuntimeDeclarationBinder? declarationBinder;
        private VerifiedPackageAssemblyLoader? verifiedDeclarationLoader;
        private bool loadingStarted;

        internal RuntimeBindingRegistry? SessionBindings => sessionBindings;

        /// <summary>Enables declaration binding for an exact profile before the first package load.</summary>
        internal void ConfigureSessionSelection(EffectiveProfile profile)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (loadingStarted || shutdownCompletion != null || sessionBindings != null)
                throw new InvalidOperationException("The exact session selection must be configured once before package loading.");
            sessionBindings = new RuntimeBindingRegistry(profile);
        }

        private void LoadWithBindings(ModPackage package, IReadOnlyCollection<ModManifest> availableManifests)
        {
            if (sessionBindings == null) { Load(package, availableManifests); return; }
            var manifest = package.Manifest!;
            var attempt = sessionBindings.BeginPackageLoad(new PackageIdentity(manifest.Id, manifest.Version));
            try { Load(package, availableManifests, attempt); }
            finally
            {
                // Covers every preflight/compatibility/dependency early return and partial-load failure.
                // A committed success has already consumed the token, so this cannot overwrite it.
                sessionBindings.CommitFailed(attempt, GetLoadFailure(manifest.Id) ?? "The package did not complete runtime loading.");
            }
        }
    }
}
