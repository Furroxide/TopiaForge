using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext : IModContext, IUnityInteropContext, IInternalSceneTransitionContext, IInternalWorldRuntimeContext,
        IInternalReplicationContext, IInternalPlayerContext, IInternalMapStorageContext
    {
        private readonly OwnerModLifetime ownerLifetime;
        private readonly ModEvents modEvents;
        private readonly IUnityInteropService? unityInterop;
        private readonly string packagePath;
        private readonly string dataPath;
        private readonly IGameplayContextFactory? gameplayFactory;
        private readonly ModServiceRegistry serviceRegistry;
        private readonly string[] visibleDependencies;
        private readonly bool allowUnityInterop;
        private readonly MultiplayerReplication replicationServices;
        private readonly RuntimeReplication replication;

        public ModContext(
            ModManifest manifest,
            ManagerPaths managerPaths,
            string packagePath,
            IModLogger logger,
            ModServiceRegistry serviceRegistry,
            IRuntimeInfo? runtimeInfo = null)
            : this(manifest, managerPaths, packagePath, logger, serviceRegistry, runtimeInfo, null)
        {
        }

        /// <param name="replicationServices">
        /// The process's replication services; ModRuntime passes its own, and a context made without them is single player.
        /// The context's network profile comes from them: the runtime's network class for the manifest, process-wide.
        /// </param>
        internal ModContext(
            ModManifest manifest,
            ManagerPaths managerPaths,
            string packagePath,
            IModLogger logger,
            ModServiceRegistry serviceRegistry,
            IRuntimeInfo? runtimeInfo,
            IGameplayContextFactory? gameplayFactory,
            IEnumerable<ModManifest>? availableManifests = null,
            MultiplayerReplication? replicationServices = null)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (managerPaths == null) throw new ArgumentNullException(nameof(managerPaths));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (serviceRegistry == null) throw new ArgumentNullException(nameof(serviceRegistry));

            this.packagePath = packagePath;
            MapCacheDirectory = Path.Combine(managerPaths.Root, "cache", "maps");
            PublishedMapLibraryPath = Path.Combine(managerPaths.Root, "published-map-sources-v1.json");
            this.gameplayFactory = gameplayFactory;
            this.serviceRegistry = serviceRegistry;
            this.replicationServices = replicationServices ?? MultiplayerReplication.Offline();
            visibleDependencies = VisibleDependencyIds(manifest, availableManifests).ToArray();
            allowUnityInterop = manifest.Capabilities.Any(capability => string.Equals(
                capability, "unsafe-native", StringComparison.OrdinalIgnoreCase));
            Identity = new ModIdentity(manifest.Id, manifest.Name, SemanticVersion.Parse(manifest.Version));
            NetProfile = this.replicationServices.ProfileFor(manifest);
            Runtime = runtimeInfo ?? new RuntimeInfo();
            Logger = logger;
            ownerLifetime = new OwnerModLifetime();
            Lifetime = ownerLifetime;
            replication = this.replicationServices.Bind(Identity.Id, NetProfile, Lifetime);
            inputActivity = new ModInputActivity();
            try
            {
                modEvents = new ModEvents(ownerLifetime, logger);
                Events = modEvents;

                var configPath = managerPaths.GetConfigPath(manifest.Id);
                dataPath = managerPaths.GetDataPath(manifest.Id);
                Directory.CreateDirectory(dataPath);
                Files = new ModFiles(packagePath, dataPath, Lifetime);
                Config = new ModConfigService(configPath, logger);
                LocalStorage = new LocalModStorageService(dataPath);

                var gameplay = gameplayFactory?.Create(
                        Identity.Id,
                        packagePath,
                        dataPath,
                        Lifetime,
                        Logger,
                        NetProfile)
                    ?? GameplayContextServices.Unavailable(Lifetime);
                Input = new NetInputService(gameplay.Input, inputActivity, Lifetime);
                Time = gameplay.Time;
                Scheduler = new NetScheduler(gameplay.Scheduler, replication);
                LocalPlayer = new LifetimePlayerService(gameplay.LocalPlayer, Lifetime);
                Scenes = gameplay.Scenes;
                Entities = new LifetimeEntityService(new NetEntityService(gameplay.Entities, replication), Lifetime);
                Physics = new NetPhysicsService(gameplay.Physics, replication, Lifetime);
                Interactions = new NetInteractionService(gameplay.Interactions, replication);
                Items = new NetItemService(gameplay.Items, replication);
                Assets = new NetAssetService(gameplay.Assets, replication);
                Audio = new NetAudioService(gameplay.Audio, replication);
                Ui = new NetUiService(gameplay.Ui, replication);
                replication.Ui = gameplay.Ui;
                playerContext = this.replicationServices.Players?.Bind(
                    Identity.Id, NetProfile.NetworkClass == ModNetworkClass.Shared, Logger, Lifetime, LocalPlayer, gameplay.Ui, inputActivity);
                if (playerContext != null) playerContext.AppearanceProfile = NetProfile;
                SceneTransitions = gameplay.SceneTransitions;
                WorldRuntime = gameplay.WorldRuntime;
                authoredMapInterop = gameplay.UnityInterop;
                unityInterop = allowUnityInterop ? gameplay.UnityInterop : null;

                Localization = new OwnerLocalizationService(Lifetime);
                Commands = new OwnerCommandService(Identity.Id, Lifetime, Logger, serviceRegistry, replication.Intents);
                Diagnostics = new OwnerDiagnosticsService(Logger);
                Extensions = new OwnerExtensionService(
                    Identity.Id,
                    visibleDependencies,
                    Lifetime,
                    serviceRegistry, this);
            }
            catch (Exception constructionFailure)
            {
                try { ownerLifetime.Dispose(); }
                catch (Exception cleanupFailure)
                { throw new AggregateException("Context construction and cleanup failed.", constructionFailure, cleanupFailure); }
                throw;
            }
        }

        private static IEnumerable<string> VisibleDependencyIds(
            ModManifest manifest,
            IEnumerable<ModManifest>? availableManifests)
        {
            var providers = (availableManifests ?? Enumerable.Empty<ModManifest>())
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.Id))
                .GroupBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in manifest.Dependencies.Concat(manifest.OptionalDependencies))
            {
                if (providers.TryGetValue(dependency.Key, out var provider) &&
                    VersionUtil.AllowsRange(provider.Version, dependency.Value))
                {
                    yield return provider.Id;
                }
            }
        }

        public ModIdentity Identity { get; }
        string IInternalMapStorageContext.MapCacheDirectory => MapCacheDirectory;
        private string MapCacheDirectory { get; }
        string IInternalMapStorageContext.PublishedMapLibraryPath => PublishedMapLibraryPath;
        private string PublishedMapLibraryPath { get; }
        public IRuntimeInfo Runtime { get; }
        public IModLogger Logger { get; }
        public IModLifetime Lifetime { get; }
        public IModEvents Events { get; }
        public IModFiles Files { get; }
        public IModConfigService Config { get; }
        public ILocalModStorageService LocalStorage { get; }
        public IInputService Input { get; }
        public IGameTime Time { get; }
        public IModScheduler Scheduler { get; }
        public ILocalPlayerService LocalPlayer { get; }
        public ISceneService Scenes { get; }
        public IEntityService Entities { get; }
        public IPhysicsService Physics { get; }
        public IInteractionService Interactions { get; }
        public IItemService Items { get; }
        public IAssetService Assets { get; }
        public IAudioService Audio { get; }
        public IUiService Ui { get; }
        public ILocalizationService Localization { get; }
        public ICommandService Commands { get; }
        public IDiagnosticsService Diagnostics { get; }
        public IExtensionService Extensions { get; }
        public IInternalSceneTransitionService SceneTransitions { get; }
        public IInternalWorldRuntimeService WorldRuntime { get; }

        /// <summary>Gets where this context runs: its mod's network class and its execution context.</summary>
        internal ModNetProfile NetProfile { get; }

        /// <summary>Gets this context's replication seam, which its network decorators record through.</summary>
        internal RuntimeReplication NetReplication => replication;

        IRuntimeReplication IInternalReplicationContext.Replication => replication;

        IUnityInteropService IUnityInteropContext.UnityInterop => unityInterop
            ?? throw new InvalidOperationException(
                "Unity interop requires the 'unsafe-native' manifest capability and the TopiaForge.Mods.Interop.Unity package.");

        internal void RaiseUpdate(float deltaTime) => RaiseFrameUpdate(deltaTime);
        internal void RaiseSceneLoaded(SceneLoadEvent scene) => modEvents.RaiseSceneLoaded(scene);
        internal void RaiseSceneActivated(SceneLoadEvent scene) => modEvents.RaiseSceneActivated(scene);
        internal void RaiseSceneLifecycle(SceneLifecycleEvent scene) => modEvents.RaiseSceneLifecycle(scene);
        internal void RaiseFixedUpdate(GameTimeSample sample) => modEvents.RaiseFixedUpdate(sample);
        internal void RaiseLateUpdate(GameTimeSample sample) => RaiseFrameLateUpdate(sample);
        internal void DisposeLifetime()
        {
            lock (scopeSync)
            {
                if (childScopes.Count != 0)
                    throw new InvalidOperationException("Session scopes must drain before their package context is disposed.");
            }
            ownerLifetime.Dispose();
        }
    }
}
