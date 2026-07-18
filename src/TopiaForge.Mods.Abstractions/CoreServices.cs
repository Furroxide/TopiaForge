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

    /// <summary>Represents a loaded, owner-scoped asset bundle.</summary>
    public interface IAssetBundle : IDisposable
    {
        /// <summary>Gets the bundle's package-relative path.</summary>
        string RelativePath { get; }

        /// <summary>Gets whether the bundle remains usable.</summary>
        bool IsAlive { get; }
    }

    /// <summary>Represents a prefab resolved from a loaded asset bundle.</summary>
    public interface IPrefabAsset : IDisposable
    {
        /// <summary>Gets the asset name inside its bundle.</summary>
        string Name { get; }

        /// <summary>Gets whether the prefab and its bundle remain usable.</summary>
        bool IsAlive { get; }
    }

    /// <summary>Describes a prefab spawn.</summary>
    public sealed class AssetSpawnRequest
    {
        /// <summary>Creates a prefab spawn request.</summary>
        public AssetSpawnRequest(IPrefabAsset prefab, TransformState transform)
        {
            Prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
            Transform = transform;
        }

        /// <summary>Gets the prefab to instantiate.</summary>
        public IPrefabAsset Prefab { get; }

        /// <summary>Gets the initial world transform.</summary>
        public TransformState Transform { get; }
    }

    /// <summary>Represents an entity spawned and owned by the current mod.</summary>
    public interface ISpawnedEntity : IEntity, IDisposable
    {
        /// <summary>Gets the initial transform supplied to the spawn operation.</summary>
        TransformState InitialTransform { get; }
    }

    /// <summary>Loads package assets and spawns opaque, lifetime-owned entities.</summary>
    public interface IAssetService
    {
        /// <summary>Loads an asset bundle from a safe package-relative path.</summary>
        Task<OperationResult<IAssetBundle>> LoadBundleAsync(
            string relativePath,
            CancellationToken cancellationToken = default);

        /// <summary>Loads a prefab from a bundle created by this context.</summary>
        Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(
            IAssetBundle bundle,
            string assetName,
            CancellationToken cancellationToken = default);

        /// <summary>Spawns a prefab and owns the resulting entity for the current mod lifetime.</summary>
        OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request);
    }

    /// <summary>Describes an audio cue playback.</summary>
    public sealed class AudioPlayRequest
    {
        /// <summary>Creates an audio playback request.</summary>
        public AudioPlayRequest(string cueId, float volume = 1f, bool loop = false, Vec3? position = null)
        {
            if (string.IsNullOrWhiteSpace(cueId))
            {
                throw new ArgumentException("An audio cue id is required.", nameof(cueId));
            }

            if (volume < 0f || volume > 1f || float.IsNaN(volume))
            {
                throw new ArgumentOutOfRangeException(nameof(volume));
            }

            CueId = cueId;
            Volume = volume;
            Loop = loop;
            Position = position;
        }

        /// <summary>Gets the framework or provider cue id.</summary>
        public string CueId { get; }

        /// <summary>Gets the normalized playback volume.</summary>
        public float Volume { get; }

        /// <summary>Gets whether playback should loop until released.</summary>
        public bool Loop { get; }

        /// <summary>Gets an optional world position; a missing value requests non-positional playback.</summary>
        public Vec3? Position { get; }
    }

    /// <summary>Represents lifetime-owned audio playback.</summary>
    public interface IAudioPlayback : IDisposable
    {
        /// <summary>Gets whether the cue is still playing.</summary>
        bool IsPlaying { get; }

        /// <summary>Stops playback. Calling this more than once is safe.</summary>
        void Stop();
    }

    /// <summary>Plays framework audio cues without exposing engine audio objects.</summary>
    public interface IAudioService
    {
        /// <summary>Starts a cue and tracks the playback for the current mod lifetime.</summary>
        OperationResult<IAudioPlayback> Play(AudioPlayRequest request);
    }

    /// <summary>Identifies a framework UI surface.</summary>
    public enum UiSurfaceKind
    {
        /// <summary>A quiet gameplay HUD panel.</summary>
        Hud = 0,

        /// <summary>An interactive paper-scheme desktop window.</summary>
        Window = 1
    }

    /// <summary>Identifies a semantic UI tone.</summary>
    public enum UiTone
    {
        /// <summary>Neutral informational content.</summary>
        Neutral = 0,

        /// <summary>A successful action.</summary>
        Success = 1,

        /// <summary>A warning that may require attention.</summary>
        Warning = 2,

        /// <summary>A failed or destructive action.</summary>
        Danger = 3
    }

    /// <summary>Describes a simple TopiaForgeUi HUD panel or window.</summary>
    public sealed class UiSurfaceRequest
    {
        /// <summary>Creates a UI surface request.</summary>
        public UiSurfaceRequest(
            string id,
            string title,
            string body,
            UiSurfaceKind kind = UiSurfaceKind.Window,
            float width = 460f,
            float height = 320f,
            UiNode? content = null)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A stable UI surface id is required.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                throw new ArgumentException("A UI surface title is required.", nameof(title));
            }

            if (!Enum.IsDefined(typeof(UiSurfaceKind), kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            if (width <= 0f || height <= 0f || float.IsNaN(width) || float.IsNaN(height))
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            Id = id;
            Title = title;
            Body = body ?? string.Empty;
            Kind = kind;
            Width = width;
            Height = height;
            UiComposition.Validate(content);
            if (kind == UiSurfaceKind.Hud && UiComposition.ContainsInteractive(content))
            {
                throw new ArgumentException(
                    "HUD surfaces are presentation-only; place interactive controls in a window or modal.",
                    nameof(content));
            }

            Content = content;
        }

        /// <summary>Gets the stable id unique inside the current mod.</summary>
        public string Id { get; }

        /// <summary>Gets the surface title.</summary>
        public string Title { get; }

        /// <summary>Gets the initial body text.</summary>
        public string Body { get; }

        /// <summary>Gets the surface kind.</summary>
        public UiSurfaceKind Kind { get; }

        /// <summary>Gets the requested width in scaled UI units.</summary>
        public float Width { get; }

        /// <summary>Gets the requested height in scaled UI units.</summary>
        public float Height { get; }

        /// <summary>Gets optional immutable interactive composition rendered below the dirty-checked body text.</summary>
        public UiNode? Content { get; }
    }

    /// <summary>Represents a lifetime-owned UI surface.</summary>
    public interface IUiSurface : IDisposable
    {
        /// <summary>Gets the stable surface id.</summary>
        string Id { get; }

        /// <summary>Gets whether the surface is currently visible.</summary>
        bool IsVisible { get; }

        /// <summary>Shows the surface.</summary>
        void Show();

        /// <summary>Hides the surface without releasing it.</summary>
        void Hide();

        /// <summary>Updates body text using the UI kit's dirty-checked setter.</summary>
        void SetBody(string body);

        /// <summary>Atomically replaces the immutable interactive composition below the body text.</summary>
        OperationResult<bool> SetContent(UiNode content);
    }

    /// <summary>Describes a confirmation modal.</summary>
    public sealed class UiModalRequest
    {
        /// <summary>Creates a modal request.</summary>
        public UiModalRequest(
            string title,
            string body,
            string confirmLabel = "CONFIRM",
            string cancelLabel = "CANCEL",
            bool destructive = false)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                throw new ArgumentException("A modal title is required.", nameof(title));
            }

            if (string.IsNullOrWhiteSpace(confirmLabel) || string.IsNullOrWhiteSpace(cancelLabel))
            {
                throw new ArgumentException("Modal action labels are required.");
            }

            Title = title;
            Body = body ?? string.Empty;
            ConfirmLabel = confirmLabel;
            CancelLabel = cancelLabel;
            Destructive = destructive;
        }

        /// <summary>Gets the modal title.</summary>
        public string Title { get; }

        /// <summary>Gets the modal body.</summary>
        public string Body { get; }

        /// <summary>Gets the positive action label.</summary>
        public string ConfirmLabel { get; }

        /// <summary>Gets the cancellation label.</summary>
        public string CancelLabel { get; }

        /// <summary>Gets whether the positive action is destructive.</summary>
        public bool Destructive { get; }
    }

    /// <summary>Represents an open confirmation modal.</summary>
    public interface IUiModal : IDisposable
    {
        /// <summary>Gets whether the modal remains open.</summary>
        bool IsOpen { get; }

        /// <summary>Closes the modal as a cancellation.</summary>
        void Close();
    }

    /// <summary>Immutable accessibility preferences applied to one mod's TopiaForgeUi host.</summary>
    public sealed class UiAccessibilityPreferences
    {
        /// <summary>Creates UI accessibility preferences.</summary>
        /// <param name="highContrast">Whether the host uses its high-contrast semantic palette.</param>
        /// <param name="uiScale">Host-relative UI scale in the inclusive 0.75-to-1.5 range.</param>
        /// <param name="reducedMotion">Whether transitions, pulses, and punches resolve immediately.</param>
        /// <param name="motionIntensity">Host-relative motion intensity in the inclusive zero-to-two range.</param>
        public UiAccessibilityPreferences(
            bool highContrast = false,
            float uiScale = 1f,
            bool reducedMotion = false,
            float motionIntensity = 1f)
        {
            if (uiScale < 0.75f || uiScale > 1.5f || float.IsNaN(uiScale) || float.IsInfinity(uiScale))
            {
                throw new ArgumentOutOfRangeException(nameof(uiScale));
            }

            if (motionIntensity < 0f || motionIntensity > 2f
                || float.IsNaN(motionIntensity) || float.IsInfinity(motionIntensity))
            {
                throw new ArgumentOutOfRangeException(nameof(motionIntensity));
            }

            HighContrast = highContrast;
            UiScale = uiScale;
            ReducedMotion = reducedMotion;
            MotionIntensity = motionIntensity;
        }

        /// <summary>Gets the default host preferences.</summary>
        public static UiAccessibilityPreferences Default { get; } = new UiAccessibilityPreferences();

        /// <summary>Gets whether the host uses its high-contrast semantic palette.</summary>
        public bool HighContrast { get; }

        /// <summary>Gets the host-relative UI scale.</summary>
        public float UiScale { get; }

        /// <summary>Gets whether nonessential motion is disabled.</summary>
        public bool ReducedMotion { get; }

        /// <summary>Gets the host-relative motion intensity.</summary>
        public float MotionIntensity { get; }
    }

    /// <summary>Creates owner-scoped TopiaForgeUi surfaces, modals, and toasts.</summary>
    public interface IUiService
    {
        /// <summary>Gets the current accessibility preferences for this mod's UI host.</summary>
        UiAccessibilityPreferences Accessibility { get; }

        /// <summary>Applies accessibility preferences to existing and future UI owned by this mod.</summary>
        OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences);

        /// <summary>Creates and lifetime-tracks a HUD panel or window.</summary>
        OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request);

        /// <summary>Shows a lifetime-tracked modal and reports whether the user confirmed it.</summary>
        OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed);

        /// <summary>Shows a short TopiaForgeUi toast.</summary>
        OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral);
    }

    /// <summary>Contains one immutable localization catalog.</summary>
    public sealed class LocalizationCatalog
    {
        private readonly ReadOnlyDictionary<string, string> entries;

        /// <summary>Creates a localization catalog.</summary>
        public LocalizationCatalog(string locale, IReadOnlyDictionary<string, string> entries)
        {
            if (string.IsNullOrWhiteSpace(locale))
            {
                throw new ArgumentException("A locale is required.", nameof(locale));
            }

            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    throw new ArgumentException("Localization keys cannot be empty.", nameof(entries));
                }

                copy.Add(entry.Key, entry.Value ?? string.Empty);
            }

            Locale = locale;
            this.entries = new ReadOnlyDictionary<string, string>(copy);
        }

        /// <summary>Gets the BCP 47-style locale name.</summary>
        public string Locale { get; }

        /// <summary>Gets the immutable key-to-text mapping.</summary>
        public IReadOnlyDictionary<string, string> Entries => entries;
    }

    /// <summary>Represents a lifetime-owned localization catalog.</summary>
    public interface ILocalizationRegistration : IDisposable
    {
        /// <summary>Gets the registered locale.</summary>
        string Locale { get; }
    }

    /// <summary>Provides owner-scoped localization with deterministic fallback.</summary>
    public interface ILocalizationService
    {
        /// <summary>Gets the current UI locale.</summary>
        string CurrentLocale { get; }

        /// <summary>Registers and lifetime-tracks a localization catalog.</summary>
        /// <returns>The registration, or a stable cancellation result when the mod is stopping.</returns>
        OperationResult<ILocalizationRegistration> Register(LocalizationCatalog catalog);

        /// <summary>Tries to resolve a key for the current locale and its language fallback.</summary>
        bool TryGet(string key, out string? text);

        /// <summary>Resolves a key or returns the supplied display-ready fallback.</summary>
        string Get(string key, string fallback);
    }

    /// <summary>Describes a console or developer command.</summary>
    public sealed class CommandDefinition
    {
        /// <summary>Creates a command definition.</summary>
        public CommandDefinition(string name, string description, string usage = "")
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A command name is required.", nameof(name));
            }

            if (name.IndexOfAny(new[] { ' ', '\t', '\r', '\n', ':' }) >= 0)
            {
                throw new ArgumentException("A command name cannot contain whitespace or a colon.", nameof(name));
            }

            Name = name;
            Description = description ?? string.Empty;
            Usage = usage ?? string.Empty;
        }

        /// <summary>Gets the short name unique inside the current mod.</summary>
        public string Name { get; }

        /// <summary>Gets the user-facing description.</summary>
        public string Description { get; }

        /// <summary>Gets optional argument usage text.</summary>
        public string Usage { get; }
    }

    /// <summary>Contains one immutable command invocation.</summary>
    public sealed class CommandInvocation
    {
        private readonly ReadOnlyCollection<string> arguments;

        /// <summary>Creates a command invocation.</summary>
        public CommandInvocation(string name, IReadOnlyList<string> arguments)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A command name is required.", nameof(name));
            }

            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            var copy = new string[arguments.Count];
            for (var index = 0; index < arguments.Count; index++)
            {
                copy[index] = arguments[index] ?? string.Empty;
            }

            Name = name;
            this.arguments = Array.AsReadOnly(copy);
        }

        /// <summary>Gets the qualified command name.</summary>
        public string Name { get; }

        /// <summary>Gets immutable command arguments.</summary>
        public IReadOnlyList<string> Arguments => arguments;
    }

    /// <summary>Represents a lifetime-owned command registration.</summary>
    public interface ICommandRegistration : IDisposable
    {
        /// <summary>Gets the globally qualified command name.</summary>
        string QualifiedName { get; }
    }

    /// <summary>Registers and invokes deterministic owner-scoped commands.</summary>
    public interface ICommandService
    {
        /// <summary>Registers and lifetime-tracks a command.</summary>
        OperationResult<ICommandRegistration> Register(
            CommandDefinition definition,
            Func<CommandInvocation, OperationResult<string>> handler);

        /// <summary>Tries to execute an own short name or a globally qualified command.</summary>
        bool TryExecute(string name, IReadOnlyList<string> arguments, out OperationResult<string>? result);
    }

    /// <summary>Identifies structured diagnostic severity.</summary>
    public enum DiagnosticSeverity
    {
        /// <summary>Verbose developer information.</summary>
        Debug = 0,

        /// <summary>Ordinary operational information.</summary>
        Info = 1,

        /// <summary>A recoverable problem.</summary>
        Warning = 2,

        /// <summary>An operation failure.</summary>
        Error = 3
    }

    /// <summary>Contains one structured mod diagnostic.</summary>
    public sealed class DiagnosticEntry
    {
        /// <summary>Creates a diagnostic entry.</summary>
        public DiagnosticEntry(
            string code,
            string message,
            DiagnosticSeverity severity = DiagnosticSeverity.Info,
            string detail = "")
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("A stable diagnostic code is required.", nameof(code));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("A diagnostic message is required.", nameof(message));
            }

            if (!Enum.IsDefined(typeof(DiagnosticSeverity), severity))
            {
                throw new ArgumentOutOfRangeException(nameof(severity));
            }

            Code = code;
            Message = message;
            Severity = severity;
            Detail = detail ?? string.Empty;
        }

        /// <summary>Gets the stable machine-readable code.</summary>
        public string Code { get; }

        /// <summary>Gets the short user-readable message.</summary>
        public string Message { get; }

        /// <summary>Gets the severity.</summary>
        public DiagnosticSeverity Severity { get; }

        /// <summary>Gets optional remediation or technical detail.</summary>
        public string Detail { get; }
    }

    /// <summary>Contains a diagnostic captured by the runtime.</summary>
    public sealed class CapturedDiagnostic
    {
        /// <summary>Creates a captured diagnostic.</summary>
        public CapturedDiagnostic(DiagnosticEntry entry, DateTimeOffset timestamp)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            Timestamp = timestamp;
        }

        /// <summary>Gets the diagnostic content.</summary>
        public DiagnosticEntry Entry { get; }

        /// <summary>Gets when the runtime received the diagnostic.</summary>
        public DateTimeOffset Timestamp { get; }
    }

    /// <summary>Captures bounded structured diagnostics and mirrors them to the mod logger.</summary>
    public interface IDiagnosticsService
    {
        /// <summary>Reports one structured diagnostic.</summary>
        void Report(DiagnosticEntry entry);

        /// <summary>Returns a bounded snapshot in capture order.</summary>
        IReadOnlyList<CapturedDiagnostic> GetSnapshot();
    }

    /// <summary>Controls whether an extension contract permits one or multiple providers.</summary>
    public enum ExtensionCardinality
    {
        /// <summary>Exactly one provider may be registered.</summary>
        Singleton = 0,

        /// <summary>Multiple providers may be registered and are selected deterministically.</summary>
        Multiple = 1
    }

    /// <summary>Represents a lifetime-owned extension provider registration.</summary>
    public interface IExtensionRegistration : IDisposable
    {
        /// <summary>Gets whether the provider is still registered.</summary>
        bool IsActive { get; }
    }

    /// <summary>
    /// Publishes typed integration contracts and resolves only providers declared as dependencies of the current mod.
    /// </summary>
    public interface IExtensionService
    {
        /// <summary>Registers a provider owned by the current mod.</summary>
        OperationResult<IExtensionRegistration> Register<T>(
            T provider,
            ExtensionCardinality cardinality = ExtensionCardinality.Singleton) where T : class;

        /// <summary>Tries to resolve the deterministic first dependency-scoped provider.</summary>
        bool TryGet<T>(out T? provider) where T : class;

        /// <summary>Returns every dependency-scoped provider ordered by normalized provider identity.</summary>
        IReadOnlyList<T> GetAll<T>() where T : class;
    }
}
