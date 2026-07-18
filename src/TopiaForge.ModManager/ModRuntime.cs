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
            DispatchSceneLoadedCore(new SceneLoadEvent(sceneName, SceneLoadMode.Single, true));
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
            DispatchSceneLoadedCore(new SceneLoadEvent(sceneName, SceneLoadMode.Single, true));
            return true;
        }

        internal bool DispatchSceneLoaded(int sceneHandle, string sceneName, bool isValid)
        {
            return DispatchSceneLoaded(
                sceneHandle,
                sceneName,
                isValid,
                SceneLoadMode.Single,
                isActive: true);
        }

        internal bool DispatchSceneLoaded(
            int sceneHandle,
            string sceneName,
            bool isValid,
            SceneLoadMode mode,
            bool isActive)
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

            DispatchSceneLoadedCore(new SceneLoadEvent(sceneName, mode, isActive));
            return true;
        }

        internal bool DispatchSceneActivated(int sceneHandle, string sceneName, bool isValid, SceneLoadMode mode)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!isValid || string.IsNullOrWhiteSpace(sceneName))
            {
                return false;
            }

            DispatchSceneActivatedCore(new SceneLoadEvent(sceneName, mode, true));
            return true;
        }

        private void DispatchSceneLoadedCore(SceneLoadEvent scene)
        {
            RefreshRuntimeCapabilities();
            var count = loadedMods.Count;
            for (var index = 0; index < count; index++)
            {
                var loaded = loadedMods[index];
                try
                {
                    loaded.Context.RaiseSceneLoaded(scene);
                    sceneFailureLogged.Remove(loaded.Manifest.Id);
                }
                catch (Exception ex)
                {
                    if (sceneFailureLogged.Add(loaded.Manifest.Id))
                    {
                        logger.Error(ex, "Mod failed during SceneLoaded '" + scene.SceneName + "': " + loaded.Manifest.Id);
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
    }
}
