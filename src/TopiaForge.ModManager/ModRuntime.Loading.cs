using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private void Load(ModPackage package, IReadOnlyCollection<ModManifest> availableManifests)
        {
            if (!package.IsValid)
            {
                var id = package.Manifest?.Id ?? Path.GetFileName(package.PackagePath);
                var reasons = package.Errors.Count > 0 ? string.Join("; ", package.Errors) : "manifest or state missing";
                failedMods[id] = reasons;
                logger.Warn("Skipping invalid package " + id + " (" + package.PackagePath + "): " + reasons);
                return;
            }

            var manifest = package.Manifest!;

            var compatibility = ManifestRuntimeCompatibility.Evaluate(manifest, validationContext);
            if (!compatibility.IsCompatible)
            {
                var reason = string.Join("; ", compatibility.Errors);
                failedMods[manifest.Id] = reason;
                logger.Warn("Skipping " + manifest.Id + ": runtime compatibility rejected the package: " + reason);
                return;
            }

            if (failedMods.ContainsKey(manifest.Id))
            {
                return;
            }

            // The resolver already validated dependencies at the manifest level, but a dependency can still
            // fail at load time (e.g. a TypeLoadException from a binary-stale package). Running a dependent
            // without its dependency's services produces a half-alive mod giving users wrong advice — skip
            // it with an honest reason instead. Load order is topological, so dependencies are visited first.
            var failedDependency = DependencyResolver.FindFailedRequiredDependency(manifest, failedMods.Keys);
            if (failedDependency != null)
            {
                failedMods[manifest.Id] = "required dependency " + failedDependency + " failed to load";
                logger.Warn("Skipping " + manifest.Id + ": required dependency " + failedDependency + " failed to load.");
                return;
            }

            IModEntrypoint? instance = null;
            ModContext? context = null;
            var onLoadStarted = false;
            var loadObserverStarted = false;
            var loadObserverCompleted = false;
            try
            {
                var assemblyPath = Path.Combine(package.PackagePath, manifest.EntryAssembly);
                if (!File.Exists(assemblyPath))
                {
                    failedMods[manifest.Id] = "entry assembly not found";
                    logger.Warn("Skipping " + manifest.Id + ": entry assembly not found.");
                    return;
                }

                // The registry verifies receipts during its deterministic scan, but bytes may change before
                // this callback reaches Assembly.LoadFrom. Recheck the complete inventory at the last safe
                // point so modified package code is never knowingly executed; users can repair/reinstall it.
                var receiptErrors = PackageInstallReceipt.Verify(package.PackagePath, manifest);
                if (receiptErrors.Count > 0)
                {
                    var reason = "package integrity changed before load: " + string.Join("; ", receiptErrors);
                    failedMods[manifest.Id] = reason;
                    logger.Warn("Skipping " + manifest.Id + ": " + reason);
                    return;
                }

                loadingOwnerId = manifest.Id;
                var assembly = Assembly.LoadFrom(assemblyPath);
                RegisterAssemblyOwner(assembly, manifest.Id);
                var type = assembly.GetType(manifest.EntryType, throwOnError: false);
                if (type == null)
                {
                    failedMods[manifest.Id] = "entry type not found: " + manifest.EntryType;
                    logger.Warn("Skipping " + manifest.Id + ": entry type not found: " + manifest.EntryType);
                    return;
                }

                if (!typeof(TopiaForgeMod).IsAssignableFrom(type))
                {
                    failedMods[manifest.Id] = "entry type does not derive from TopiaForgeMod";
                    logger.Warn("Skipping " + manifest.Id + ": entry type does not derive from TopiaForgeMod.");
                    return;
                }

                if (!(Activator.CreateInstance(type) is TopiaForgeMod v1Mod))
                {
                    throw new InvalidOperationException("The mod entry type could not be activated.");
                }

                instance = new V1ModEntrypoint(v1Mod);

                context = new ModContext(
                    manifest,
                    paths,
                    package.PackagePath,
                    logger.ForMod(manifest.Id),
                    serviceRegistry,
                    runtimeInfo,
                    coreGameplayServices,
                    availableManifests);
                loadObserverStarted = true;
                loadObserver?.OnLoading(manifest.Id);
                onLoadStarted = true;
                instance.OnLoad(context);
                loadObserverCompleted = true;
                loadObserver?.OnLoadCompleted(manifest.Id, succeeded: true);
                // Log before committing to loadedMods. Even a custom/failing log sink must leave this path in
                // the partial-load catch, where OnUnload and owner cleanup run, rather than stranding a ghost.
                logger.Info("Loaded mod " + manifest.Id + " " + manifest.Version + ".");
                loadedMods.Add(new LoadedMod(manifest, instance, context));
                loadedModIds.Add(manifest.Id);
                runtimeInfo.MarkProviderLoaded(manifest);
                RefreshRuntimeCapabilities();
            }
            catch (Exception ex)
            {
                var rootException = UnwrapInvocationException(ex);
                failedMods[manifest.Id] = rootException.GetType().Name + ": " + rootException.Message;
                runtimeInfo.MarkProviderFailed(manifest, failedMods[manifest.Id]);
                Exception? unloadFailure = null;
                if (context != null)
                {
                    try { context.BeginStopping(); }
                    catch (Exception cancellationException) { unloadFailure = cancellationException; }
                }
                if (loadObserverStarted && !loadObserverCompleted)
                {
                    loadObserverCompleted = true;
                    try
                    {
                        loadObserver?.OnLoadCompleted(manifest.Id, succeeded: false);
                    }
                    catch (Exception observerException)
                    {
                        unloadFailure = CombineCleanupFailures(unloadFailure, observerException);
                    }
                }

                if (onLoadStarted && instance != null)
                {
                    try
                    {
                        // Assemblies cannot unload under Mono, so give a partially initialized mod the same
                        // best-effort chance to detach static/Unity callbacks and destroy objects as a normal unload.
                        instance.OnUnload();
                    }
                    catch (Exception unloadException)
                    {
                        unloadFailure = CombineCleanupFailures(unloadFailure, unloadException);
                    }
                }

                if (context != null)
                {
                    try
                    {
                        context.DisposeLifetime();
                    }
                    catch (Exception lifetimeException)
                    {
                        unloadFailure = CombineCleanupFailures(unloadFailure, lifetimeException);
                    }
                }

                // OnLoad may have published services or acquired a scene claim before throwing. A failed mod
                // is not added to loadedMods, so UnloadAll would never otherwise clean those partial effects.
                try { CleanupOwnedFrameworkServices(manifest.Id); }
                catch (Exception cleanupException) { unloadFailure = CombineCleanupFailures(unloadFailure, cleanupException); }
                try { serviceRegistry.UnregisterOwner(manifest.Id); }
                catch (Exception serviceException) { unloadFailure = CombineCleanupFailures(unloadFailure, serviceException); }
                // Failed diagnostics must not prevent an independent package from loading.
                try { logger.Error(ex, "Failed to load mod " + manifest.Id + "."); }
                catch { }
                if (unloadFailure != null)
                {
                    try { logger.Error(unloadFailure, "Failed to clean up partially loaded mod " + manifest.Id + "."); }
                    catch { }
                }
            }
            finally
            {
                loadingOwnerId = null;
            }
        }
    }
}
