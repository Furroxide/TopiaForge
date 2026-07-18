using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Identifies the optional Worlds contract and runtime provider.</summary>
    public static class WorldsModule
    {
        /// <summary>Gets the manifest id used to declare the Worlds runtime module.</summary>
        public const string Id = "io.github.furroxide.topiaforge.worlds";
    }

    /// <summary>Registers worlds and modes and owns one typed world session.</summary>
    public interface IWorldGamemodeService
    {
        /// <summary>Gets all visible world definitions in normalized id order.</summary>
        IReadOnlyList<WorldDefinition> Worlds { get; }

        /// <summary>Gets all visible gamemode definitions in normalized id order.</summary>
        IReadOnlyList<GamemodeDefinition> Gamemodes { get; }

        /// <summary>Gets all visible launch entries in normalized id order.</summary>
        IReadOnlyList<GamemodeMenuEntry> MenuEntries { get; }

        /// <summary>Gets the current session, or null outside a modded world session.</summary>
        WorldSession? CurrentSession { get; }

        /// <summary>Raised after a new session becomes authoritative.</summary>
        event Action<WorldSession>? SessionChanged;

        /// <summary>Raised exactly once after a session is cleared.</summary>
        event Action<WorldSessionEnd>? SessionEnded;

        /// <summary>Registers a world for the current mod lifetime.</summary>
        OperationResult<IWorldRegistration> RegisterWorld(
            WorldDefinition world,
            ICustomWorldContent? content = null);

        /// <summary>Registers a gamemode for the current mod lifetime.</summary>
        OperationResult<IWorldRegistration> RegisterGamemode(GamemodeDefinition gamemode);

        /// <summary>Registers a launch entry for the current mod lifetime.</summary>
        OperationResult<IWorldRegistration> RegisterMenuEntry(GamemodeMenuEntry entry);

        /// <summary>Loads a registered world and starts its session.</summary>
        Task<OperationResult<WorldSession>> LoadAsync(
            WorldLoadRequest request,
            CancellationToken cancellationToken = default);

        /// <summary>Loads the world and gamemode referenced by a registered launch entry.</summary>
        Task<OperationResult<WorldSession>> LaunchMenuEntryAsync(
            string entryId,
            CancellationToken cancellationToken = default);

        /// <summary>Ends the current session. The call is idempotent.</summary>
        OperationResult<bool> EndSession(WorldSessionEndReason reason);
    }

    /// <summary>Exposes whether the provider is currently changing scenes.</summary>
    public interface IWorldTransitionState
    {
        /// <summary>Gets whether a world scene transition is in flight.</summary>
        bool IsTransitionInFlight { get; }
    }

    /// <summary>A lifetime-owned world, gamemode, or menu-entry registration.</summary>
    public interface IWorldRegistration : IDisposable
    {
        /// <summary>Gets the registered stable id.</summary>
        string Id { get; }

        /// <summary>Gets the kind of definition registered.</summary>
        WorldRegistrationKind Kind { get; }

        /// <summary>Gets whether the definition is still registered.</summary>
        bool IsActive { get; }
    }

    /// <summary>Identifies the definition owned by a world registration.</summary>
    public enum WorldRegistrationKind
    {
        /// <summary>A world definition.</summary>
        World = 0,

        /// <summary>A gamemode definition.</summary>
        Gamemode = 1,

        /// <summary>A launch-menu entry.</summary>
        MenuEntry = 2
    }

    /// <summary>Controls whether automatic world loads may displace user-initiated work.</summary>
    public enum WorldLoadPriority
    {
        /// <summary>Background work that yields to an active transition.</summary>
        Automatic = 0,

        /// <summary>An explicit user launch that may supersede automatic work.</summary>
        UserInitiated = 1
    }

    /// <summary>Explains why a world session ended.</summary>
    public enum WorldSessionEndReason
    {
        /// <summary>A non-gameplay menu scene became active.</summary>
        MenuReached = 0,

        /// <summary>The gamemode ended its own session.</summary>
        EndedByGamemode = 1,

        /// <summary>A newer session replaced this one.</summary>
        Superseded = 2,

        /// <summary>The provider or owning registration was released.</summary>
        ProviderUnloading = 3,

        /// <summary>Another coordinated scene request replaced the session scene.</summary>
        SceneReplaced = 4,

        /// <summary>The scene load failed, timed out, or was cancelled.</summary>
        LoadFailed = 5
    }

    /// <summary>Contains an ended session and its stable reason.</summary>
    public sealed class WorldSessionEnd
    {
        /// <summary>Creates a session-end event.</summary>
        public WorldSessionEnd(WorldSession session, WorldSessionEndReason reason)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Reason = reason;
        }

        /// <summary>Gets the ended session.</summary>
        public WorldSession Session { get; }

        /// <summary>Gets why the session ended.</summary>
        public WorldSessionEndReason Reason { get; }
    }

    /// <summary>Pairs a gamemode and optional world for launch surfaces.</summary>
    public sealed class GamemodeMenuEntry
    {
        /// <summary>Creates a menu entry.</summary>
        public GamemodeMenuEntry(
            string id,
            string title,
            string description,
            string gamemodeId,
            string worldId = "")
        {
            Id = Require(id, nameof(id));
            Title = Require(title, nameof(title));
            Description = description ?? string.Empty;
            GamemodeId = Require(gamemodeId, nameof(gamemodeId));
            WorldId = worldId ?? string.Empty;
        }

        /// <summary>Gets the stable entry id.</summary>
        public string Id { get; }

        /// <summary>Gets the display title.</summary>
        public string Title { get; }

        /// <summary>Gets the display description.</summary>
        public string Description { get; }

        /// <summary>Gets the registered gamemode id.</summary>
        public string GamemodeId { get; }

        /// <summary>Gets the optional registered world id.</summary>
        public string WorldId { get; }

        private static string Require(string value, string parameter) =>
            !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ArgumentException("A non-empty value is required.", parameter);
    }

    /// <summary>Describes one launchable world.</summary>
    public sealed class WorldDefinition
    {
        /// <summary>Creates a world definition.</summary>
        public WorldDefinition(
            string id,
            string name,
            string description,
            string sceneName = "",
            bool firstParty = false,
            bool supportsSceneReplacement = false,
            bool supportsAdditiveArena = true)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("World id and name are required.");
            }

            Id = id;
            Name = name;
            Description = description ?? string.Empty;
            SceneName = sceneName ?? string.Empty;
            FirstParty = firstParty;
            SupportsSceneReplacement = supportsSceneReplacement;
            SupportsAdditiveArena = supportsAdditiveArena;
        }

        /// <summary>Gets the stable world id.</summary>
        public string Id { get; }

        /// <summary>Gets the display name.</summary>
        public string Name { get; }

        /// <summary>Gets the display description.</summary>
        public string Description { get; }

        /// <summary>Gets the optional game scene name.</summary>
        public string SceneName { get; }

        /// <summary>Gets whether this is built-in framework content.</summary>
        public bool FirstParty { get; }

        /// <summary>Gets whether the world can replace the active scene.</summary>
        public bool SupportsSceneReplacement { get; }

        /// <summary>Gets whether the world can run as an additive arena.</summary>
        public bool SupportsAdditiveArena { get; }
    }

    /// <summary>Describes one gamemode.</summary>
    public sealed class GamemodeDefinition
    {
        /// <summary>Creates a gamemode definition.</summary>
        public GamemodeDefinition(string id, string name, string description)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Gamemode id and name are required.");
            }

            Id = id;
            Name = name;
            Description = description ?? string.Empty;
        }

        /// <summary>Gets the stable gamemode id.</summary>
        public string Id { get; }

        /// <summary>Gets the display name.</summary>
        public string Name { get; }

        /// <summary>Gets the display description.</summary>
        public string Description { get; }
    }

    /// <summary>Describes a world and gamemode launch.</summary>
    public sealed class WorldLoadRequest
    {
        /// <summary>Creates a launch request.</summary>
        public WorldLoadRequest(
            string worldId,
            string gamemodeId,
            WorldLoadPriority priority = WorldLoadPriority.UserInitiated,
            bool preferSceneReplacement = true,
            bool allowAdditiveFallback = true)
        {
            if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(gamemodeId))
            {
                throw new ArgumentException("World and gamemode ids are required.");
            }

            if (!Enum.IsDefined(typeof(WorldLoadPriority), priority))
            {
                throw new ArgumentOutOfRangeException(nameof(priority));
            }

            WorldId = worldId;
            GamemodeId = gamemodeId;
            Priority = priority;
            PreferSceneReplacement = preferSceneReplacement;
            AllowAdditiveFallback = allowAdditiveFallback;
        }

        /// <summary>Gets the registered world id.</summary>
        public string WorldId { get; }

        /// <summary>Gets the registered gamemode id.</summary>
        public string GamemodeId { get; }

        /// <summary>Gets transition priority.</summary>
        public WorldLoadPriority Priority { get; }

        /// <summary>Gets whether single-scene replacement should be tried first.</summary>
        public bool PreferSceneReplacement { get; }

        /// <summary>Gets whether an additive fallback is allowed.</summary>
        public bool AllowAdditiveFallback { get; }
    }

    /// <summary>Immutable state for one active world session.</summary>
    public sealed class WorldSession
    {
        /// <summary>Creates session state.</summary>
        public WorldSession(
            string worldId,
            string gamemodeId,
            string mode,
            string sceneName,
            DateTimeOffset startedAtUtc)
        {
            WorldId = worldId ?? string.Empty;
            GamemodeId = gamemodeId ?? string.Empty;
            Mode = mode ?? string.Empty;
            SceneName = sceneName ?? string.Empty;
            StartedAtUtc = startedAtUtc;
        }

        /// <summary>Gets the world id.</summary>
        public string WorldId { get; }

        /// <summary>Gets the gamemode id.</summary>
        public string GamemodeId { get; }

        /// <summary>Gets the provider-selected load mode.</summary>
        public string Mode { get; }

        /// <summary>Gets the active game scene name.</summary>
        public string SceneName { get; }

        /// <summary>Gets when the session started.</summary>
        public DateTimeOffset StartedAtUtc { get; }
    }
}
