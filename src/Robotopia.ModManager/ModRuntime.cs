using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Robotopia.ModManager.Core;
using Robotopia.Mods;

namespace Robotopia.ModManager
{
    public sealed class ModRuntime
    {
        private readonly ManagerPaths paths;
        private readonly ManagerFileLogger logger;
        private readonly ModServiceRegistry serviceRegistry;
        private readonly List<LoadedMod> loadedMods = new List<LoadedMod>();
        private readonly List<string> searchPaths = new List<string>();
        private readonly string pluginAssemblyPath;
        private readonly HashSet<string> updateFailureLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> sceneFailureLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> failedMods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ModRuntime(ManagerPaths paths, ManagerFileLogger logger)
        {
            this.paths = paths;
            this.logger = logger;
            serviceRegistry = new ModServiceRegistry();
            pluginAssemblyPath = Path.GetDirectoryName(typeof(ModRuntime).Assembly.Location) ?? string.Empty;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }

        public IReadOnlyCollection<string> LoadedModIds => loadedMods.Select(m => m.Manifest.Id).ToList();

        /// <summary>Why a mod in the load order did not come up (skip reason or exception), or null.</summary>
        public string? GetLoadFailure(string id)
        {
            return failedMods.TryGetValue(id, out var reason) ? reason : null;
        }

        public void Load(IEnumerable<ModPackage> orderedPackages)
        {
            foreach (var package in orderedPackages)
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
            foreach (var loaded in loadedMods.ToArray())
            {
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
            foreach (var loaded in loadedMods.ToArray())
            {
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
            foreach (var loaded in loadedMods.AsEnumerable().Reverse().ToArray())
            {
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

            try
            {
                var assemblyPath = Path.Combine(package.PackagePath, manifest.EntryAssembly);
                if (!File.Exists(assemblyPath))
                {
                    failedMods[manifest.Id] = "entry assembly not found";
                    logger.Warn("Skipping " + manifest.Id + ": entry assembly not found.");
                    return;
                }

                searchPaths.Add(package.PackagePath);
                var assembly = Assembly.LoadFrom(assemblyPath);
                var type = assembly.GetType(manifest.EntryType, throwOnError: false);
                if (type == null)
                {
                    failedMods[manifest.Id] = "entry type not found: " + manifest.EntryType;
                    logger.Warn("Skipping " + manifest.Id + ": entry type not found: " + manifest.EntryType);
                    return;
                }

                if (!typeof(IRobotopiaMod).IsAssignableFrom(type))
                {
                    failedMods[manifest.Id] = "entry type does not implement IRobotopiaMod";
                    logger.Warn("Skipping " + manifest.Id + ": entry type does not implement IRobotopiaMod.");
                    return;
                }

                var instance = (IRobotopiaMod)Activator.CreateInstance(type);
                var context = new ModContext(manifest, paths, package.PackagePath, logger.ForMod(manifest.Id), serviceRegistry);
                instance.OnLoad(context);
                loadedMods.Add(new LoadedMod(manifest, instance, context));
                logger.Info("Loaded mod " + manifest.Id + " " + manifest.Version + ".");
            }
            catch (Exception ex)
            {
                failedMods[manifest.Id] = ex.GetType().Name + ": " + ex.Message;
                logger.Error(ex, "Failed to load mod " + manifest.Id + ".");
            }
        }

        private Assembly? ResolveAssembly(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name);
            var requestedName = requested.Name + ".dll";
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, requested.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return assembly;
                }
            }

            var pathsToSearch = string.IsNullOrEmpty(pluginAssemblyPath)
                ? searchPaths
                : new[] { pluginAssemblyPath }.Concat(searchPaths);
            foreach (var path in pathsToSearch.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(path, requestedName);
                if (File.Exists(candidate))
                {
                    return Assembly.LoadFrom(candidate);
                }
            }

            return null;
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
        }

        private sealed class LoadedMod
        {
            public LoadedMod(ModManifest manifest, IRobotopiaMod instance, ModContext context)
            {
                Manifest = manifest;
                Instance = instance;
                Context = context;
            }

            public ModManifest Manifest { get; }
            public IRobotopiaMod Instance { get; }
            public ModContext Context { get; }
        }
    }
}
