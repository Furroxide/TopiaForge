using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    public sealed partial class WorldsService
    {
        public void UnloadArena()
        {
            // Cancel any in-flight "build the arena when the sandbox scene loads" handshake: tearing the arena
            // down (or switching to a different world) must not leave a pending build that fires on a later load.
            sandboxArenaPending = false;

            pendingCustomWorld = null;
            // Abandon (but keep draining) any in-flight creation: the SDK can still hand back live content,
            // and only the main thread may release the Unity objects it owns. UpdateTransition does that.
            contentLoad.Cancel();
            placingCustomWorld = null;
            activeWorldContent?.Dispose();
            activeWorldContent = null;

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
        public OperationResult<bool> EndSession(WorldSessionEndReason reason)
        {
            if (disposed)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidState,
                    "World service is disposed.");
            }

            // The scene API exposes no cancellation handle. If teardown arrives before sceneLoaded, quarantine
            // that dispatch instead of declaring it resolved: a late arrival must retire it before another world
            // load can begin, or it could be mistaken for the retry's scene.
            transitionTracker.Abandon();
            var session = CurrentSession;
            if (session == null)
            {
                return OperationResult<bool>.Success(false);
            }

            CurrentSession = null;
            sessionSceneName = string.Empty;
            UnloadArena();
            SafeEvent.Invoke(
                SessionEnded,
                new WorldSessionEnd(session, reason),
                ex => logger.Error(ex, "A SessionEnded subscriber failed."));

            logger.Info("World session ended (" + reason + "): " + session.GamemodeId + " in " + session.WorldId + ".");
            return OperationResult<bool>.Success(true);
        }

        // Releases the scene-loaded subscription. Called when the mod unloads (C# assemblies never unload under
        // Mono, so a dangling static event handler would otherwise survive and fire against a dead service).
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            EndSession(WorldSessionEndReason.ProviderUnloading);
            disposed = true;
            // Past this point no drain runs again. The mod lifetime is stopping, so the runtime's own asset
            // tracking releases anything the SDK still produces; dropping the task here cannot leak it.
            contentLoad.Forget();
            placingCustomWorld = null;
            catalogWrite = null;
            ReleasePendingSceneClaim();
            levelBridge.Dispose();
            DisposeLocalWorlds();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            UnloadArena();
            transitionTracker.Abandon();
            DeactivateRegistrations(worldRegistrations);
            DeactivateRegistrations(gamemodeRegistrations);
            DeactivateRegistrations(menuEntryRegistrations);
            worlds.Clear();
            gamemodes.Clear();
            menuEntries.Clear();
            worldCheckpoints.Clear();
            customWorldContent.Clear();
            SessionChanged = null;
            SessionEnded = null;
        }

        /// <summary>
        /// Main-thread failure/timeout drain for a provisional async scene load. A failed dispatch must not leave
        /// a gamemode active over the old scene or retain its session-scoped coordinator claim indefinitely.
        /// </summary>
        internal void UpdateTransition()
        {
            if (disposed)
            {
                return;
            }

            // Reflective Task/UniTask continuations may complete on worker threads. Drain their immutable
            // results here so logging, callbacks, and transition state changes stay on Unity's main thread.
            levelBridge.DrainAsyncLoadOutcomes();

            // SDK asset tasks complete on this thread, so custom-world creation and the diagnostic catalog
            // write are both armed elsewhere and finished here. Neither is ever waited on.
            UpdateCustomWorldPlacement();
            UpdateCatalogWrite();

            var failure = transitionTracker.ConsumeFailure(
                Time.realtimeSinceStartup,
                TransitionTimeoutSeconds);
            if (failure == null)
            {
                return;
            }

            if (!transitionTracker.IsQuarantined)
            {
                ReleasePendingSceneClaim();
            }

            EndSession(WorldSessionEndReason.LoadFailed);
            logger.Warn("Worlds provisional scene load failed: " + failure);
        }
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (disposed || mode != LoadSceneMode.Single)
            {
                return;
            }

            // Admission stays serialized until the expected target arrives. An unrelated/menu single scene does
            // not retire an abandoned/timed-out dispatch, so its target still cannot resolve a newer retry.
            if (transitionTracker.ResolveSceneArrival(scene.name))
            {
                ReleasePendingSceneClaim();
            }

            // Reaching a non-gameplay scene (menu/boot/loader) under a live session means the player left the
            // world — most commonly via the game's own pause-menu exit. End the session so no gamemode stays
            // active over the menu (HUD overlays, time drivers, spawning). The mode==Single gate above keeps
            // additively streamed scenes (e.g. "...Loader" content scenes) from falsely ending a session.
            if (CurrentSession != null && GameScenes.IsNonGameplayScene(scene.name))
            {
                if (EndSessionOnMenuScene)
                {
                    EndSession(WorldSessionEndReason.MenuReached);
                }

                // A caller that owns explicit session teardown (such as a confirmed gamemode exit) keeps the
                // original immutable session identity until it ends the session. Never rebind a live session to a
                // menu/boot/loader scene.
                return;
            }

            if (CurrentSession != null)
            {
                if (!string.Equals(scene.name, sessionSceneName, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateSessionScene(scene.name, "native single-scene transition");
                }
            }

            OnSandboxSceneLoaded(scene, mode);
        }

        private void OnActiveSceneChanged(Scene previous, Scene current)
        {
            if (disposed || CurrentSession == null || !current.IsValid())
            {
                return;
            }

            if (GameScenes.IsNonGameplayScene(current.name))
            {
                // Session exit is driven by OnSceneLoaded's Single-mode gate. An additively loaded UI/loader scene
                // may become active temporarily and must not tear down the gameplay session.
                return;
            }

            if (!string.Equals(current.name, sessionSceneName, StringComparison.OrdinalIgnoreCase))
            {
                UpdateSessionScene(current.name, "active-scene transition");
            }
        }

        private void UpdateSessionScene(string sceneName, string reason)
        {
            var previous = CurrentSession;
            if (previous == null || string.IsNullOrWhiteSpace(sceneName))
            {
                return;
            }

            var updated = new WorldSession(
                previous.WorldId,
                previous.GamemodeId,
                previous.Mode,
                sceneName,
                previous.StartedAtUtc);
            CurrentSession = updated;
            sessionSceneName = sceneName;
            logger.Debug("Worlds session scene changed to '" + sceneName + "' (" + reason
                + "); session consumers are being rebound.");
            SafeEvent.Invoke(
                SessionChanged,
                updated,
                ex => logger.Error(ex, "A SessionChanged subscriber failed during scene rebinding."));
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

        // Starts materializing SDK-owned content in the freshly loaded sandbox play scene. Creation is
        // asynchronous and its task can only complete on Unity's main thread, so it is armed here and drained
        // by UpdateTransition; blocking on it from this scene-loaded callback would deadlock the process.
        private void PlaceCustomWorld(PendingCustomWorld pending, Scene scene)
        {
            var spawnPosition = levelBridge.GetSandboxSpawnPosition();
            try
            {
                arenaRoot = new GameObject("TopiaForge Worlds - Custom World: " + pending.World.Id);
                UnityEngine.Object.DontDestroyOnLoad(arenaRoot);

                placingCustomWorld = pending;
                placingSpawnPosition = spawnPosition;
                contentLoad.Begin(
                    token => pending.Content.CreateAsync(token),
                    lifetimeToken,
                    Time.realtimeSinceStartup);
                logger.Debug("Custom world '" + pending.World.Name + "' is being created for scene '"
                    + scene.name + "'.");
            }
            catch (Exception ex)
            {
                FailCustomWorldPlacement(pending.World.Name, ex.Message, spawnPosition);
            }
        }

        // Main-thread drain for the armed creation. Runs from UpdateTransition, so every Unity object the SDK
        // hands back — including one that arrives after a cancel or timeout — is placed or released here.
        private void UpdateCustomWorldPlacement()
        {
            var state = contentLoad.Poll(
                Time.realtimeSinceStartup,
                TransitionTimeoutSeconds,
                out var result);
            switch (state)
            {
                case PendingOperationState.Idle:
                case PendingOperationState.Waiting:
                    return;

                case PendingOperationState.Abandoned:
                    // The session moved on while the SDK was still working. Release any content it produced
                    // rather than leaking the bundle, prefab, and spawned entity it owns. A newer placement may
                    // already be armed, so nothing else about the current placement is touched here.
                    if (result.TryGetValue(out var orphaned))
                    {
                        orphaned.Dispose();
                    }

                    return;

                case PendingOperationState.TimedOut:
                    FailCustomWorldPlacement(
                        placingCustomWorld?.World.Name ?? "unknown",
                        "creation did not complete within " + TransitionTimeoutSeconds + " seconds",
                        placingSpawnPosition);
                    return;

                default:
                    CompleteCustomWorldPlacement(result);
                    return;
            }
        }

        private void CompleteCustomWorldPlacement(OperationResult<IWorldContent> result)
        {
            var pending = placingCustomWorld;
            var spawnPosition = placingSpawnPosition;
            placingCustomWorld = null;
            if (pending == null)
            {
                if (result.TryGetValue(out var orphaned))
                {
                    orphaned.Dispose();
                }

                return;
            }

            if (!result.TryGetValue(out var created))
            {
                FailCustomWorldPlacement(
                    pending.World.Name,
                    result.ErrorCode + ": " + result.ErrorMessage,
                    spawnPosition);
                return;
            }

            try
            {
                if (arenaRoot == null)
                {
                    // The arena root was torn down while the SDK was working; the content has nowhere to live.
                    created.Dispose();
                    return;
                }

                activeWorldContent = created;
                var options = pending.Content.Options;
                if (options.ApplyDefaultEnvironment)
                {
                    arenaProfile = HdrpEnvironment.Apply(arenaRoot, logger);
                }

                var guard = arenaRoot.AddComponent<SandboxPlayerGuard>();
                guard.Initialize(levelBridge, levelBridge.ResolveSandboxPlayerPrefab(), spawnPosition, logger, 1.5f);
                if (options.EnableKillPlane)
                {
                    var killPlane = arenaRoot.AddComponent<CustomWorldPlayerGuard>();
                    killPlane.Initialize(levelBridge, spawnPosition, spawnPosition.y - options.KillPlaneDepth, logger);
                }

                logger.Info("Custom world '" + pending.World.Name + "' placed.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Custom world '" + pending.World.Name + "' could not be placed.");
                FailCustomWorldPlacement(pending.World.Name, ex.Message, spawnPosition);
            }
        }

        // Never strand the player on a void: tear down whatever half-placed content exists and fall back to
        // the generated arena so the session stays playable.
        private void FailCustomWorldPlacement(string worldName, string reason, Vector3 spawnPosition)
        {
            logger.Warn("Custom world '" + worldName + "' failed to place (" + reason
                + "); falling back to the generated arena.");
            placingCustomWorld = null;
            UnloadArena();
            BuildArena(spawnPosition);
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
        private WorldLoadResult StartSession(
            WorldDefinition world,
            GamemodeDefinition gamemode,
            string mode,
            string sceneName,
            IInternalSceneTransitionLease? launchClaim)
        {
            // The debounce timestamp is already stamped at the top of Load (covering both success and failure);
            // re-stamping here would be a redundant second source of truth for the same value.
            var session = new WorldSession(world.Id, gamemode.Id, mode, sceneName, DateTimeOffset.UtcNow);
            CurrentSession = session;
            sessionSceneName = sceneName;

            if (launchClaim != null)
            {
                if (transitionTracker.BlocksAdmission)
                {
                    ReleasePendingSceneClaim();
                    pendingSceneClaim = launchClaim;
                }
                else
                {
                    // A synchronous fallback may complete its scene callback before StartSession runs.
                    launchClaim.Dispose();
                }
            }

            // A consumer must not turn an already-dispatched world load into a reported failure (which
            // would also cause the caller to dispose the now session-owned scene claim), nor starve later
            // subscribers that also own session-scoped state.
            SafeEvent.Invoke(
                SessionChanged,
                session,
                ex => logger.Error(ex, "A SessionChanged subscriber failed."));
            var message = "Loaded " + world.Name + " [" + world.Id + "] with " + gamemode.Name
                + " [" + gamemode.Id + "] via " + mode + " in scene '" + sceneName + "'.";
            logger.Info("World session started: " + message);
            return WorldLoadResult.Success(session, message);
        }

        private void ReleasePendingSceneClaim()
        {
            var claim = pendingSceneClaim;
            pendingSceneClaim = null;
            claim?.Dispose();
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
            arenaRoot = new GameObject("TopiaForge Worlds - Open Sandbox");
            UnityEngine.Object.DontDestroyOnLoad(arenaRoot);

            SandboxArenaBuilder.Build(arenaRoot, center, logger);

            // HDRP has no default sky/exposure/tonemapping; without a global Volume the arena looks washed out.
            arenaProfile = HdrpEnvironment.Apply(arenaRoot, logger);
            logger.Info("Built open sandbox arena.");
        }
    }
}
