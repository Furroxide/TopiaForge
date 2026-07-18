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
        private const float CapabilityRefreshIntervalSeconds = 1f;
        private readonly ManagerPaths paths;
        private readonly IModRuntimeLogger logger;
        private readonly ModServiceRegistry serviceRegistry;
        private readonly SceneCoordinator sceneCoordinator;
        private readonly IRuntimeGameplayHost coreGameplayServices;
        private readonly RuntimeInfo runtimeInfo;
        private readonly ManifestValidationContext validationContext;
        private readonly IModLoadObserver? loadObserver;
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
        private bool initialSceneDeliveryAttempted;
        private int? sceneDeliveredBeforeInitialReplay;
        private int? replayedInitialSceneAwaitingNativeCallback;
        private float capabilityRefreshRemaining;

        public ModRuntime(
            ManagerPaths paths,
            ManagerFileLogger logger,
            ManifestValidationContext? validationContext = null)
            : this(paths, logger, validationContext, null)
        {
        }

        internal ModRuntime(
            ManagerPaths paths,
            ManagerFileLogger logger,
            ManifestValidationContext? validationContext,
            IModLoadObserver? loadObserver)
            : this(
                paths,
                logger,
                validationContext,
                loadObserver,
                new CoreGameplayServices())
        {
        }

        internal ModRuntime(
            ManagerPaths paths,
            IModRuntimeLogger logger,
            ManifestValidationContext? validationContext,
            IModLoadObserver? loadObserver,
            IRuntimeGameplayHost coreGameplayServices)
        {
            // The runtime itself owns lifecycle dispatch, so establish the thread invariant here even when a
            // non-Unity gameplay host is supplied (for example by integration tests or a future host adapter).
            // CoreGameplayServices captures the same thread as a defensive check for its direct construction.
            UnityMainThreadGuard.CaptureCurrentThread();
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.loadObserver = loadObserver;
            this.validationContext = validationContext == null
                ? ManifestValidationContext.ForCurrentRuntime()
                : validationContext.EnforceRuntimeCompatibility
                    ? validationContext
                    : new ManifestValidationContext(
                        gameVersion: validationContext.GameVersion,
                        loaderVersion: validationContext.LoaderVersion,
                        sdkVersion: validationContext.SdkVersion,
                        requireKnownGameVersion: validationContext.RequireKnownGameVersion,
                        enforceRuntimeCompatibility: true);
            runtimeInfo = new RuntimeInfo(this.validationContext.GameVersion);
            serviceRegistry = new ModServiceRegistry();
            runtimeInfo.SetCapabilityRefresher(RefreshRuntimeCapabilities);
            // Manager-owned framework service: scene-transition arbitration is available to every mod from
            // the first OnLoad and cannot be shadowed or removed through the public mod registry.
            sceneCoordinator = new SceneCoordinator(logger.Info);
            this.coreGameplayServices = coreGameplayServices
                ?? throw new ArgumentNullException(nameof(coreGameplayServices));
            coreGameplayServices.FixedUpdate += DispatchFixedUpdate;
            coreGameplayServices.LateUpdate += DispatchLateUpdate;
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
            UnityMainThreadGuard.AssertCurrent();
            if (orderedPackages == null)
            {
                throw new ArgumentNullException(nameof(orderedPackages));
            }

            var packages = orderedPackages.ToList();
            var availableManifests = packages
                .Where(package => package.Manifest != null)
                .Select(package => package.Manifest!)
                .ToArray();
            runtimeInfo.ConfigureProviders(packages);
            assemblyCatalog = new ModAssemblyResolutionCatalog(packages, pluginAssemblyPath);
            foreach (var entry in assemblyCatalog.ValidateScopes())
            {
                var reason = string.Join("; ", entry.Value);
                failedMods[entry.Key] = reason;
                logger.Warn("Skipping " + entry.Key + ": assembly preflight failed: " + reason);
            }

            foreach (var package in packages)
            {
                Load(package, availableManifests);
                if (package.Manifest != null
                    && failedMods.TryGetValue(package.Manifest.Id, out var providerFailure))
                {
                    runtimeInfo.MarkProviderFailed(package.Manifest, providerFailure);
                }
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
            UnityMainThreadGuard.AssertCurrent();
            coreGameplayServices.BeginFrame(deltaTime);
            capabilityRefreshRemaining -= Math.Max(0f, deltaTime);
            if (capabilityRefreshRemaining <= 0f)
            {
                RefreshRuntimeCapabilities();
                capabilityRefreshRemaining = CapabilityRefreshIntervalSeconds;
            }
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
            UnityMainThreadGuard.AssertCurrent();
            DispatchSceneLoadedCore(sceneName);
        }

        internal bool DispatchInitialScene(int sceneHandle, string sceneName, bool isValid)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (initialSceneDeliveryAttempted)
            {
                return false;
            }

            initialSceneDeliveryAttempted = true;
            if (!isValid || string.IsNullOrWhiteSpace(sceneName))
            {
                sceneDeliveredBeforeInitialReplay = null;
                return false;
            }

            if (sceneDeliveredBeforeInitialReplay == sceneHandle)
            {
                // A native callback won the narrow subscribe-to-active-scene race. It already delivered the
                // active scene, so the explicit replay must not deliver it a second time.
                sceneDeliveredBeforeInitialReplay = null;
                return false;
            }

            sceneDeliveredBeforeInitialReplay = null;
            replayedInitialSceneAwaitingNativeCallback = sceneHandle;
            DispatchSceneLoadedCore(sceneName);
            return true;
        }

        internal bool DispatchSceneLoaded(int sceneHandle, string sceneName, bool isValid)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!isValid || string.IsNullOrWhiteSpace(sceneName))
            {
                return false;
            }

            if (!initialSceneDeliveryAttempted)
            {
                sceneDeliveredBeforeInitialReplay = sceneHandle;
            }
            else if (replayedInitialSceneAwaitingNativeCallback.HasValue)
            {
                var duplicateInitialCallback = replayedInitialSceneAwaitingNativeCallback.Value == sceneHandle;
                replayedInitialSceneAwaitingNativeCallback = null;
                if (duplicateInitialCallback)
                {
                    return false;
                }
            }

            DispatchSceneLoadedCore(sceneName);
            return true;
        }

        private void DispatchSceneLoadedCore(string sceneName)
        {
            RefreshRuntimeCapabilities();
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

            RefreshRuntimeCapabilities();
        }

        public void UnloadAll()
        {
            UnityMainThreadGuard.AssertCurrent();
            for (var index = loadedMods.Count - 1; index >= 0; index--)
            {
                var loaded = loadedMods[index];
                var failed = false;
                try
                {
                    loaded.Instance.OnUnload();
                }
                catch (Exception ex)
                {
                    failed = true;
                    logger.Error(ex, "Mod failed during OnUnload: " + loaded.Manifest.Id);
                }

                try
                {
                    loaded.Context.DisposeLifetime();
                }
                catch (Exception ex)
                {
                    failed = true;
                    logger.Error(ex, "Mod lifetime cleanup failed for " + loaded.Manifest.Id + ".");
                }

                try
                {
                    CleanupOwnedFrameworkServices(loaded.Manifest.Id);
                    serviceRegistry.UnregisterOwner(loaded.Manifest.Id);
                }
                catch (Exception ex)
                {
                    failed = true;
                    logger.Error(ex, "Mod service cleanup failed for " + loaded.Manifest.Id + ".");
                }

                if (!failed)
                {
                    logger.Info("Unloaded mod " + loaded.Manifest.Id + ".");
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
            runtimeInfo.SetCapabilityRefresher(null);
            coreGameplayServices.FixedUpdate -= DispatchFixedUpdate;
            coreGameplayServices.LateUpdate -= DispatchLateUpdate;
            coreGameplayServices.Dispose();
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveAssembly;
        }

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
                if (loadObserverStarted && !loadObserverCompleted)
                {
                    loadObserverCompleted = true;
                    try
                    {
                        loadObserver?.OnLoadCompleted(manifest.Id, succeeded: false);
                    }
                    catch (Exception observerException)
                    {
                        unloadFailure = observerException;
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

        private void DispatchFixedUpdate(GameTimeSample sample)
        {
            UnityMainThreadGuard.AssertCurrent();
            var count = loadedMods.Count;
            for (var index = 0; index < count; index++)
            {
                loadedMods[index].Context.RaiseFixedUpdate(sample);
            }
        }

        private void DispatchLateUpdate(GameTimeSample sample)
        {
            UnityMainThreadGuard.AssertCurrent();
            var count = loadedMods.Count;
            for (var index = 0; index < count; index++)
            {
                loadedMods[index].Context.RaiseLateUpdate(sample);
            }
        }

        private void RefreshRuntimeCapabilities()
        {
            UnityMainThreadGuard.AssertCurrent();
            RuntimeCapabilityProbe.Refresh(runtimeInfo, serviceRegistry);
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

        private static Exception CombineCleanupFailures(Exception? current, Exception next)
        {
            return current == null ? next : new AggregateException(current, next);
        }

        private static Exception UnwrapInvocationException(Exception exception)
        {
            while (exception is TargetInvocationException invocation && invocation.InnerException != null)
            {
                exception = invocation.InnerException;
            }

            return exception;
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
                sceneCoordinator.ReleaseOwner(ownerModId);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Scene claim cleanup failed for " + ownerModId + ".");
            }
        }

        private sealed class LoadedMod
        {
            public LoadedMod(ModManifest manifest, IModEntrypoint instance, ModContext context)
            {
                Manifest = manifest;
                Instance = instance;
                Context = context;
            }

            public ModManifest Manifest { get; }
            public IModEntrypoint Instance { get; }
            public ModContext Context { get; }
        }

        private interface IModEntrypoint
        {
            void OnLoad(IModContext context);
            void OnUnload();
        }

        private sealed class V1ModEntrypoint : IModEntrypoint
        {
            private readonly TopiaForgeMod mod;

            public V1ModEntrypoint(TopiaForgeMod mod)
            {
                this.mod = mod;
            }

            public void OnLoad(IModContext context)
            {
                mod.Load(context);
            }

            public void OnUnload()
            {
                mod.Unload();
            }
        }

    }
}
