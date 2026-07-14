using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    public interface IWorldGamemodeService
    {
        IReadOnlyList<WorldDefinition> Worlds { get; }
        IReadOnlyList<GamemodeDefinition> Gamemodes { get; }
        IReadOnlyList<GamemodeMenuEntry> MenuEntries { get; }
        WorldSession? CurrentSession { get; }

        event Action<WorldSession>? SessionChanged;

        /// <summary>
        /// Raised exactly once per session when it ends — whether the gamemode ended it, a new launch
        /// superseded it, the player reached a non-gameplay scene (e.g. the vanilla exit-to-menu), or the
        /// provider is unloading. Fired after <see cref="CurrentSession"/> has been cleared, so subscribers
        /// observing the service see no active session.
        /// </summary>
        event Action<WorldSessionEnd>? SessionEnded;

        void RegisterWorld(WorldDefinition world);

        /// <summary>
        /// Registers a world backed by mod-provided content (e.g. a prefab from a mod-shipped
        /// AssetBundle — see <see cref="BundleWorldContent"/>). Launching it loads the game's clean play
        /// stage (a real player spawns natively) and places the content at the player spawn; the content
        /// is destroyed when the session ends. Re-registering the same world id replaces the content.
        /// </summary>
        void RegisterWorld(WorldDefinition world, ICustomWorldContent content);

        /// <summary>
        /// Removes a registered world (and any content coupling). Ends the current session with
        /// <see cref="WorldSessionEndReason.ProviderUnloading"/> when that world backs it. Returns
        /// <c>false</c> when the id is unknown. Call from <c>OnUnload</c> for worlds your mod registered.
        /// </summary>
        bool UnregisterWorld(string worldId);

        void RegisterGamemode(GamemodeDefinition gamemode);

        /// <summary>
        /// Registers a launchable entry (a gamemode paired with a world) to be surfaced in the game's
        /// level-select menu and the mod manager overlay. A blank <see cref="GamemodeMenuEntry.WorldId"/>
        /// lets the service pick a sensible default playable world.
        /// </summary>
        void RegisterMenuEntry(GamemodeMenuEntry entry);

        WorldLoadResult Load(WorldLoadRequest request);

        /// <summary>Launches a previously registered menu entry by id, loading the world in correct play state.</summary>
        WorldLoadResult LaunchMenuEntry(string entryId);

        /// <summary>
        /// Ends the current session: clears <see cref="CurrentSession"/>, tears down provider-owned session
        /// state (e.g. the sandbox arena), and fires <see cref="SessionEnded"/>. Idempotent — a no-op when
        /// no session is active.
        /// </summary>
        void EndSession(WorldSessionEndReason reason);
    }

    /// <summary>
    /// Optional capability published by world providers that expose their scene-load state. It is separate
    /// from <see cref="IWorldGamemodeService"/> because consumers do not need transition-state details merely
    /// to register or launch worlds.
    /// </summary>
    public interface IWorldTransitionState
    {
        /// <summary>
        /// True from scene-load dispatch until the target single-mode scene arrives (or the provider times out).
        /// Automatic scene triggers should use <see cref="ISceneCoordinator"/> rather than racing this state.
        /// </summary>
        bool IsTransitionInFlight { get; }
    }

    /// <summary>
    /// Optional registry-lifecycle capability for world providers. Consumers that register gamemodes or menu
    /// entries should resolve this capability and undo those registrations from <c>OnUnload</c>.
    /// </summary>
    public interface IWorldRegistrationService
    {
        /// <summary>
        /// Removes a gamemode. If it owns the current session, that session ends with
        /// <see cref="WorldSessionEndReason.ProviderUnloading"/>. Returns false when unknown.
        /// </summary>
        bool UnregisterGamemode(string gamemodeId);

        /// <summary>Removes a launch-menu entry. Returns false when unknown.</summary>
        bool UnregisterMenuEntry(string entryId);
    }

    /// <summary>Why a world session ended (carried by <see cref="IWorldGamemodeService.SessionEnded"/>).</summary>
    public enum WorldSessionEndReason
    {
        /// <summary>A non-gameplay scene became active under the session (e.g. the vanilla exit-to-menu).</summary>
        MenuReached,

        /// <summary>The gamemode ended its own session (e.g. a game-over screen's return-to-menu action).</summary>
        EndedByGamemode,

        /// <summary>A new world/gamemode launch replaced this session.</summary>
        Superseded,

        /// <summary>The world/gamemode provider mod is unloading.</summary>
        ProviderUnloading,

        /// <summary>
        /// Another mod's coordinated scene transition (see <see cref="ISceneCoordinator"/>) replaced the
        /// session's scene, so the session was ended instead of running over a scene it does not own.
        /// </summary>
        SceneReplaced,

        /// <summary>
        /// The provider dispatched a scene load, but it faulted, was canceled, or never completed before the
        /// timeout. The provisional session and its coordinator claim have been torn down.
        /// </summary>
        LoadFailed
    }

    public sealed class WorldSessionEnd
    {
        public WorldSessionEnd(WorldSession session, WorldSessionEndReason reason)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Reason = reason;
        }

        public WorldSession Session { get; }
        public WorldSessionEndReason Reason { get; }
    }

    public sealed class GamemodeMenuEntry
    {
        public GamemodeMenuEntry(string id, string title, string description, string gamemodeId, string worldId = "")
        {
            Id = id;
            Title = title;
            Description = description;
            GamemodeId = gamemodeId;
            WorldId = worldId;
        }

        public string Id { get; }
        public string Title { get; }
        public string Description { get; }
        public string GamemodeId { get; }

        /// <summary>Optional world to launch the gamemode in. Blank means "let the service choose a default".</summary>
        public string WorldId { get; }
    }

    public sealed class WorldDefinition
    {
        public WorldDefinition(
            string id,
            string name,
            string description,
            string sceneName = "",
            bool firstParty = false,
            bool supportsSceneReplacement = false,
            bool supportsAdditiveArena = true)
        {
            Id = id;
            Name = name;
            Description = description;
            SceneName = sceneName;
            FirstParty = firstParty;
            SupportsSceneReplacement = supportsSceneReplacement;
            SupportsAdditiveArena = supportsAdditiveArena;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string SceneName { get; }
        public bool FirstParty { get; }
        public bool SupportsSceneReplacement { get; }
        public bool SupportsAdditiveArena { get; }
    }

    public sealed class GamemodeDefinition
    {
        public GamemodeDefinition(string id, string name, string description)
        {
            Id = id;
            Name = name;
            Description = description;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
    }

    public sealed class WorldLoadRequest
    {
        public WorldLoadRequest(
            string worldId,
            string gamemodeId,
            SceneTransitionPriority priority = SceneTransitionPriority.UserInitiated,
            bool preferSceneReplacement = true,
            bool allowAdditiveFallback = true)
        {
            if (!Enum.IsDefined(typeof(SceneTransitionPriority), priority))
            {
                throw new ArgumentOutOfRangeException(nameof(priority));
            }

            WorldId = worldId;
            GamemodeId = gamemodeId;
            PreferSceneReplacement = preferSceneReplacement;
            AllowAdditiveFallback = allowAdditiveFallback;
            Priority = priority;
        }

        public string WorldId { get; }
        public string GamemodeId { get; }
        public bool PreferSceneReplacement { get; }
        public bool AllowAdditiveFallback { get; }

        /// <summary>
        /// Priority used to acquire the scene-transition claim before any load side effect. Direct API/UI
        /// launches keep the default; startup auto-loaders pass <see cref="SceneTransitionPriority.Automatic"/>.
        /// </summary>
        public SceneTransitionPriority Priority { get; }
    }

    public sealed class WorldSession
    {
        public WorldSession(
            string worldId,
            string gamemodeId,
            string mode,
            string sceneName,
            DateTime startedAtUtc)
        {
            WorldId = worldId;
            GamemodeId = gamemodeId;
            Mode = mode;
            SceneName = sceneName;
            StartedAtUtc = startedAtUtc;
        }

        public string WorldId { get; }
        public string GamemodeId { get; }
        public string Mode { get; }
        public string SceneName { get; }
        public DateTime StartedAtUtc { get; }
    }

    public sealed class WorldLoadResult
    {
        private WorldLoadResult(bool ok, WorldSession? session, string message)
        {
            Ok = ok;
            Session = session;
            Message = message;
        }

        public bool Ok { get; }
        public WorldSession? Session { get; }
        public string Message { get; }

        public static WorldLoadResult Success(WorldSession session, string message)
        {
            return new WorldLoadResult(true, session, message);
        }

        public static WorldLoadResult Fail(string message)
        {
            return new WorldLoadResult(false, null, message);
        }
    }
}
