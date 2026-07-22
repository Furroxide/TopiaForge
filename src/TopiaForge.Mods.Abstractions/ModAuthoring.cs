using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Describes the package identity of the currently executing mod.</summary>
    public sealed class ModIdentity
    {
        /// <summary>Creates a mod identity.</summary>
        /// <param name="id">The stable, globally unique manifest identifier.</param>
        /// <param name="name">The user-facing display name.</param>
        /// <param name="version">The complete package semantic version.</param>
        public ModIdentity(string id, string name, SemanticVersion version)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A mod id is required.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A mod name is required.", nameof(name));
            }

            Id = id;
            Name = name;
            Version = version;
        }

        /// <summary>Gets the stable, globally unique manifest identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the user-facing display name.</summary>
        public string Name { get; }

        /// <summary>Gets the complete package semantic version.</summary>
        public SemanticVersion Version { get; }
    }

    /// <summary>Describes the TopiaForge and game runtime hosting the current mod.</summary>
    public interface IRuntimeInfo
    {
        /// <summary>Gets the TopiaForge loader version.</summary>
        SemanticVersion LoaderVersion { get; }

        /// <summary>Gets the TopiaForge SDK contract version.</summary>
        SemanticVersion SdkVersion { get; }

        /// <summary>Tries to get the detected Robotopia build version.</summary>
        bool TryGetGameVersion(out SemanticVersion version);

        /// <summary>Gets the normalized operating-system name, such as <c>windows</c>, <c>macos</c>, or <c>linux</c>.</summary>
        string Platform { get; }

        /// <summary>Gets the normalized process architecture, such as <c>x64</c> or <c>arm64</c>.</summary>
        string Architecture { get; }

        /// <summary>Gets the normalized platform and architecture pair, such as <c>windows-x64</c>.</summary>
        string RuntimeIdentifier { get; }

        /// <summary>Gets loaded package/provider versions keyed by stable manifest id, including third-party extension providers.</summary>
        IReadOnlyDictionary<string, SemanticVersion> ProviderVersions { get; }

        /// <summary>Gets unavailable capability ids and plain-language reasons.</summary>
        IReadOnlyDictionary<string, string> UnavailableCapabilities { get; }

        /// <summary>Tries to explain why a capability is unavailable in this host.</summary>
        bool TryGetUnavailableCapability(string capability, out string? reason);
    }

    /// <summary>
    /// Owns resources acquired by a mod. The runtime cancels and disposes the lifetime after every load attempt,
    /// including partial failures, and releases tracked resources in reverse registration order.
    /// </summary>
    public interface IModLifetime : IDisposable
    {
        /// <summary>Gets a token cancelled as soon as mod shutdown begins.</summary>
        CancellationToken StoppingToken { get; }

        /// <summary>Gets whether shutdown has begun.</summary>
        bool IsStopping { get; }

        /// <summary>
        /// Tracks a disposable resource and returns a lease that can release it early. Disposing the lease removes
        /// the resource from lifetime cleanup and disposes the resource exactly once.
        /// </summary>
        /// <param name="resource">The resource to own.</param>
        /// <returns>An idempotent lease for early release.</returns>
        IDisposable Track(IDisposable resource);

        /// <summary>Registers an action to run during reverse-order lifetime cleanup.</summary>
        /// <param name="cleanup">The cleanup action.</param>
        /// <returns>An idempotent lease that can run and remove the action early.</returns>
        IDisposable Defer(Action cleanup);
    }

    /// <summary>Provides owner-scoped subscriptions to runtime events.</summary>
    public interface IModEvents
    {
        /// <summary>Subscribes to the game-frame update event.</summary>
        /// <param name="handler">A callback receiving elapsed frame time in seconds.</param>
        /// <returns>A lifetime-tracked subscription that may be disposed early.</returns>
        IDisposable SubscribeUpdate(Action<float> handler);

        /// <summary>Subscribes to fixed-rate physics updates.</summary>
        /// <param name="handler">A callback receiving the current fixed-loop timing sample.</param>
        /// <returns>A lifetime-tracked subscription that may be disposed early.</returns>
        IDisposable SubscribeFixedUpdate(Action<GameTimeSample> handler);

        /// <summary>Subscribes after ordinary rendered-frame updates and camera movement.</summary>
        /// <param name="handler">A callback receiving the current late-loop timing sample.</param>
        /// <returns>A lifetime-tracked subscription that may be disposed early.</returns>
        IDisposable SubscribeLateUpdate(Action<GameTimeSample> handler);

        /// <summary>Subscribes to successful scene-load notifications.</summary>
        /// <param name="handler">A callback receiving the loaded scene name.</param>
        /// <returns>A lifetime-tracked subscription that may be disposed early.</returns>
        IDisposable SubscribeSceneLoaded(Action<string> handler);
    }

    /// <summary>
    /// Optional detailed scene-load event source implemented by hosts that can report load mode and active-scene
    /// state. Use <see cref="ModEventExtensions.SubscribeSceneLoaded(IModEvents, Action{SceneLoadEvent})"/> rather
    /// than casting to this interface so mods remain compatible with older hosts.
    /// </summary>
    public interface ISceneLoadEventSource
    {
        /// <summary>Subscribes to successful scene load/activation notifications with transition metadata.</summary>
        /// <param name="handler">A callback receiving the scene-load notification.</param>
        /// <returns>A lifetime-tracked subscription that may be disposed early.</returns>
        IDisposable SubscribeSceneLoaded(Action<SceneLoadEvent> handler);
    }

    /// <summary>
    /// Optional complete scene lifecycle source implemented by hosts that can distinguish scene instances and report
    /// normalized load, activation, and unload phases. Subscribe through
    /// <see cref="ModEventExtensions.SubscribeSceneLifecycle"/> to retain a load-only fallback on simpler hosts.
    /// </summary>
    public interface ISceneLifecycleEventSource
    {
        /// <summary>Subscribes to normalized scene-instance lifecycle notifications.</summary>
        /// <param name="handler">A callback receiving the lifecycle transition.</param>
        /// <returns>A lifetime-tracked subscription that may be disposed early.</returns>
        IDisposable SubscribeSceneLifecycle(Action<SceneLifecycleEvent> handler);
    }

    /// <summary>Compatibility-preserving additions to runtime mod events.</summary>
    public static class ModEventExtensions
    {
        /// <summary>
        /// Subscribes to detailed scene transition notifications. Older event hosts fall back to a world-replacing single
        /// load, matching the only scene-load semantics their string-only contract could represent.
        /// </summary>
        /// <param name="events">The owner-scoped mod event source.</param>
        /// <param name="handler">A callback receiving the scene-load notification.</param>
        /// <returns>A lifetime-tracked subscription that may be disposed early.</returns>
        public static IDisposable SubscribeSceneLoaded(this IModEvents events, Action<SceneLoadEvent> handler)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            if (events is ISceneLoadEventSource detailed)
            {
                return detailed.SubscribeSceneLoaded(handler);
            }

            return events.SubscribeSceneLoaded(sceneName =>
                handler(new SceneLoadEvent(sceneName, SceneLoadMode.Single, true)));
        }

        /// <summary>
        /// Subscribes to normalized scene lifecycle notifications. Hosts implementing
        /// <see cref="ISceneLifecycleEventSource"/> report loaded, activated, and unloaded phases with a process-local
        /// instance id. Simpler hosts preserve compatibility by reporting load-only events with instance id zero.
        /// </summary>
        /// <param name="events">The owner-scoped mod event source.</param>
        /// <param name="handler">A callback receiving the lifecycle transition.</param>
        /// <returns>A lifetime-tracked subscription that may be disposed early.</returns>
        public static IDisposable SubscribeSceneLifecycle(
            this IModEvents events,
            Action<SceneLifecycleEvent> handler)
        {
            if (events == null) throw new ArgumentNullException(nameof(events));
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            if (events is ISceneLifecycleEventSource lifecycle)
            {
                return lifecycle.SubscribeSceneLifecycle(handler);
            }

            return events.SubscribeSceneLoaded((SceneLoadEvent scene) => handler(new SceneLifecycleEvent(
                0,
                scene.SceneName,
                SceneLifecyclePhase.Loaded,
                scene.Mode,
                scene.IsActive)));
        }
    }

    /// <summary>Defines defaults, schema version, validation, and migration for one typed config document.</summary>
    /// <typeparam name="T">The serializable configuration type.</typeparam>
    public sealed class ConfigDefinition<T> where T : class
    {
        /// <summary>Creates a typed configuration definition.</summary>
        public ConfigDefinition(
            int schemaVersion,
            Func<T> createDefault,
            Func<T, OperationResult<bool>>? validate = null,
            Func<int, T, OperationResult<T>>? migrate = null)
        {
            if (schemaVersion < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(schemaVersion));
            }

            SchemaVersion = schemaVersion;
            CreateDefault = createDefault ?? throw new ArgumentNullException(nameof(createDefault));
            Validate = validate ?? (_ => OperationResult<bool>.Success(true));
            Migrate = migrate;
        }

        /// <summary>Gets the current positive schema version.</summary>
        public int SchemaVersion { get; }

        /// <summary>Gets the factory used when no document exists.</summary>
        public Func<T> CreateDefault { get; }

        /// <summary>
        /// Gets the validator run after defaults, load, migration, and before save. Return success with
        /// <see langword="true"/> for a valid value or a stable failure for invalid input.
        /// </summary>
        public Func<T, OperationResult<bool>> Validate { get; }

        /// <summary>
        /// Gets the optional migrator receiving the stored schema version and value and returning the migrated value
        /// or a stable failure.
        /// </summary>
        public Func<int, T, OperationResult<T>>? Migrate { get; }
    }

    /// <summary>Loads and saves the current mod's validated, versioned typed configuration document.</summary>
    public interface IModConfigService
    {
        /// <summary>Loads, validates, and when necessary migrates configuration.</summary>
        /// <typeparam name="T">The serializable configuration type.</typeparam>
        /// <param name="definition">The current schema contract.</param>
        /// <returns>The valid current value or a stable failure.</returns>
        OperationResult<T> Load<T>(ConfigDefinition<T> definition) where T : class;

        /// <summary>Validates and atomically saves configuration.</summary>
        /// <typeparam name="T">The serializable configuration type.</typeparam>
        /// <param name="definition">The current schema contract.</param>
        /// <param name="value">The value to validate and save.</param>
        /// <returns>Success or a stable validation/persistence failure.</returns>
        OperationResult<bool> Save<T>(ConfigDefinition<T> definition, T value) where T : class;

        /// <summary>Replaces configuration with validated defaults.</summary>
        /// <typeparam name="T">The serializable configuration type.</typeparam>
        /// <param name="definition">The current schema contract.</param>
        /// <returns>The saved default value or a stable failure.</returns>
        OperationResult<T> Reset<T>(ConfigDefinition<T> definition) where T : class;
    }

    /// <summary>Reads package files and manages persistent data without revealing filesystem paths.</summary>
    public interface IModFiles
    {
        /// <summary>Gets whether a package-relative file exists.</summary>
        bool PackageFileExists(string relativePath);

        /// <summary>Gets whether a persistent data file exists.</summary>
        bool DataFileExists(string relativePath);

        /// <summary>Reads a bounded package file as bytes.</summary>
        Task<OperationResult<byte[]>> ReadPackageBytesAsync(
            string relativePath,
            CancellationToken cancellationToken = default);

        /// <summary>Reads a bounded persistent data file as bytes.</summary>
        Task<OperationResult<byte[]>> ReadDataBytesAsync(
            string relativePath,
            CancellationToken cancellationToken = default);

        /// <summary>Reads a strict UTF-8 package file.</summary>
        Task<OperationResult<string>> ReadPackageTextAsync(
            string relativePath,
            CancellationToken cancellationToken = default);

        /// <summary>Reads a strict UTF-8 persistent data file.</summary>
        Task<OperationResult<string>> ReadDataTextAsync(
            string relativePath,
            CancellationToken cancellationToken = default);

        /// <summary>Atomically writes a bounded persistent data file.</summary>
        Task<OperationResult<bool>> WriteDataBytesAsync(
            string relativePath,
            byte[] content,
            CancellationToken cancellationToken = default);

        /// <summary>Atomically writes strict UTF-8 text to persistent data.</summary>
        Task<OperationResult<bool>> WriteDataTextAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default);

        /// <summary>Deletes a persistent data file when it exists.</summary>
        Task<OperationResult<bool>> DeleteDataFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Provides installation-local typed JSON key-value persistence scoped to the current mod. Values are not
    /// save-scoped, synchronized, or replicated between processes.
    /// </summary>
    public interface ILocalModStorageService
    {
        /// <summary>Gets whether a key currently exists.</summary>
        bool Contains(string key);

        /// <summary>Loads a typed value.</summary>
        OperationResult<T> Load<T>(string key) where T : class;

        /// <summary>Atomically saves a typed value.</summary>
        OperationResult<bool> Save<T>(string key, T value) where T : class;

        /// <summary>Deletes a key when it exists.</summary>
        OperationResult<bool> Delete(string key);
    }
}
