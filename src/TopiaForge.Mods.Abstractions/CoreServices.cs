using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Controls how a scene load affects scenes that are already loaded.</summary>
    public enum SceneLoadMode
    {
        /// <summary>Replaces the currently loaded scenes.</summary>
        Single = 0,

        /// <summary>Adds the requested scene without unloading existing scenes.</summary>
        Additive = 1
    }

    /// <summary>Identifies the normalized phase of a scene instance's runtime lifecycle.</summary>
    public enum SceneLifecyclePhase
    {
        /// <summary>The scene finished loading and can be queried by mods.</summary>
        Loaded = 0,

        /// <summary>The scene became Unity's active scene.</summary>
        Activated = 1,

        /// <summary>The scene was removed from the set of loaded scenes.</summary>
        Unloaded = 2
    }

    /// <summary>
    /// Describes one normalized scene-instance lifecycle transition without exposing a native scene object.
    /// The instance id is process-local and exists only to correlate duplicate scene names; it must not be persisted.
    /// </summary>
    public sealed class SceneLifecycleEvent
    {
        /// <summary>Creates a scene lifecycle notification.</summary>
        /// <param name="sceneInstanceId">
        /// Process-local scene identity, or zero when an older/fake host cannot provide one.
        /// </param>
        /// <param name="sceneName">Name of the scene instance.</param>
        /// <param name="phase">The normalized lifecycle phase.</param>
        /// <param name="mode">
        /// How a native scene was loaded. Initial snapshots use normalized active/background modes because their
        /// original load history is unavailable.
        /// </param>
        /// <param name="isActive">Whether this scene is active after the transition.</param>
        /// <param name="isInitial">Whether this is the runtime's startup replay of an already loaded scene.</param>
        public SceneLifecycleEvent(
            int sceneInstanceId,
            string sceneName,
            SceneLifecyclePhase phase,
            SceneLoadMode mode,
            bool isActive,
            bool isInitial = false)
        {
            if (sceneInstanceId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sceneInstanceId));
            }

            if (string.IsNullOrWhiteSpace(sceneName))
            {
                throw new ArgumentException("A scene name is required.", nameof(sceneName));
            }

            if (!Enum.IsDefined(typeof(SceneLifecyclePhase), phase))
            {
                throw new ArgumentOutOfRangeException(nameof(phase));
            }

            if (!Enum.IsDefined(typeof(SceneLoadMode), mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (phase == SceneLifecyclePhase.Unloaded && isActive)
            {
                throw new ArgumentException("An unloaded scene cannot remain active.", nameof(isActive));
            }

            if (phase == SceneLifecyclePhase.Activated && !isActive)
            {
                throw new ArgumentException("An activated scene must be active.", nameof(isActive));
            }

            SceneInstanceId = sceneInstanceId;
            SceneName = sceneName;
            Phase = phase;
            Mode = mode;
            IsActive = isActive;
            IsInitial = isInitial;
        }

        /// <summary>
        /// Gets the process-local scene identity. Zero means the host cannot distinguish equal scene names.
        /// </summary>
        public int SceneInstanceId { get; }

        /// <summary>Gets the scene name.</summary>
        public string SceneName { get; }

        /// <summary>Gets the normalized lifecycle phase.</summary>
        public SceneLifecyclePhase Phase { get; }

        /// <summary>
        /// Gets the native load mode, or the normalized active/background mode for an initial snapshot.
        /// </summary>
        public SceneLoadMode Mode { get; }

        /// <summary>Gets whether the scene is active after this transition.</summary>
        public bool IsActive { get; }

        /// <summary>Gets whether this notification was synthesized from the runtime's initial loaded-scene snapshot.</summary>
        public bool IsInitial { get; }
    }

    /// <summary>
    /// Describes a successful scene load or later active-scene transition without exposing engine scene objects.
    /// Detailed subscribers may see the same additively loaded scene again when it later becomes active; legacy
    /// string-only subscribers retain one notification per load.
    /// </summary>
    public sealed class SceneLoadEvent
    {
        /// <summary>Creates a scene transition notification.</summary>
        /// <param name="sceneName">Name of the scene that finished loading or became active.</param>
        /// <param name="mode">How the load affected scenes that were already loaded.</param>
        /// <param name="isActive">Whether the loaded scene is the active scene when the notification is published.</param>
        public SceneLoadEvent(string sceneName, SceneLoadMode mode, bool isActive)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                throw new ArgumentException("A scene name is required.", nameof(sceneName));
            }

            if (!Enum.IsDefined(typeof(SceneLoadMode), mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            SceneName = sceneName;
            Mode = mode;
            IsActive = isActive;
        }

        /// <summary>Gets the loaded or activated scene name.</summary>
        public string SceneName { get; }

        /// <summary>Gets how the load affected scenes that were already loaded.</summary>
        public SceneLoadMode Mode { get; }

        /// <summary>Gets whether the loaded scene is now the active scene.</summary>
        public bool IsActive { get; }

        /// <summary>
        /// Gets whether the notification represents an authoritative world replacement. Single loads always do;
        /// an activated additive gameplay scene does, while temporary menu/boot/loader overlays do not.
        /// </summary>
        public bool IsAuthoritativeReplacement => Mode == SceneLoadMode.Single
            || (IsActive && !GameScenes.IsNonGameplayScene(SceneName));
    }

    /// <summary>Describes one loaded or loadable scene without exposing an engine scene handle.</summary>
    public sealed class SceneSnapshot
    {
        /// <summary>Creates a scene snapshot.</summary>
        public SceneSnapshot(string name, bool isLoaded, bool isActive)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A scene name is required.", nameof(name));
            }

            Name = name;
            IsLoaded = isLoaded;
            IsActive = isActive;
        }

        /// <summary>Gets the engine-independent scene name.</summary>
        public string Name { get; }

        /// <summary>Gets whether scene content has finished loading.</summary>
        public bool IsLoaded { get; }

        /// <summary>Gets whether this is the active scene.</summary>
        public bool IsActive { get; }
    }

    /// <summary>Describes an asynchronous scene load.</summary>
    public sealed class SceneLoadRequest
    {
        /// <summary>Creates a scene-load request.</summary>
        public SceneLoadRequest(string sceneName, SceneLoadMode mode = SceneLoadMode.Single)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                throw new ArgumentException("A scene name is required.", nameof(sceneName));
            }

            if (!Enum.IsDefined(typeof(SceneLoadMode), mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            SceneName = sceneName;
            Mode = mode;
        }

        /// <summary>Gets the scene name.</summary>
        public string SceneName { get; }

        /// <summary>Gets how existing scenes are treated.</summary>
        public SceneLoadMode Mode { get; }
    }

    /// <summary>Describes the story checkpoint currently selected by the game.</summary>
    public sealed class CheckpointSnapshot
    {
        /// <summary>Creates a checkpoint snapshot.</summary>
        /// <param name="id">Stable checkpoint identifier supplied by the game.</param>
        /// <param name="sceneName">Scene containing the checkpoint.</param>
        /// <param name="position">World-space checkpoint position when the game exposes one.</param>
        public CheckpointSnapshot(string id, string sceneName, Vec3 position)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A checkpoint id is required.", nameof(id));
            }

            Id = id;
            SceneName = sceneName ?? string.Empty;
            Position = position;
        }

        /// <summary>Gets the stable game checkpoint identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the scene containing the checkpoint, or an empty string when unavailable.</summary>
        public string SceneName { get; }

        /// <summary>Gets the world-space checkpoint position, or <see cref="Vec3.Zero"/> when unavailable.</summary>
        public Vec3 Position { get; }
    }

    /// <summary>Provides typed scene state and serialized, main-thread scene loading.</summary>
    public interface ISceneService
    {
        /// <summary>Tries to read the active scene.</summary>
        bool TryGetActive(out SceneSnapshot? scene);

        /// <summary>Returns a snapshot of every currently loaded scene.</summary>
        IReadOnlyList<SceneSnapshot> GetLoadedScenes();

        /// <summary>Tries to read the game's current story checkpoint.</summary>
        bool TryGetCheckpoint(out CheckpointSnapshot? checkpoint);

        /// <summary>
        /// Subscribes to current-checkpoint changes. The registration is automatically released with the mod
        /// lifetime and remains disposable for early release.
        /// </summary>
        IDisposable SubscribeCheckpointChanged(Action<CheckpointSnapshot> handler);

        /// <summary>
        /// Loads a scene on the main thread. Lifetime shutdown is combined with caller cancellation. Engines
        /// that cannot cancel an in-flight native load still suppress its result after cancellation.
        /// </summary>
        Task<OperationResult<SceneSnapshot>> LoadAsync(
            SceneLoadRequest request,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Describes an interactable attached to an existing entity.</summary>
    public sealed class InteractableDefinition
    {
        /// <summary>Creates an interactable definition.</summary>
        public InteractableDefinition(string prompt, float maximumDistance = 3f)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ArgumentException("An interaction prompt is required.", nameof(prompt));
            }

            if (maximumDistance <= 0f || float.IsNaN(maximumDistance) || float.IsInfinity(maximumDistance))
            {
                throw new ArgumentOutOfRangeException(nameof(maximumDistance));
            }

            Prompt = prompt;
            MaximumDistance = maximumDistance;
        }

        /// <summary>Gets the localized or display-ready prompt.</summary>
        public string Prompt { get; }

        /// <summary>Gets the maximum interaction distance in world units.</summary>
        public float MaximumDistance { get; }
    }

    /// <summary>Describes a completed interaction.</summary>
    public sealed class InteractionEvent
    {
        /// <summary>Creates an interaction event.</summary>
        public InteractionEvent(IEntity target, PlayerSnapshot player)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Player = player ?? throw new ArgumentNullException(nameof(player));
        }

        /// <summary>Gets the entity that was interacted with.</summary>
        public IEntity Target { get; }

        /// <summary>Gets the player state sampled for the interaction.</summary>
        public PlayerSnapshot Player { get; }
    }

    /// <summary>Represents a lifetime-owned interactable registration.</summary>
    public interface IInteractableRegistration : IDisposable
    {
        /// <summary>Gets the registered entity.</summary>
        IEntity Entity { get; }

        /// <summary>Gets whether the registration remains active.</summary>
        bool IsActive { get; }
    }

    /// <summary>Registers and queries game interactions through opaque entities.</summary>
    public interface IInteractionService
    {
        /// <summary>Registers an interactable and automatically owns it for the current mod lifetime.</summary>
        OperationResult<IInteractableRegistration> Register(
            IEntity entity,
            InteractableDefinition definition,
            Action<InteractionEvent> handler);

        /// <summary>Tries to read the interaction currently targeted by the player.</summary>
        bool TryGetFocused(out IInteractableRegistration? interaction);
    }

    /// <summary>Describes the item currently held by the player.</summary>
    public sealed class HeldItemSnapshot
    {
        /// <summary>Creates a held-item snapshot.</summary>
        public HeldItemSnapshot(string itemId, IEntity entity)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                throw new ArgumentException("An item id is required.", nameof(itemId));
            }

            ItemId = itemId;
            Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        }

        /// <summary>Gets the stable framework item id.</summary>
        public string ItemId { get; }

        /// <summary>Gets the held world entity.</summary>
        public IEntity Entity { get; }
    }

    /// <summary>Describes an item grant to the current player.</summary>
    public sealed class ItemGrantRequest
    {
        /// <summary>Creates an item grant request.</summary>
        public ItemGrantRequest(string itemId, int quantity = 1)
        {
            if (string.IsNullOrWhiteSpace(itemId))
            {
                throw new ArgumentException("An item id is required.", nameof(itemId));
            }

            if (quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity));
            }

            ItemId = itemId;
            Quantity = quantity;
        }

        /// <summary>Gets the stable framework item id.</summary>
        public string ItemId { get; }

        /// <summary>Gets the positive quantity to grant.</summary>
        public int Quantity { get; }
    }

    /// <summary>Provides held-item, give, and drop operations.</summary>
    public interface IItemService
    {
        /// <summary>Tries to read the player's currently held item.</summary>
        bool TryGetHeld(out HeldItemSnapshot? item);

        /// <summary>Grants an item using the current game's inventory adapter.</summary>
        Task<OperationResult<HeldItemSnapshot>> GiveAsync(
            ItemGrantRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>Drops the currently held item, optionally applying an initial velocity.</summary>
        Task<OperationResult<IEntity>> DropHeldAsync(
            Vec3 velocity,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Describes a position, rotation, and scale without exposing an engine transform.</summary>
    public readonly struct TransformState : IEquatable<TransformState>
    {
        /// <summary>Creates transform state.</summary>
        public TransformState(Vec3 position, Quat rotation, Vec3 scale)
        {
            if (!position.IsFinite || !rotation.IsFinite || !scale.IsFinite
                || scale.X == 0f || scale.Y == 0f || scale.Z == 0f)
            {
                throw new ArgumentException("Transform values must be finite and scale components must be non-zero.");
            }

            Position = position;
            Rotation = rotation.Normalized;
            Scale = scale;
        }

        /// <summary>Gets the identity transform.</summary>
        public static TransformState Identity => new TransformState(
            Vec3.Zero,
            Quat.Identity,
            new Vec3(1f, 1f, 1f));

        /// <summary>Gets the world position.</summary>
        public Vec3 Position { get; }

        /// <summary>Gets the world rotation.</summary>
        public Quat Rotation { get; }

        /// <summary>Gets the local scale.</summary>
        public Vec3 Scale { get; }

        /// <summary>Compares two transforms for exact equality.</summary>
        public static bool operator ==(TransformState left, TransformState right) => left.Equals(right);

        /// <summary>Compares two transforms for inequality.</summary>
        public static bool operator !=(TransformState left, TransformState right) => !left.Equals(right);

        /// <inheritdoc/>
        public bool Equals(TransformState other)
        {
            return Position.Equals(other.Position) && Rotation.Equals(other.Rotation) && Scale.Equals(other.Scale);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is TransformState other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Position.GetHashCode();
                hash = (hash * 397) ^ Rotation.GetHashCode();
                return (hash * 397) ^ Scale.GetHashCode();
            }
        }
    }
}
