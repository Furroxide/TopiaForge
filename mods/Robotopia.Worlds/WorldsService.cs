using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Robotopia.Mods;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Robotopia.Worlds
{
    public sealed class WorldsService : IWorldGamemodeService
    {
        public const string OpenSandboxWorldId = "robotopia.worlds.open_sandbox";
        public const string SandboxGamemodeId = "robotopia.worlds.sandbox";

        private readonly IModLogger logger;
        private readonly string dataPath;
        private readonly GameLevelBridge levelBridge;
        private readonly List<WorldDefinition> worlds = new List<WorldDefinition>();
        private readonly List<GamemodeDefinition> gamemodes = new List<GamemodeDefinition>();
        private readonly List<GamemodeMenuEntry> menuEntries = new List<GamemodeMenuEntry>();
        private readonly Dictionary<string, object> worldCheckpoints = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly ReadOnlyCollection<WorldDefinition> worldsView;
        private readonly ReadOnlyCollection<GamemodeDefinition> gamemodesView;
        private readonly ReadOnlyCollection<GamemodeMenuEntry> menuEntriesView;
        private GameObject? arenaRoot;
        private VolumeProfile? arenaProfile;
        private float lastLaunchTime = -10f;
        // Open Sandbox arena is built once the game's clean play scene finishes loading (async); these track the
        // one-shot "build the arena on the next sandbox-scene load" handshake set up by LoadOpenSandbox.
        private bool sandboxArenaPending;
        private bool sandboxSceneHookRegistered;

        public WorldsService(IModLogger logger, string dataPath)
        {
            this.logger = logger;
            this.dataPath = dataPath;
            levelBridge = new GameLevelBridge(logger);
            worldsView = new ReadOnlyCollection<WorldDefinition>(worlds);
            gamemodesView = new ReadOnlyCollection<GamemodeDefinition>(gamemodes);
            menuEntriesView = new ReadOnlyCollection<GamemodeMenuEntry>(menuEntries);
        }

        // Live read-only views over the registries (registries are only mutated on the main thread during load).
        public IReadOnlyList<WorldDefinition> Worlds => worldsView;
        public IReadOnlyList<GamemodeDefinition> Gamemodes => gamemodesView;
        public IReadOnlyList<GamemodeMenuEntry> MenuEntries => menuEntriesView;
        public WorldSession? CurrentSession { get; private set; }

        public event Action<WorldSession>? SessionChanged;

        public void DiscoverBuiltIns()
        {
            RegisterWorld(new WorldDefinition(
                OpenSandboxWorldId,
                "Open Sandbox",
                "Generated open-world sandbox arena.",
                supportsAdditiveArena: true));
            RegisterGamemode(new GamemodeDefinition(
                SandboxGamemodeId,
                "Sandbox",
                "Freeform creator sandbox."));

            // Prefer the game's curated level entry points: these carry a checkpoint asset, so we can launch
            // them through the game's own loader and they come up in correct play state (player + HDRP).
            var levels = levelBridge.GetLevels();
            if (levels.Count > 0)
            {
                foreach (var level in levels)
                {
                    var worldId = "robotopia.level." + Slug(level.SceneName);
                    RegisterWorld(new WorldDefinition(
                        worldId,
                        level.DisplayName,
                        string.IsNullOrWhiteSpace(level.Description) ? "First-party Robotopia level." : level.Description,
                        level.SceneName,
                        firstParty: true,
                        supportsSceneReplacement: true,
                        supportsAdditiveArena: false));
                    worldCheckpoints[worldId] = level.CheckpointAsset;
                }
            }
            else
            {
                DiscoverBuildSettingsScenes();
            }
        }

        private void DiscoverBuildSettingsScenes()
        {
            var activeScene = SceneManager.GetActiveScene().name;
            for (var index = 0; index < SceneManager.sceneCountInBuildSettings; index++)
            {
                var scenePath = SceneUtility.GetScenePathByBuildIndex(index);
                var sceneName = Path.GetFileNameWithoutExtension(scenePath);
                if (string.IsNullOrWhiteSpace(sceneName))
                {
                    continue;
                }

                // Skip menu/boot/loader scenes so users cannot "launch" a non-gameplay scene as a world.
                if (string.Equals(sceneName, activeScene, StringComparison.OrdinalIgnoreCase) || IsNonGameplayScene(sceneName))
                {
                    continue;
                }

                RegisterWorld(new WorldDefinition(
                    "robotopia.first_party." + Slug(sceneName),
                    sceneName,
                    "First-party Robotopia scene.",
                    sceneName,
                    firstParty: true,
                    supportsSceneReplacement: true,
                    supportsAdditiveArena: true));
            }
        }

        public static bool IsNonGameplayScene(string name)
        {
            return name.IndexOf("StartMenu", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Boot", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Loader", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Splash", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void RegisterWorld(WorldDefinition world)
        {
            worlds.RemoveAll(item => string.Equals(item.Id, world.Id, StringComparison.OrdinalIgnoreCase));
            worlds.Add(world);
        }

        public void RegisterGamemode(GamemodeDefinition gamemode)
        {
            gamemodes.RemoveAll(item => string.Equals(item.Id, gamemode.Id, StringComparison.OrdinalIgnoreCase));
            gamemodes.Add(gamemode);
        }

        public void RegisterMenuEntry(GamemodeMenuEntry entry)
        {
            menuEntries.RemoveAll(item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            menuEntries.Add(entry);
        }

        public WorldLoadResult LaunchMenuEntry(string entryId)
        {
            return LaunchMenuEntry(entryId, preferSceneReplacement: true, allowAdditiveFallback: true);
        }

        // Overload that threads the caller's configured load mode through to Load, so the launcher's "Load mode"
        // selection is honoured on the menu-entry path instead of being structurally dropped.
        public WorldLoadResult LaunchMenuEntry(string entryId, bool preferSceneReplacement, bool allowAdditiveFallback)
        {
            var entry = menuEntries.FirstOrDefault(item => string.Equals(item.Id, entryId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return WorldLoadResult.Fail("Unknown gamemode menu entry: " + entryId);
            }

            var worldId = ResolveWorldId(entry.WorldId);
            if (string.IsNullOrWhiteSpace(worldId))
            {
                return WorldLoadResult.Fail("No playable world is available for " + entry.Title + ".");
            }

            return Load(new WorldLoadRequest(worldId, entry.GamemodeId, preferSceneReplacement, allowAdditiveFallback));
        }

        public WorldLoadResult Load(WorldLoadRequest request)
        {
            var world = worlds.FirstOrDefault(item => string.Equals(item.Id, request.WorldId, StringComparison.OrdinalIgnoreCase));
            var gamemode = gamemodes.FirstOrDefault(item => string.Equals(item.Id, request.GamemodeId, StringComparison.OrdinalIgnoreCase));
            if (world == null)
            {
                return WorldLoadResult.Fail("Unknown world: " + request.WorldId);
            }

            if (gamemode == null)
            {
                return WorldLoadResult.Fail("Unknown gamemode: " + request.GamemodeId);
            }

            // Debounce rapid re-launches so a second click does not race a second scene load against the
            // first one's in-flight async load (which would overwrite the static checkpoint override). Stamp
            // immediately so even a launch that ultimately fails still throttles repeated attempts.
            if (Time.realtimeSinceStartup - lastLaunchTime < 1.5f)
            {
                return WorldLoadResult.Fail("A world is already loading; please wait.");
            }

            lastLaunchTime = Time.realtimeSinceStartup;

            // The Sandbox gamemode (and the Open Sandbox world) is a story-free creator space: launch the clean
            // Open Sandbox arena, never a first-party story level. This reverses the earlier routing where the
            // sandbox resolved to a real checkpoint level and dropped the player into the vanilla story/tutorial.
            // It is keyed on the gamemode (not just the world) so a sandbox launch pinned to a story world still
            // gets the arena instead of that world's campaign.
            if (string.Equals(gamemode.Id, SandboxGamemodeId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(world.Id, OpenSandboxWorldId, StringComparison.OrdinalIgnoreCase))
            {
                return LoadOpenSandbox(world, gamemode);
            }

            // A real level: launch through the game's loader so it comes up in correct state (checkpoint,
            // player spawn, baked HDRP lighting) and the gamemode layers on top of a properly lit scene.
            if (worldCheckpoints.TryGetValue(world.Id, out var checkpoint))
            {
                UnloadArena();
                if (levelBridge.LaunchLevel(checkpoint))
                {
                    return StartSession(world, gamemode, "gameScene", world.SceneName);
                }

                logger.Warn("Worlds could not launch " + world.Name + " via the game loader; falling back.");
            }

            // A scene-backed world: load its actual scene. The additive arena below builds only the generic
            // sandbox geometry over the current scene — it can never represent a real named scene — so for any
            // world that names a scene we load that scene rather than the requested-but-meaningless arena. This
            // also recovers when an incompatible additiveArena load mode was requested for a scene-only world,
            // which would otherwise strand the player in a bare arena instead of the world they chose.
            if (world.SupportsSceneReplacement && !string.IsNullOrWhiteSpace(world.SceneName))
            {
                UnloadArena();
                if (!levelBridge.LoadSceneByName(world.SceneName))
                {
                    try
                    {
                        // Last-resort fallback. First-party scenes are often addressable/streamed and not in
                        // build settings, so this can throw; degrade gracefully instead of crashing the game.
                        SceneManager.LoadScene(world.SceneName, LoadSceneMode.Single);
                    }
                    catch (Exception ex)
                    {
                        logger.Warn("Worlds could not load scene '" + world.SceneName + "': " + ex.Message);
                        return WorldLoadResult.Fail("Could not load world scene: " + world.Name);
                    }
                }

                return StartSession(world, gamemode, "sceneReplacement", world.SceneName);
            }

            if (!request.AllowAdditiveFallback || !world.SupportsAdditiveArena)
            {
                return WorldLoadResult.Fail("World cannot be loaded with the requested mode: " + world.Name);
            }

            BuildArena();
            return StartSession(world, gamemode, "additiveArena", SceneManager.GetActiveScene().name);
        }

        public void WriteCatalog()
        {
            Directory.CreateDirectory(dataPath);
            File.WriteAllText(Path.Combine(dataPath, "catalog.json"), CatalogJson());
        }

        public void UnloadArena()
        {
            // Cancel any in-flight "build the arena when the sandbox scene loads" handshake: tearing the arena
            // down (or switching to a different world) must not leave a pending build that fires on a later load.
            sandboxArenaPending = false;

            if (arenaRoot != null)
            {
                UnityEngine.Object.Destroy(arenaRoot);
                arenaRoot = null;
            }

            // The HDRP VolumeProfile + its components are ScriptableObjects, not destroyed with the GameObject.
            HdrpEnvironment.Cleanup(arenaProfile);
            arenaProfile = null;
        }

        // Releases the scene-loaded subscription. Called when the mod unloads (C# assemblies never unload under
        // Mono, so a dangling static event handler would otherwise survive and fire against a dead service).
        public void Dispose()
        {
            if (sandboxSceneHookRegistered)
            {
                SceneManager.sceneLoaded -= OnSandboxSceneLoaded;
                sandboxSceneHookRegistered = false;
            }

            UnloadArena();
        }

        // Launches the clean Open Sandbox arena: load the game's story-free play scene (which spawns a real
        // player), then build the arena geometry around that spawn once the async scene load completes.
        private WorldLoadResult LoadOpenSandbox(WorldDefinition selectedWorld, GamemodeDefinition gamemode)
        {
            UnloadArena();

            // Report the session as the Open Sandbox world (so the result message and SessionChanged reflect what
            // actually loaded), falling back to the requested world if the built-in sandbox world is missing.
            var sandboxWorld = worlds.FirstOrDefault(item =>
                string.Equals(item.Id, OpenSandboxWorldId, StringComparison.OrdinalIgnoreCase)) ?? selectedWorld;

            if (levelBridge.LaunchOpenSandbox())
            {
                ArmSandboxArena();
                return StartSession(sandboxWorld, gamemode, "openSandbox", GameLevelBridge.SandboxSceneName);
            }

            // The game's play scene could not be loaded (missing symbol). Fall back to building the arena over the
            // current scene so the launch still produces something, even if it overlays a non-gameplay scene.
            logger.Warn("Worlds could not load the game sandbox scene; building the arena over the current scene.");
            BuildArena();
            return StartSession(sandboxWorld, gamemode, "additiveArena", SceneManager.GetActiveScene().name);
        }

        // Arms a one-shot: when the sandbox play scene finishes its async load, build the arena around the player.
        private void ArmSandboxArena()
        {
            sandboxArenaPending = true;
            if (!sandboxSceneHookRegistered)
            {
                SceneManager.sceneLoaded += OnSandboxSceneLoaded;
                sandboxSceneHookRegistered = true;
            }
        }

        private void OnSandboxSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!sandboxArenaPending
                || mode != LoadSceneMode.Single
                || !string.Equals(scene.name, GameLevelBridge.SandboxSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            sandboxArenaPending = false;

            // The scene's native bootstrap spawns the player at its own transform; centre the arena there so the
            // ground/walls line up with the spawn, and grab the prefab in case we must spawn a fallback player.
            var spawnPosition = levelBridge.GetSandboxSpawnPosition();
            var playerPrefab = levelBridge.ResolveSandboxPlayerPrefab();
            BuildArena(spawnPosition);

            if (arenaRoot != null)
            {
                var guard = arenaRoot.AddComponent<SandboxPlayerGuard>();
                guard.Initialize(levelBridge, playerPrefab, spawnPosition, logger, 1.5f);
            }

            logger.Info("Worlds open sandbox arena ready in scene '" + scene.name + "'.");
        }

        private string ResolveWorldId(string requestedWorldId)
        {
            if (!string.IsNullOrWhiteSpace(requestedWorldId) &&
                worlds.Any(item => string.Equals(item.Id, requestedWorldId, StringComparison.OrdinalIgnoreCase)))
            {
                return requestedWorldId;
            }

            // Prefer a real, checkpoint-backed level (correct HDRP state); fall back to the sandbox arena.
            var realLevel = worlds.FirstOrDefault(item => worldCheckpoints.ContainsKey(item.Id));
            if (realLevel != null)
            {
                return realLevel.Id;
            }

            return worlds.Any(item => string.Equals(item.Id, OpenSandboxWorldId, StringComparison.OrdinalIgnoreCase))
                ? OpenSandboxWorldId
                : worlds.FirstOrDefault()?.Id ?? string.Empty;
        }

        private WorldLoadResult StartSession(WorldDefinition world, GamemodeDefinition gamemode, string mode, string sceneName)
        {
            // The debounce timestamp is already stamped at the top of Load (covering both success and failure);
            // re-stamping here would be a redundant second source of truth for the same value.
            var session = new WorldSession(world.Id, gamemode.Id, mode, sceneName, DateTime.UtcNow);
            CurrentSession = session;
            SessionChanged?.Invoke(session);
            return WorldLoadResult.Success(session, "Loaded " + world.Name + " with " + gamemode.Name + ".");
        }

        private void BuildArena()
        {
            BuildArena(Vector3.zero);
        }

        // Centres the ground/boundary geometry at <paramref name="center"/> so the arena lines up with wherever
        // the sandbox player actually spawns (the play scene's spawn point), rather than always at world origin.
        private void BuildArena(Vector3 center)
        {
            UnloadArena();
            arenaRoot = new GameObject("Robotopia Worlds - Open Sandbox");
            UnityEngine.Object.DontDestroyOnLoad(arenaRoot);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Sandbox Ground";
            ground.transform.SetParent(arenaRoot.transform, false);
            ground.transform.localScale = new Vector3(120f, 1f, 120f);
            ground.transform.position = center + new Vector3(0f, -0.5f, 0f);

            for (var index = 0; index < 4; index++)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "Sandbox Boundary " + index;
                wall.transform.SetParent(arenaRoot.transform, false);
                wall.transform.localScale = index < 2 ? new Vector3(120f, 8f, 1f) : new Vector3(1f, 8f, 120f);
                wall.transform.position = center + index switch
                {
                    0 => new Vector3(0f, 4f, 60f),
                    1 => new Vector3(0f, 4f, -60f),
                    2 => new Vector3(60f, 4f, 0f),
                    _ => new Vector3(-60f, 4f, 0f)
                };
            }

            // HDRP has no default sky/exposure/tonemapping; without a global Volume the arena looks washed out.
            arenaProfile = HdrpEnvironment.Apply(arenaRoot, logger);
            logger.Info("Built open sandbox arena.");
        }

        private string CatalogJson()
        {
            var builder = new StringBuilder();
            builder.Append("{\"worlds\":[");
            AppendWorlds(builder);
            builder.Append("],\"gamemodes\":[");
            AppendGamemodes(builder);
            builder.Append("]}");
            return builder.ToString();
        }

        private void AppendWorlds(StringBuilder builder)
        {
            for (var index = 0; index < worlds.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var world = worlds[index];
                builder
                    .Append("{\"id\":\"").Append(Escape(world.Id))
                    .Append("\",\"name\":\"").Append(Escape(world.Name))
                    .Append("\",\"description\":\"").Append(Escape(world.Description))
                    .Append("\",\"sceneName\":\"").Append(Escape(world.SceneName))
                    .Append("\",\"firstParty\":").Append(world.FirstParty ? "true" : "false")
                    .Append(",\"supportsSceneReplacement\":").Append(world.SupportsSceneReplacement ? "true" : "false")
                    .Append(",\"supportsAdditiveArena\":").Append(world.SupportsAdditiveArena ? "true" : "false")
                    .Append('}');
            }
        }

        private void AppendGamemodes(StringBuilder builder)
        {
            for (var index = 0; index < gamemodes.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var gamemode = gamemodes[index];
                builder
                    .Append("{\"id\":\"").Append(Escape(gamemode.Id))
                    .Append("\",\"name\":\"").Append(Escape(gamemode.Name))
                    .Append("\",\"description\":\"").Append(Escape(gamemode.Description))
                    .Append("\"}");
            }
        }

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        private static string Slug(string value)
        {
            var builder = new StringBuilder();
            foreach (var character in value.ToLowerInvariant())
            {
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');
            }

            return builder.ToString().Trim('_');
        }
    }
}
