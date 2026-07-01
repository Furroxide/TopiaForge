using System;
using System.Collections.Generic;

namespace Robotopia.Mods
{
    public interface IWorldGamemodeService
    {
        IReadOnlyList<WorldDefinition> Worlds { get; }
        IReadOnlyList<GamemodeDefinition> Gamemodes { get; }
        IReadOnlyList<GamemodeMenuEntry> MenuEntries { get; }
        WorldSession? CurrentSession { get; }

        event Action<WorldSession>? SessionChanged;

        void RegisterWorld(WorldDefinition world);
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
            bool preferSceneReplacement = true,
            bool allowAdditiveFallback = true)
        {
            WorldId = worldId;
            GamemodeId = gamemodeId;
            PreferSceneReplacement = preferSceneReplacement;
            AllowAdditiveFallback = allowAdditiveFallback;
        }

        public string WorldId { get; }
        public string GamemodeId { get; }
        public bool PreferSceneReplacement { get; }
        public bool AllowAdditiveFallback { get; }
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
