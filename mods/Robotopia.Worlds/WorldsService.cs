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
        // Aliases of the SDK's WellKnownIds so consumers that cannot reference this assembly and this
        // service always agree on the ids (SdkSurfaceTests pins the WellKnownIds values).
        public const string OpenSandboxWorldId = WellKnownIds.OpenSandboxWorldId;
        public const string SandboxGamemodeId = WellKnownIds.SandboxGamemodeId;

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
        private readonly Dictionary<string, ICustomWorldContent> customWorldContent =
            new Dictionary<string, ICustomWorldContent>(StringComparer.OrdinalIgnoreCase);
        private GameObject? arenaRoot;
        private VolumeProfile? arenaProfile;
        private float lastLaunchTime = -10f;
        // Open Sandbox arena is built once the game's clean play scene finishes loading (async); this tracks the
        // one-shot "build the arena on the next sandbox-scene load" handshake set up by LoadOpenSandbox.
        private bool sandboxArenaPending;
        // One-shot payload armed by LoadCustomWorld and consumed on the same sandbox-scene load: the custom
        // world's pre-created content, waiting for the play scene (and its player spawn) to exist.
        private PendingCustomWorld? pendingCustomWorld;

        public WorldsService(IModLogger logger, string dataPath)
        {
            this.logger = logger;
            this.dataPath = dataPath;
            levelBridge = new GameLevelBridge(logger);
            worldsView = new ReadOnlyCollection<WorldDefinition>(worlds);
            gamemodesView = new ReadOnlyCollection<GamemodeDefinition>(gamemodes);
            menuEntriesView = new ReadOnlyCollection<GamemodeMenuEntry>(menuEntries);

            // Persistent scene hook (removed in Dispose). Registered here — before the manager plugin's own
            // sceneLoaded dispatch to mods — so the session is already ended by the time per-session handlers
            // (e.g. a gamemode controller's own sceneLoaded hook) run in the same dispatch.
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // Live read-only views over the registries (registries are only mutated on the main thread during load).
        public IReadOnlyList<WorldDefinition> Worlds => worldsView;
        public IReadOnlyList<GamemodeDefinition> Gamemodes => gamemodesView;
        public IReadOnlyList<GamemodeMenuEntry> MenuEntries => menuEntriesView;
        public WorldSession? CurrentSession { get; private set; }

        public event Action<WorldSession>? SessionChanged;
        public event Action<WorldSessionEnd>? SessionEnded;

        // Config gate for the automatic end-on-menu behaviour (WorldsConfig.EndSessionOnMenuScene). Explicit
        // EndSession calls are never gated.
        public bool EndSessionOnMenuScene { get; set; } = true;

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
                if (string.Equals(sceneName, activeScene, StringComparison.OrdinalIgnoreCase) || GameScenes.IsNonGameplayScene(sceneName))
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

        public void RegisterWorld(WorldDefinition world)
        {
            worlds.RemoveAll(item => string.Equals(item.Id, world.Id, StringComparison.OrdinalIgnoreCase));
            worlds.Add(world);
            // A plain re-registration means "this id is a normal world again" — drop any stale content link.
            customWorldContent.Remove(world.Id);
        }

        public void RegisterWorld(WorldDefinition world, ICustomWorldContent content)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            RegisterWorld(world);
            customWorldContent[world.Id] = content;
        }

        public bool UnregisterWorld(string worldId)
        {
            if (string.IsNullOrWhiteSpace(worldId))
            {
                return false;
            }

            var removed = worlds.RemoveAll(item => string.Equals(item.Id, worldId, StringComparison.OrdinalIgnoreCase)) > 0;
            customWorldContent.Remove(worldId);
            worldCheckpoints.Remove(worldId);
            if (removed && CurrentSession != null
                && string.Equals(CurrentSession.WorldId, worldId, StringComparison.OrdinalIgnoreCase))
            {
                EndSession(WorldSessionEndReason.ProviderUnloading);
            }

            return removed;
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

            // A new launch replaces any live session: end it properly (arena teardown + SessionEnded) so the
            // outgoing gamemode's controller is disposed before SessionChanged starts the incoming one.
            EndSession(WorldSessionEndReason.Superseded);

            // Custom-content worlds (mod-shipped prefabs/instances) route first — BEFORE the sandbox-gamemode
            // routing below — so a custom world is playable both with the Sandbox gamemode and any other.
            if (customWorldContent.TryGetValue(world.Id, out var content))
            {
                return LoadCustomWorld(world, gamemode, content);
            }

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

            // Same for a pending custom world; a pre-created scene instance the launch never placed would
            // otherwise leak as a hidden DontDestroyOnLoad object.
            if (pendingCustomWorld != null)
            {
                if (pendingCustomWorld.IsInstance && pendingCustomWorld.ContentRootOrPrefab != null)
                {
                    UnityEngine.Object.Destroy(pendingCustomWorld.ContentRootOrPrefab);
                }

                pendingCustomWorld = null;
            }

            if (arenaRoot != null)
            {
                UnityEngine.Object.Destroy(arenaRoot);
                arenaRoot = null;
            }

            // The HDRP VolumeProfile + its components are ScriptableObjects, not destroyed with the GameObject.
            HdrpEnvironment.Cleanup(arenaProfile);
            arenaProfile = null;
        }

        /// <summary>
        /// Ends the current session: clears <see cref="CurrentSession"/> first (so re-entrant calls and
        /// subscribers observing the service see no active session), tears down the sandbox arena, then fires
        /// <see cref="SessionEnded"/> exactly once.
        /// </summary>
        public void EndSession(WorldSessionEndReason reason)
        {
            var session = CurrentSession;
            if (session == null)
            {
                return;
            }

            CurrentSession = null;
            UnloadArena();
            try
            {
                SessionEnded?.Invoke(new WorldSessionEnd(session, reason));
            }
            catch (Exception ex)
            {
                logger.Error(ex, "A SessionEnded subscriber failed.");
            }

            logger.Info("World session ended (" + reason + "): " + session.GamemodeId + " in " + session.WorldId + ".");
        }

        // Releases the scene-loaded subscription. Called when the mod unloads (C# assemblies never unload under
        // Mono, so a dangling static event handler would otherwise survive and fire against a dead service).
        public void Dispose()
        {
            EndSession(WorldSessionEndReason.ProviderUnloading);
            SceneManager.sceneLoaded -= OnSceneLoaded;
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
        // The persistent OnSceneLoaded hook (registered in the constructor) picks it up.
        private void ArmSandboxArena()
        {
            sandboxArenaPending = true;
        }

        // Launches a mod-provided custom world: create the content eagerly (so a broken bundle fails the load
        // synchronously, before any scene is touched), load the clean sandbox play scene (real player spawn),
        // then place the content at the player spawn once that scene is up.
        private WorldLoadResult LoadCustomWorld(WorldDefinition world, GamemodeDefinition gamemode, ICustomWorldContent content)
        {
            UnloadArena();

            GameObject contentRoot;
            try
            {
                if (!(content.CreateContentRoot() is GameObject created))
                {
                    return WorldLoadResult.Fail(
                        "Custom world content for '" + world.Name + "' could not be created (see the log for details).");
                }

                contentRoot = created;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Custom world content for '" + world.Name + "' threw during creation.");
                return WorldLoadResult.Fail("Custom world content for '" + world.Name + "' failed: " + ex.Message);
            }

            // A live scene instance (procedural world) must survive the single-mode scene switch and stay
            // hidden until placement; a prefab asset is just held and instantiated at placement time.
            var isInstance = contentRoot.scene.IsValid();
            if (isInstance)
            {
                UnityEngine.Object.DontDestroyOnLoad(contentRoot);
                contentRoot.SetActive(false);
            }

            if (!levelBridge.LaunchOpenSandbox())
            {
                // Unlike the generic arena, a custom world overlaid on whatever scene is active (usually the
                // menu) is useless — fail the launch instead.
                if (isInstance)
                {
                    UnityEngine.Object.Destroy(contentRoot);
                }

                return WorldLoadResult.Fail("The game's sandbox play scene could not be loaded for '" + world.Name + "'.");
            }

            pendingCustomWorld = new PendingCustomWorld(world, content, contentRoot, isInstance);
            sandboxArenaPending = false;
            return StartSession(world, gamemode, "customWorld", GameLevelBridge.SandboxSceneName);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single)
            {
                return;
            }

            // Reaching a non-gameplay scene (menu/boot/loader) under a live session means the player left the
            // world — most commonly via the game's own pause-menu exit. End the session so no gamemode stays
            // active over the menu (HUD overlays, time drivers, spawning). The mode==Single gate above keeps
            // additively streamed scenes (e.g. "...Loader" content scenes) from falsely ending a session.
            if (EndSessionOnMenuScene && CurrentSession != null && GameScenes.IsNonGameplayScene(scene.name))
            {
                EndSession(WorldSessionEndReason.MenuReached);
                return;
            }

            OnSandboxSceneLoaded(scene, mode);
        }

        private void OnSandboxSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single
                || !string.Equals(scene.name, GameLevelBridge.SandboxSceneName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (pendingCustomWorld != null)
            {
                var pending = pendingCustomWorld;
                pendingCustomWorld = null;
                PlaceCustomWorld(pending, scene);
                return;
            }

            if (!sandboxArenaPending)
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

        // Materializes a pending custom world in the freshly loaded sandbox play scene: instantiate/adopt the
        // content, align its spawn point to the native player spawn, apply the default environment (unless
        // the content brings its own global Volume), and attach the player guards.
        private void PlaceCustomWorld(PendingCustomWorld pending, Scene scene)
        {
            var spawnPosition = levelBridge.GetSandboxSpawnPosition();
            try
            {
                arenaRoot = new GameObject("Robotopia Worlds - Custom World: " + pending.World.Id);
                UnityEngine.Object.DontDestroyOnLoad(arenaRoot);

                GameObject root;
                if (pending.IsInstance)
                {
                    root = pending.ContentRootOrPrefab;
                    root.SetActive(true);
                }
                else
                {
                    root = UnityEngine.Object.Instantiate(pending.ContentRootOrPrefab);
                }

                root.transform.SetParent(arenaRoot.transform, worldPositionStays: true);

                // Move the world to the player: offset the root so its spawn marker coincides with where the
                // scene's native bootstrap spawns the player — no player teleport, no extra game reflection.
                var options = pending.Content.Options;
                var spawnPoint = FindDescendant(root.transform, options.SpawnPointName);
                if (spawnPoint != null)
                {
                    root.transform.position += spawnPosition - spawnPoint.position;
                }
                else
                {
                    logger.Warn("Custom world '" + pending.World.Name + "' has no '" + options.SpawnPointName
                        + "' marker; using the content root as the spawn point.");
                    root.transform.position = spawnPosition;
                }

                var effectiveSpawn = spawnPoint != null ? spawnPoint.position : root.transform.position;

                // Respect a world that ships its own sky/exposure: any active global Volume suppresses ours.
                var hasOwnEnvironment = HasGlobalVolume(root);
                if (options.ApplyDefaultEnvironment && !hasOwnEnvironment)
                {
                    arenaProfile = HdrpEnvironment.Apply(arenaRoot, logger);
                }

                var guard = arenaRoot.AddComponent<SandboxPlayerGuard>();
                guard.Initialize(levelBridge, levelBridge.ResolveSandboxPlayerPrefab(), effectiveSpawn, logger, 1.5f);
                if (options.EnableKillPlane)
                {
                    var killPlane = arenaRoot.AddComponent<CustomWorldPlayerGuard>();
                    killPlane.Initialize(levelBridge, effectiveSpawn, effectiveSpawn.y - options.KillPlaneDepth, logger);
                }

                logger.Info("Custom world '" + pending.World.Name + "' placed in scene '" + scene.name + "'.");
            }
            catch (Exception ex)
            {
                // Never strand the player on a void: tear down whatever half-placed content exists and fall
                // back to the generated arena so the session stays playable.
                logger.Error(ex, "Custom world '" + pending.World.Name + "' failed to place; falling back to the arena.");
                UnloadArena();
                BuildArena(spawnPosition);
            }
        }

        private static Transform? FindDescendant(Transform root, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            // Breadth-first so a top-level marker wins over an identically named nested one.
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current != root && string.Equals(current.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                for (var index = 0; index < current.childCount; index++)
                {
                    queue.Enqueue(current.GetChild(index));
                }
            }

            return null;
        }

        private static bool HasGlobalVolume(GameObject root)
        {
            foreach (var volume in root.GetComponentsInChildren<Volume>(true))
            {
                if (volume.isGlobal)
                {
                    return true;
                }
            }

            return false;
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

            SandboxArenaBuilder.Build(arenaRoot, center, logger);

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

        private sealed class PendingCustomWorld
        {
            public PendingCustomWorld(WorldDefinition world, ICustomWorldContent content, GameObject contentRootOrPrefab, bool isInstance)
            {
                World = world;
                Content = content;
                ContentRootOrPrefab = contentRootOrPrefab;
                IsInstance = isInstance;
            }

            public WorldDefinition World { get; }
            public ICustomWorldContent Content { get; }

            /// <summary>A live scene instance when <see cref="IsInstance"/>, otherwise a prefab asset.</summary>
            public GameObject ContentRootOrPrefab { get; }

            public bool IsInstance { get; }
        }
    }
}
