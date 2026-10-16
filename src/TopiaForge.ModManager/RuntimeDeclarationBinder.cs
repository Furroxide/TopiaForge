using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Builds one complete metadata batch without invoking implementation constructors.</summary>
    internal sealed class RuntimeDeclarationBinder
    {
        private readonly VerifiedPackageAssemblyLoader loader;
        internal RuntimeDeclarationBinder(VerifiedPackageAssemblyLoader loader) { this.loader = loader; }

        internal PackageBindingBatch Bind(ModPackage package)
        {
            var manifest = package.Manifest ?? throw new ArgumentException("A manifest is required.");
            var identity = new PackageIdentity(manifest.Id, manifest.Version);
            var modes = new List<ISessionImplementation<IGamemodeFactory>>();
            var worlds = new List<ISessionImplementation<IWorldContentProvider>>();
            var discoveries = new List<ISessionImplementation<IWorldDiscoverySource>>();
            var failures = new List<RuntimeBindingFailure>();
            if (manifest.Contributions != null)
            {
                foreach (var mode in manifest.Contributions.Gamemodes)
                    BindOne(package, identity, "gamemode", mode.Id, mode.Implementation, modes, failures);
                foreach (var world in manifest.Contributions.Worlds)
                {
                    if (world.Content?.Kind == ModWorldContent.DiscoveredKind)
                    {
                        var source = BindOne(package, identity, "world", world.Id, world.Content.Implementation, discoveries, failures);
                        if (source != null) worlds.Add(source);
                    }
                    else if (world.Content?.Kind == ModWorldContent.ProviderKind)
                        BindOne(package, identity, "world", world.Id, world.Content.Implementation, worlds, failures);
                    else if (world.Content?.Kind == ModWorldContent.BundleKind || world.Content?.Kind == ModWorldContent.GameSceneKind)
                    {
                        try { worlds.Add(new BuiltinWorldImplementation(identity, world)); }
                        catch (Exception error) { failures.Add(new RuntimeBindingFailure(identity, "world", world.Id, RuntimeBindingFailureCode.InvalidType, error.Message)); }
                    }
                    else failures.Add(new RuntimeBindingFailure(identity, "world", world.Id, RuntimeBindingFailureCode.InvalidType, "Unsupported world content declaration."));
                }
            }
            return new PackageBindingBatch(identity, modes, worlds, discoveries, failures);
        }

        private ISessionImplementation<T>? BindOne<T>(ModPackage package, PackageIdentity identity, string kind, string id,
            ModImplementationBinding? binding, List<ISessionImplementation<T>> success, List<RuntimeBindingFailure> failures) where T : class
        {
            try
            {
                if (binding == null) throw new BindingVerificationException(RuntimeBindingFailureCode.MissingType, "The declaration has no implementation binding.");
                var type = loader.LoadType(package, binding);
                SessionImplementation<T> implementation;
                try { implementation = new SessionImplementation<T>(identity, id, type); }
                catch (ArgumentException error) { throw new BindingVerificationException(RuntimeBindingFailureCode.InvalidType, error.Message, error); }
                success.Add(implementation);
                return implementation;
            }
            catch (Exception error)
            {
                var code = error is BindingVerificationException verified ? verified.Code : RuntimeBindingFailureCode.VerificationFailed;
                failures.Add(new RuntimeBindingFailure(identity, kind, id, code, error.Message, binding?.Assembly ?? package.Manifest!.EntryAssembly, binding?.Type));
                return null;
            }
        }
    }
}
