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
    internal sealed class ModContext : IModContext, IUnityInteropContext
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
        internal void RaiseSceneLoaded(string sceneName) => modEvents.RaiseSceneLoaded(sceneName);
        internal void RaiseFixedUpdate(GameTimeSample sample) => modEvents.RaiseFixedUpdate(sample);
        internal void RaiseLateUpdate(GameTimeSample sample) => modEvents.RaiseLateUpdate(sample);
        internal void DisposeLifetime() => ownerLifetime.Dispose();

        private sealed class ModConfigService : IModConfigService
        {
            private readonly string path;
            private readonly IModLogger logger;
            private readonly object sync = new object();

            public ModConfigService(string path, IModLogger logger)
            {
                this.path = path;
                this.logger = logger;
            }

            public OperationResult<T> Load<T>(ConfigDefinition<T> definition) where T : class
            {
                if (definition == null) throw new ArgumentNullException(nameof(definition));
                lock (sync)
                {
                    if (!File.Exists(path) && !File.Exists(path + JsonUtil.BackupSuffix))
                    {
                        var defaults = CreateDefaults(definition);
                        if (!defaults.TryGetValue(out var defaultValue)) return defaults;
                        var saved = SaveCore(definition, defaultValue);
                        return saved.Succeeded
                            ? OperationResult<T>.Success(defaultValue)
                            : OperationResult<T>.Failure(saved.ErrorCode, saved.ErrorMessage);
                    }

                    try
                    {
                        var envelope = JsonUtil.LoadPersistentFile(path, new ConfigEnvelope<T>());
                        var storedVersion = envelope.SchemaVersion;
                        var value = envelope.Value;
                        if (value == null)
                        {
                            var fallback = definition.CreateDefault();
                            value = JsonUtil.LoadPersistentFile(path, fallback);
                            storedVersion = 0;
                        }

                        if (storedVersion > definition.SchemaVersion)
                        {
                            return OperationResult<T>.Failure(
                                ModErrorCode.InvalidState,
                                "Config schema " + storedVersion + " is newer than supported schema "
                                + definition.SchemaVersion + ".");
                        }

                        if (storedVersion < definition.SchemaVersion)
                        {
                            if (definition.Migrate == null)
                            {
                                return OperationResult<T>.Failure(
                                    ModErrorCode.InvalidState,
                                    "Config schema " + storedVersion + " requires a migration to schema "
                                    + definition.SchemaVersion + ".");
                            }

                            var migration = definition.Migrate(storedVersion, value);
                            if (!migration.Succeeded || migration.Value == null)
                            {
                                return OperationResult<T>.Failure(migration.ErrorCode, migration.ErrorMessage);
                            }

                            value = migration.Value;
                            var saved = SaveCore(definition, value);
                            if (!saved.Succeeded)
                            {
                                return OperationResult<T>.Failure(saved.ErrorCode, saved.ErrorMessage);
                            }
                        }

                        var validation = definition.Validate(value);
                        return validation != null && validation.Succeeded && validation.Value == true
                            ? OperationResult<T>.Success(value)
                            : OperationResult<T>.Failure(
                                validation == null || validation.Succeeded
                                    ? ModErrorCode.InvalidArgument
                                    : validation.ErrorCode,
                                validation == null
                                    ? "The config validator returned no result."
                                    : validation.Succeeded
                                        ? "The config validator rejected the value."
                                        : validation.ErrorMessage);
                    }
                    catch (Exception exception)
                    {
                        logger.Error(exception, "Failed to load typed mod configuration.");
                        return OperationResult<T>.Failure(ModErrorCode.Io, exception.Message);
                    }
                }
            }

            public OperationResult<bool> Save<T>(ConfigDefinition<T> definition, T value) where T : class
            {
                if (definition == null) throw new ArgumentNullException(nameof(definition));
                if (value == null) throw new ArgumentNullException(nameof(value));
                lock (sync)
                {
                    return SaveCore(definition, value);
                }
            }

            public OperationResult<T> Reset<T>(ConfigDefinition<T> definition) where T : class
            {
                if (definition == null) throw new ArgumentNullException(nameof(definition));
                lock (sync)
                {
                    var defaults = CreateDefaults(definition);
                    if (!defaults.TryGetValue(out var value)) return defaults;
                    var save = SaveCore(definition, value);
                    return save.Succeeded
                        ? OperationResult<T>.Success(value)
                        : OperationResult<T>.Failure(save.ErrorCode, save.ErrorMessage);
                }
            }

            private static OperationResult<T> CreateDefaults<T>(ConfigDefinition<T> definition) where T : class
            {
                var value = definition.CreateDefault();
                if (value == null)
                {
                    return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The config default factory returned null.");
                }

                var validation = definition.Validate(value);
                return validation != null && validation.Succeeded && validation.Value == true
                    ? OperationResult<T>.Success(value)
                    : OperationResult<T>.Failure(
                        validation == null || validation.Succeeded
                            ? ModErrorCode.InvalidArgument
                            : validation.ErrorCode,
                        validation == null
                            ? "The config validator returned no result."
                            : validation.Succeeded
                                ? "The config validator rejected the value."
                                : validation.ErrorMessage);
            }

            private OperationResult<bool> SaveCore<T>(ConfigDefinition<T> definition, T value) where T : class
            {
                try
                {
                    var validation = definition.Validate(value);
                    if (validation == null || !validation.Succeeded || validation.Value != true)
                    {
                        return OperationResult<bool>.Failure(
                            validation == null || validation.Succeeded
                                ? ModErrorCode.InvalidArgument
                                : validation.ErrorCode,
                            validation == null
                                ? "The config validator returned no result."
                                : validation.Succeeded
                                    ? "The config validator rejected the value."
                                    : validation.ErrorMessage);
                    }

                    JsonUtil.SaveFile(path, new ConfigEnvelope<T>
                    {
                        SchemaVersion = definition.SchemaVersion,
                        Value = value
                    });
                    return OperationResult<bool>.Success(true);
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Failed to save typed mod configuration.");
                    return OperationResult<bool>.Failure(ModErrorCode.Io, exception.Message);
                }
            }

            [DataContract]
            private sealed class ConfigEnvelope<T> where T : class
            {
                [DataMember(Name = "schemaVersion")]
                public int SchemaVersion { get; set; }

                [DataMember(Name = "value")]
                public T? Value { get; set; }
            }
        }

        private sealed class ModStorageService : IModStorageService
        {
            private const string StoryFlagPrefix = "story-flags/";
            private readonly string root;
            private readonly object sync = new object();

            public ModStorageService(string dataPath)
            {
                root = Path.Combine(dataPath, "storage");
            }

            public bool Contains(string key) => File.Exists(Resolve(key));

            public OperationResult<T> Load<T>(string key) where T : class
            {
                var file = Resolve(key);
                lock (sync)
                {
                    if (!File.Exists(file) && !File.Exists(file + JsonUtil.BackupSuffix))
                    {
                        return OperationResult<T>.Failure(ModErrorCode.NotFound, "Storage key '" + key + "' was not found.");
                    }

                    try
                    {
                        var value = JsonUtil.LoadPersistentFile<T?>(file, null);
                        return value != null
                            ? OperationResult<T>.Success(value)
                            : OperationResult<T>.Failure(ModErrorCode.Io, "The stored value was null.");
                    }
                    catch (Exception exception)
                    {
                        return OperationResult<T>.Failure(ModErrorCode.Io, exception.Message);
                    }
                }
            }

            public OperationResult<bool> Save<T>(string key, T value) where T : class
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                var file = Resolve(key);
                lock (sync)
                {
                    try
                    {
                        JsonUtil.SaveFile(file, value);
                        return OperationResult<bool>.Success(true);
                    }
                    catch (Exception exception)
                    {
                        return OperationResult<bool>.Failure(ModErrorCode.Io, exception.Message);
                    }
                }
            }

            public OperationResult<bool> Delete(string key)
            {
                var file = Resolve(key);
                lock (sync)
                {
                    try
                    {
                        if (File.Exists(file)) File.Delete(file);
                        if (File.Exists(file + JsonUtil.BackupSuffix)) File.Delete(file + JsonUtil.BackupSuffix);
                        return OperationResult<bool>.Success(true);
                    }
                    catch (Exception exception)
                    {
                        return OperationResult<bool>.Failure(ModErrorCode.Io, exception.Message);
                    }
                }
            }

            public bool TryGetStoryFlag(string key, out bool value)
            {
                var result = Load<StoryFlagValue>(StoryFlagPrefix + ValidateStoryFlagKey(key));
                if (result.TryGetValue(out var stored))
                {
                    value = stored.Value;
                    return true;
                }

                value = false;
                return false;
            }

            public OperationResult<bool> SetStoryFlag(string key, bool value) =>
                Save(StoryFlagPrefix + ValidateStoryFlagKey(key), new StoryFlagValue { Value = value });

            public OperationResult<bool> DeleteStoryFlag(string key) =>
                Delete(StoryFlagPrefix + ValidateStoryFlagKey(key));

            private static string ValidateStoryFlagKey(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    throw new ArgumentException("A story flag key is required.", nameof(key));
                }

                return key;
            }

            [DataContract]
            private sealed class StoryFlagValue
            {
                [DataMember(Name = "value")]
                public bool Value { get; set; }
            }

            private string Resolve(string key)
            {
                if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A storage key is required.", nameof(key));
                try
                {
                    return PathSafety.CombineRelativeChild(root, key + ".json");
                }
                catch (InvalidOperationException exception)
                {
                    throw new ArgumentException("Storage keys cannot be absolute or traverse directories.", nameof(key), exception);
                }
            }
        }

        private sealed class ModFiles : IModFiles
        {
            private const int MaximumBytes = 16 * 1024 * 1024;
            private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
            private readonly string packageRoot;
            private readonly string dataRoot;
            private readonly IModLifetime lifetime;

            public ModFiles(string packageRoot, string dataRoot, IModLifetime lifetime)
            {
                this.packageRoot = packageRoot;
                this.dataRoot = dataRoot;
                this.lifetime = lifetime;
            }

            public bool PackageFileExists(string relativePath) => TryResolve(packageRoot, relativePath, out var path) && File.Exists(path);
            public bool DataFileExists(string relativePath) => TryResolve(dataRoot, relativePath, out var path) && File.Exists(path);

            public Task<OperationResult<byte[]>> ReadPackageBytesAsync(string relativePath, CancellationToken cancellationToken = default)
                => ReadBytesAsync(packageRoot, relativePath, cancellationToken);

            public Task<OperationResult<byte[]>> ReadDataBytesAsync(string relativePath, CancellationToken cancellationToken = default)
                => ReadBytesAsync(dataRoot, relativePath, cancellationToken);

            public async Task<OperationResult<string>> ReadPackageTextAsync(string relativePath, CancellationToken cancellationToken = default)
                => Decode(await ReadPackageBytesAsync(relativePath, cancellationToken).ConfigureAwait(false));

            public async Task<OperationResult<string>> ReadDataTextAsync(string relativePath, CancellationToken cancellationToken = default)
                => Decode(await ReadDataBytesAsync(relativePath, cancellationToken).ConfigureAwait(false));

            public Task<OperationResult<bool>> WriteDataBytesAsync(
                string relativePath,
                byte[] content,
                CancellationToken cancellationToken = default)
            {
                if (content == null) throw new ArgumentNullException(nameof(content));
                if (content.Length > MaximumBytes)
                {
                    return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "File content exceeds 16 MiB."));
                }

                var copy = (byte[])content.Clone();
                return RunAsync(dataRoot, relativePath, cancellationToken, (path, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    AtomicWrite(path, copy);
                    return OperationResult<bool>.Success(true);
                });
            }

            public Task<OperationResult<bool>> WriteDataTextAsync(
                string relativePath,
                string content,
                CancellationToken cancellationToken = default)
            {
                if (content == null) throw new ArgumentNullException(nameof(content));
                return WriteDataBytesAsync(relativePath, StrictUtf8.GetBytes(content), cancellationToken);
            }

            public Task<OperationResult<bool>> DeleteDataFileAsync(string relativePath, CancellationToken cancellationToken = default)
            {
                return RunAsync(dataRoot, relativePath, cancellationToken, (path, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(path)) File.Delete(path);
                    return OperationResult<bool>.Success(true);
                });
            }

            private Task<OperationResult<byte[]>> ReadBytesAsync(string root, string relativePath, CancellationToken cancellationToken)
            {
                return RunAsync(root, relativePath, cancellationToken, (path, token) =>
                {
                    if (!File.Exists(path))
                    {
                        return OperationResult<byte[]>.Failure(ModErrorCode.NotFound, "File '" + relativePath + "' was not found.");
                    }

                    token.ThrowIfCancellationRequested();
                    var info = new FileInfo(path);
                    if (info.Length > MaximumBytes)
                    {
                        return OperationResult<byte[]>.Failure(ModErrorCode.Io, "File exceeds the 16 MiB SDK limit.");
                    }

                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        return OperationResult<byte[]>.Failure(
                            ModErrorCode.Io,
                            "The requested file must be a regular file and cannot be a symbolic link.");
                    }

                    using (var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        81920,
                        FileOptions.SequentialScan))
                    {
                        if (stream.Length > MaximumBytes)
                        {
                            return OperationResult<byte[]>.Failure(ModErrorCode.Io, "File exceeds the 16 MiB SDK limit.");
                        }

                        var expectedLength = checked((int)stream.Length);
                        var bytes = new byte[expectedLength];
                        var total = 0;
                        while (total < bytes.Length)
                        {
                            token.ThrowIfCancellationRequested();
                            var read = stream.Read(bytes, total, bytes.Length - total);
                            if (read == 0) break;
                            total += read;
                        }

                        token.ThrowIfCancellationRequested();
                        if (stream.ReadByte() >= 0)
                        {
                            return OperationResult<byte[]>.Failure(ModErrorCode.Io, "File grew while it was being read.");
                        }

                        if (total != bytes.Length)
                        {
                            Array.Resize(ref bytes, total);
                        }

                        return OperationResult<byte[]>.Success(bytes);
                    }
                });
            }

            private async Task<OperationResult<T>> RunAsync<T>(
                string root,
                string relativePath,
                CancellationToken callerToken,
                Func<string, CancellationToken, OperationResult<T>> operation) where T : notnull
            {
                if (!TryResolve(root, relativePath, out var path))
                {
                    return OperationResult<T>.Failure(ModErrorCode.InvalidArgument, "The path must be a safe relative child path.");
                }

                var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.StoppingToken, callerToken);
                IDisposable tracking;
                try
                {
                    tracking = lifetime.Track(linked);
                }
                catch (ObjectDisposedException)
                {
                    linked.Dispose();
                    return OperationResult<T>.Failure(ModErrorCode.Cancelled, "The mod is stopping.");
                }

                try
                {
                    return await Task.Run(() => operation(path, linked.Token), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return OperationResult<T>.Failure(ModErrorCode.Cancelled, "The file operation was cancelled.");
                }
                catch (Exception exception)
                {
                    return OperationResult<T>.Failure(ModErrorCode.Io, exception.Message);
                }
                finally
                {
                    tracking.Dispose();
                }
            }

            private static OperationResult<string> Decode(OperationResult<byte[]> bytes)
            {
                if (!bytes.TryGetValue(out var value))
                {
                    return OperationResult<string>.Failure(bytes.ErrorCode, bytes.ErrorMessage);
                }

                try
                {
                    return OperationResult<string>.Success(StrictUtf8.GetString(value));
                }
                catch (DecoderFallbackException exception)
                {
                    return OperationResult<string>.Failure(ModErrorCode.Io, "File is not strict UTF-8: " + exception.Message);
                }
            }

            private static bool TryResolve(string root, string relativePath, out string path)
            {
                try
                {
                    path = PathSafety.CombineRelativeChild(root, relativePath);
                    return !string.IsNullOrWhiteSpace(relativePath);
                }
                catch
                {
                    path = string.Empty;
                    return false;
                }
            }

            private static void AtomicWrite(string path, byte[] content)
            {
                var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Data file has no parent directory.");
                Directory.CreateDirectory(directory);
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(content, 0, content.Length);
                        stream.Flush(true);
                    }

                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        private sealed class ModEvents : IModEvents
        {
            private readonly object sync = new object();
            private readonly List<Action<float>> updates = new List<Action<float>>();
            private readonly List<Action<GameTimeSample>> fixedUpdates = new List<Action<GameTimeSample>>();
            private readonly List<Action<GameTimeSample>> lateUpdates = new List<Action<GameTimeSample>>();
            private readonly List<Action<string>> scenes = new List<Action<string>>();
            private readonly IModLifetime lifetime;
            private readonly IModLogger logger;

            public ModEvents(IModLifetime lifetime, IModLogger logger)
            {
                this.lifetime = lifetime;
                this.logger = logger;
            }

            public IDisposable SubscribeUpdate(Action<float> handler) => Subscribe(updates, handler);
            public IDisposable SubscribeFixedUpdate(Action<GameTimeSample> handler) => Subscribe(fixedUpdates, handler);
            public IDisposable SubscribeLateUpdate(Action<GameTimeSample> handler) => Subscribe(lateUpdates, handler);
            public IDisposable SubscribeSceneLoaded(Action<string> handler) => Subscribe(scenes, handler);

            public void RaiseUpdate(float value) => Raise(updates, value, "Update");
            public void RaiseFixedUpdate(GameTimeSample value) => Raise(fixedUpdates, value, "FixedUpdate");
            public void RaiseLateUpdate(GameTimeSample value) => Raise(lateUpdates, value, "LateUpdate");
            public void RaiseSceneLoaded(string value) => Raise(scenes, value, "SceneLoaded");

            private IDisposable Subscribe<T>(List<Action<T>> handlers, Action<T> handler)
            {
                if (handler == null) throw new ArgumentNullException(nameof(handler));
                lock (sync) handlers.Add(handler);
                return lifetime.Track(new EventSubscription(() =>
                {
                    lock (sync) handlers.Remove(handler);
                }));
            }

            private void Raise<T>(List<Action<T>> handlers, T value, string phase)
            {
                Action<T>[] snapshot;
                lock (sync) snapshot = handlers.ToArray();
                foreach (var handler in snapshot)
                {
                    try { handler(value); }
                    catch (Exception exception) { logger.Error(exception, "A mod event subscriber failed during " + phase + "."); }
                }
            }

            private sealed class EventSubscription : IDisposable
            {
                private Action? unsubscribe;
                public EventSubscription(Action unsubscribe) => this.unsubscribe = unsubscribe;
                public void Dispose() => Interlocked.Exchange(ref unsubscribe, null)?.Invoke();
            }
        }
    }
}
