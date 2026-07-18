using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext : IModContext, IUnityInteropContext
    {
        private readonly OwnerModLifetime ownerLifetime;
        private readonly ModEvents modEvents;
        private readonly IUnityInteropService? unityInterop;

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

        internal ModContext(
            ModManifest manifest,
            ManagerPaths managerPaths,
            string packagePath,
            IModLogger logger,
            ModServiceRegistry serviceRegistry,
            IRuntimeInfo? runtimeInfo,
            IGameplayContextFactory? gameplayFactory,
            IEnumerable<ModManifest>? availableManifests = null)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (managerPaths == null) throw new ArgumentNullException(nameof(managerPaths));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (serviceRegistry == null) throw new ArgumentNullException(nameof(serviceRegistry));

            Identity = new ModIdentity(manifest.Id, manifest.Name, SemanticVersion.Parse(manifest.Version));
            Runtime = runtimeInfo ?? new RuntimeInfo();
            Logger = logger;
            ownerLifetime = new OwnerModLifetime();
            Lifetime = ownerLifetime;
            modEvents = new ModEvents(ownerLifetime, logger);
            Events = modEvents;

            var configPath = managerPaths.GetConfigPath(manifest.Id);
            var dataPath = managerPaths.GetDataPath(manifest.Id);
            Directory.CreateDirectory(dataPath);
            Files = new ModFiles(packagePath, dataPath, Lifetime);
            Config = new ModConfigService(configPath, logger);
            Storage = new ModStorageService(dataPath);

            var gameplay = gameplayFactory?.Create(
                    Identity.Id,
                    packagePath,
                    dataPath,
                    Lifetime,
                    Logger)
                ?? GameplayContextServices.Unavailable(Lifetime);
            Input = gameplay.Input;
            Time = gameplay.Time;
            Scheduler = gameplay.Scheduler;
            Player = gameplay.Player;
            Scenes = gameplay.Scenes;
            Entities = gameplay.Entities;
            Physics = gameplay.Physics;
            Interactions = gameplay.Interactions;
            Items = gameplay.Items;
            Assets = gameplay.Assets;
            Audio = gameplay.Audio;
            Ui = gameplay.Ui;
            unityInterop = manifest.Capabilities.Any(capability => string.Equals(
                capability,
                "unsafe-native",
                StringComparison.OrdinalIgnoreCase))
                ? gameplay.UnityInterop
                : null;

            Localization = new OwnerLocalizationService(Lifetime);
            Commands = new OwnerCommandService(Identity.Id, Lifetime, Logger, serviceRegistry);
            Diagnostics = new OwnerDiagnosticsService(Logger);
            Extensions = new OwnerExtensionService(
                Identity.Id,
                VisibleDependencyIds(manifest, availableManifests),
                Lifetime,
                serviceRegistry);
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
        public IRuntimeInfo Runtime { get; }
        public IModLogger Logger { get; }
        public IModLifetime Lifetime { get; }
        public IModEvents Events { get; }
        public IModFiles Files { get; }
        public IModConfigService Config { get; }
        public IModStorageService Storage { get; }
        public IInputService Input { get; }
        public IGameTime Time { get; }
        public IModScheduler Scheduler { get; }
        public IPlayerService Player { get; }
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

        IUnityInteropService IUnityInteropContext.UnityInterop => unityInterop
            ?? throw new InvalidOperationException(
                "Unity interop requires the 'unsafe-native' manifest capability and the TopiaForge.Mods.Interop.Unity package.");

        internal void RaiseUpdate(float deltaTime) => modEvents.RaiseUpdate(deltaTime);
        internal void RaiseSceneLoaded(SceneLoadEvent scene) => modEvents.RaiseSceneLoaded(scene);
        internal void RaiseSceneActivated(SceneLoadEvent scene) => modEvents.RaiseSceneActivated(scene);
        internal void RaiseFixedUpdate(GameTimeSample sample) => modEvents.RaiseFixedUpdate(sample);
        internal void RaiseLateUpdate(GameTimeSample sample) => modEvents.RaiseLateUpdate(sample);
        internal void DisposeLifetime() => ownerLifetime.Dispose();
    }
}
