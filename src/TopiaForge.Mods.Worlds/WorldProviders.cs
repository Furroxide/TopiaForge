using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>How the selected world is integrated into the native scene environment.</summary>
    public enum WorldLoadTransition
    {
        /// <summary>The selected world replaces the active scene.</summary>
        SceneReplacement = 0,
        /// <summary>The selected world adds an arena to an existing scene.</summary>
        AdditiveArena = 1
    }

    /// <summary>How the provider must establish the player's spawn.</summary>
    public enum WorldSpawnKind
    {
        /// <summary>The provider resolves and applies its actual default spawn.</summary>
        ProviderDefault = 0,
        /// <summary>The provider resolves exactly one authored marker and applies it.</summary>
        AuthoredMarker = 1
    }

    /// <summary>Immutable spawn requirement for the selected world.</summary>
    public sealed class WorldSpawnPolicy
    {
        /// <summary>Creates a spawn policy with a marker only when the authored-marker kind requires one.</summary>
        public WorldSpawnPolicy(WorldSpawnKind kind, string? markerName = null)
        {
            if (!Enum.IsDefined(typeof(WorldSpawnKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (kind == WorldSpawnKind.AuthoredMarker ? string.IsNullOrWhiteSpace(markerName) : markerName != null)
                throw new ArgumentException("The spawn marker must match its policy.", nameof(markerName));
            Kind = kind;
            MarkerName = markerName;
        }
        /// <summary>Gets the required spawn kind.</summary>
        public WorldSpawnKind Kind { get; }
        /// <summary>Gets the required authored marker, or null for a provider default.</summary>
        public string? MarkerName { get; }
    }

    /// <summary>Actual scene identity; the opaque process-local integer may be negative.</summary>
    public sealed class WorldSceneIdentity
    {
        /// <summary>Creates the identity of the scene that actually contains the world.</summary>
        public WorldSceneIdentity(int instanceId, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A scene name is required.", nameof(name));
            InstanceId = instanceId;
            Name = name;
        }
        /// <summary>Gets the opaque process-local scene instance identity.</summary>
        public int InstanceId { get; }
        /// <summary>Gets the actual native scene name.</summary>
        public string Name { get; }
    }

    /// <summary>Immutable evidence that scene, content, player and spawn readiness have completed.</summary>
    public sealed class WorldReadiness
    {
        /// <summary>Captures the actual scene and applied spawn after provider readiness.</summary>
        public WorldReadiness(WorldSceneIdentity scene, TransformState spawn)
        {
            Scene = scene ?? throw new ArgumentNullException(nameof(scene));
            Spawn = spawn;
        }
        /// <summary>Gets the actual scene identity.</summary>
        public WorldSceneIdentity Scene { get; }
        /// <summary>Gets the resolved spawn transform applied to the player.</summary>
        public TransformState Spawn { get; }
    }

    /// <summary>Owns loaded world content and cleanup; gameplay receives only its readiness view.</summary>
    public interface IWorldInstance : IDisposable
    {
        /// <summary>Gets actual world readiness after all required preparation has completed.</summary>
        WorldReadiness Readiness { get; }
    }

    /// <summary>Selection and package-scoped services supplied before world activation.</summary>
    public interface IWorldLoadContext
    {
        /// <summary>Gets the unique session identity allocated before activation.</summary>
        string SessionId { get; }
        /// <summary>Gets the selected launch target.</summary>
        string TargetId { get; }
        /// <summary>Gets the concrete selected world.</summary>
        string WorldId { get; }
        /// <summary>Gets the discovered family, or null for static content.</summary>
        string? WorldFamilyId { get; }
        /// <summary>Gets the transition chosen by resolution.</summary>
        WorldLoadTransition Transition { get; }
        /// <summary>Gets the spawn requirement that must be resolved before success.</summary>
        WorldSpawnPolicy SpawnPolicy { get; }
        /// <summary>Gets the world owner's scoped context.</summary>
        IModContext Context { get; }
    }

    /// <summary>Loads one world, including content, player and spawn readiness.</summary>
    public interface IWorldContentProvider
    {
        /// <summary>Returns one owned world only after all readiness requirements are satisfied.</summary>
        Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken cancellationToken);
    }

    /// <summary>Bounded discovery context for one declared family.</summary>
    public interface IWorldDiscoveryContext
    {
        /// <summary>Gets the manifest-declared family prefix.</summary>
        string FamilyId { get; }
        /// <summary>Gets the permitted result count, between one and 4096.</summary>
        int MaximumResults { get; }
        /// <summary>Gets the declaring package's scoped services.</summary>
        IModContext Context { get; }
    }

    /// <summary>Discovers instances of a declared family and loads selected instances through the same provider.</summary>
    public interface IWorldDiscoverySource : IWorldContentProvider
    {
        /// <summary>Returns bounded immutable instance descriptors; discoveries never declare launch targets.</summary>
        Task<OperationResult<IReadOnlyList<DiscoveredWorldDescriptor>>> DiscoverAsync(
            IWorldDiscoveryContext context, CancellationToken cancellationToken);
    }
}
