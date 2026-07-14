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
    public sealed class ModRuntime
    {
        // Diagnostic owner for registrations protected by ModServiceRegistry's framework-only path.
        private const string FrameworkServiceOwnerId = "io.github.furroxide.topiaforge.modmanager";

        private readonly ManagerPaths paths;
        private readonly ManagerFileLogger logger;
        private readonly ModServiceRegistry serviceRegistry;
        private readonly SceneCoordinator sceneCoordinator;
        private readonly List<LoadedMod> loadedMods = new List<LoadedMod>();
        private readonly List<string> loadedModIds = new List<string>();
        private readonly ReadOnlyCollection<string> loadedModIdsView;
        private readonly Dictionary<Assembly, string> assemblyOwners = new Dictionary<Assembly, string>();
        private readonly string pluginAssemblyPath;
        private readonly HashSet<string> updateFailureLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> sceneFailureLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> failedMods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private ModAssemblyResolutionCatalog? assemblyCatalog;
        private string? loadingOwnerId;

        public ModRuntime(ManagerPaths paths, ManagerFileLogger logger)
        {
            this.paths = paths;
            this.logger = logger;
            serviceRegistry = new ModServiceRegistry();
            // Manager-owned framework service: scene-transition arbitration is available to every mod from
            // the first OnLoad and cannot be shadowed or removed through the public mod registry.
            sceneCoordinator = new SceneCoordinator(logger.Info);
            serviceRegistry.RegisterFramework<ISceneCoordinator>(FrameworkServiceOwnerId, sceneCoordinator);
            pluginAssemblyPath = Path.GetDirectoryName(typeof(ModRuntime).Assembly.Location) ?? string.Empty;
            loadedModIdsView = loadedModIds.AsReadOnly();
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }

        public IReadOnlyCollection<string> LoadedModIds => loadedModIdsView;

        /// <summary>Why a mod in the load order did not come up (skip reason or exception), or null.</summary>
        public string? GetLoadFailure(string id)
        {
            return failedMods.TryGetValue(id, out var reason) ? reason : null;
        }

        public void Load(IEnumerable<ModPackage> orderedPackages)
        {
            if (orderedPackages == null)
            {
                throw new ArgumentNullException(nameof(orderedPackages));
            }

            var packages = orderedPackages.ToList();
            assemblyCatalog = new ModAssemblyResolutionCatalog(packages, pluginAssemblyPath);
            foreach (var entry in assemblyCatalog.ValidateScopes())
            {
                var reason = string.Join("; ", entry.Value);
                failedMods[entry.Key] = reason;
                logger.Warn("Skipping " + entry.Key + ": assembly preflight failed: " + reason);
            }

            foreach (var package in packages)
            {
                Load(package);
            }
        }

        public bool IsLoaded(string id)
        {
            return loadedMods.Any(m => string.Equals(m.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public T? GetService<T>() where T : class
        {
            return serviceRegistry.Get<T>();
        }

        public void DispatchUpdate(float deltaTime)
        {
            var count = loadedMods.Count;
            for (var index = 0; index < count; index++)
            {
                var loaded = loadedMods[index];
                try
                {
                    loaded.Context.RaiseUpdate(deltaTime);
                    updateFailureLogged.Remove(loaded.Manifest.Id);
                }
                catch (Exception ex)
                {
                    if (updateFailureLogged.Add(loaded.Manifest.Id))
                    {
                        logger.Error(ex, "Mod failed during Update: " + loaded.Manifest.Id);
                    }
                }
            }
        }

        public void DispatchSceneLoaded(string sceneName)
        {
            var count = loadedMods.Count;
            for (var index = 0; index < count; index++)
            {
                var loaded = loadedMods[index];
                try
                {
                    loaded.Context.RaiseSceneLoaded(sceneName);
                    sceneFailureLogged.Remove(loaded.Manifest.Id);
                }
                catch (Exception ex)
                {
                    if (sceneFailureLogged.Add(loaded.Manifest.Id))
                    {
                        logger.Error(ex, "Mod failed during SceneLoaded '" + sceneName + "': " + loaded.Manifest.Id);
                    }
                }
            }
        }

        public void UnloadAll()
        {
            for (var index = loadedMods.Count - 1; index >= 0; index--)
            {
                var loaded = loadedMods[index];
                try
                {
                    loaded.Instance.OnUnload();
                    CleanupOwnedFrameworkServices(loaded.Manifest.Id);
                    serviceRegistry.UnregisterOwner(loaded.Manifest.Id);
                    logger.Info("Unloaded mod " + loaded.Manifest.Id + ".");
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Mod failed during OnUnload: " + loaded.Manifest.Id);
                    CleanupOwnedFrameworkServices(loaded.Manifest.Id);
                    serviceRegistry.UnregisterOwner(loaded.Manifest.Id);
                }
            }

            loadedMods.Clear();
            loadedModIds.Clear();
            assemblyOwners.Clear();
            assemblyCatalog = null;
            loadingOwnerId = null;
            updateFailureLogged.Clear();
            sceneFailureLogged.Clear();
            failedMods.Clear();
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
        }

        private void Load(ModPackage package)
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

            ITopiaForgeMod? instance = null;
            var onLoadStarted = false;
            try
            {
                var assemblyPath = Path.Combine(package.PackagePath, manifest.EntryAssembly);
                if (!File.Exists(assemblyPath))
                {
                    failedMods[manifest.Id] = "entry assembly not found";
                    logger.Warn("Skipping " + manifest.Id + ": entry assembly not found.");
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

                if (!typeof(ITopiaForgeMod).IsAssignableFrom(type))
                {
                    failedMods[manifest.Id] = "entry type does not implement ITopiaForgeMod";
                    logger.Warn("Skipping " + manifest.Id + ": entry type does not implement ITopiaForgeMod.");
                    return;
                }

                instance = (ITopiaForgeMod)Activator.CreateInstance(type);
                var context = new ModContext(manifest, paths, package.PackagePath, logger.ForMod(manifest.Id), serviceRegistry);
                onLoadStarted = true;
                instance.OnLoad(context);
                // Log before committing to loadedMods. Even a custom/failing log sink must leave this path in
                // the partial-load catch, where OnUnload and owner cleanup run, rather than stranding a ghost.
                logger.Info("Loaded mod " + manifest.Id + " " + manifest.Version + ".");
                loadedMods.Add(new LoadedMod(manifest, instance, context));
                loadedModIds.Add(manifest.Id);
            }
            catch (Exception ex)
            {
                failedMods[manifest.Id] = ex.GetType().Name + ": " + ex.Message;
                Exception? unloadFailure = null;
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
                        unloadFailure = unloadException;
                    }
                }

                // OnLoad may have published services or acquired a scene claim before throwing. A failed mod
                // is not added to loadedMods, so UnloadAll would never otherwise clean those partial effects.
                CleanupOwnedFrameworkServices(manifest.Id);
                serviceRegistry.UnregisterOwner(manifest.Id);
                // Diagnostics are deliberately last: cleanup is mandatory even if every log sink is broken.
                logger.Error(ex, "Failed to load mod " + manifest.Id + ".");
                if (unloadFailure != null)
                {
                    logger.Error(unloadFailure, "Failed to clean up partially loaded mod " + manifest.Id + ".");
                }
            }
            finally
            {
                loadingOwnerId = null;
            }
        }

        private Assembly? ResolveAssembly(object sender, ResolveEventArgs args)
        {
            AssemblyName requested;
            try
            {
                requested = new AssemblyName(args.Name);
            }
            catch
            {
                return null;
            }

            var requesterOwner = ResolveRequesterOwner(args.RequestingAssembly) ?? loadingOwnerId;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName definition;
                try
                {
                    definition = assembly.GetName();
                }
                catch
                {
                    continue;
                }

                if (!ModAssemblyResolutionCatalog.IdentityMatches(requested, definition))
                {
                    continue;
                }

                if (!assemblyOwners.TryGetValue(assembly, out var candidateOwner))
                {
                    candidateOwner = ResolveRequesterOwner(assembly);
                }

                // Runtime/framework assemblies are globally visible. Private mod assemblies are visible only
                // to their owner and that owner's explicit dependency consumers.
                if (candidateOwner == null ||
                    (requesterOwner != null &&
                     assemblyCatalog?.IsOwnerVisible(requesterOwner, candidateOwner) == true))
                {
                    return assembly;
                }
            }

            var candidate = assemblyCatalog?.FindCandidate(requesterOwner, requested);
            if (candidate == null)
            {
                return null;
            }

            var resolved = Assembly.LoadFrom(candidate);
            if (assemblyCatalog!.TryGetOwner(candidate, out var resolvedOwner))
            {
                RegisterAssemblyOwner(resolved, resolvedOwner);
            }

            return resolved;
        }

        private string? ResolveRequesterOwner(Assembly? assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            if (assemblyOwners.TryGetValue(assembly, out var owner))
            {
                return owner;
            }

            try
            {
                return assemblyCatalog != null && assemblyCatalog.TryGetOwner(assembly.Location, out owner)
                    ? owner
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private void RegisterAssemblyOwner(Assembly assembly, string owner)
        {
            if (assemblyOwners.TryGetValue(assembly, out var existingOwner) &&
                !string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileLoadException("Assembly '" + assembly.FullName + "' is already owned by "
                    + existingOwner + " and cannot also be loaded for " + owner + ".");
            }

            assemblyOwners[assembly] = owner;
        }

        private void CleanupOwnedFrameworkServices(string ownerModId)
        {
            try
            {
                serviceRegistry.Get<IAssetBundleService>()?.UnloadOwner(ownerModId);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Asset service cleanup failed for " + ownerModId + ".");
            }

            try
            {
                serviceRegistry.Get<IPromptOverrideRegistry>()?.UnregisterOwner(ownerModId);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Prompt override cleanup failed for " + ownerModId + ".");
            }

            try
            {
                sceneCoordinator.ReleaseOwner(ownerModId);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Scene claim cleanup failed for " + ownerModId + ".");
            }
        }

        private sealed class LoadedMod
        {
            public LoadedMod(ModManifest manifest, ITopiaForgeMod instance, ModContext context)
            {
                Manifest = manifest;
                Instance = instance;
                Context = context;
            }

            public ModManifest Manifest { get; }
            public ITopiaForgeMod Instance { get; }
            public ModContext Context { get; }
        }
    }
}
