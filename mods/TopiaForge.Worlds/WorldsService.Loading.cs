using TopiaForge.Mods.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    public sealed partial class WorldsService
    {
        public Task<OperationResult<WorldSession>> LoadAsync(
            WorldLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<WorldSession>.Failure(
                    ModErrorCode.Cancelled,
                    "World load was cancelled."));
            }

            return Task.FromResult(ToOperation(Load(request)));
        }

        public Task<OperationResult<WorldSession>> LaunchMenuEntryAsync(
            string entryId,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<WorldSession>.Failure(
                    ModErrorCode.Cancelled,
                    "World launch was cancelled."));
            }

            return Task.FromResult(ToOperation(LaunchMenuEntry(entryId)));
        }

        internal WorldLoadResult LaunchMenuEntry(string entryId)
        {
            return LaunchMenuEntry(
                entryId,
                preferSceneReplacement: true,
                allowAdditiveFallback: true,
                WorldLoadPriority.UserInitiated);
        }

        // Overload that threads the caller's configured load mode through to Load, so the launcher's "Load mode"
        // selection is honoured on the menu-entry path instead of being structurally dropped.
        internal WorldLoadResult LaunchMenuEntry(string entryId, bool preferSceneReplacement, bool allowAdditiveFallback)
        {
            return LaunchMenuEntry(
                entryId,
                preferSceneReplacement,
                allowAdditiveFallback,
                WorldLoadPriority.UserInitiated);
        }

        internal WorldLoadResult LaunchMenuEntry(
            string entryId,
            bool preferSceneReplacement,
            bool allowAdditiveFallback,
            WorldLoadPriority priority)
        {
            if (disposed)
            {
                return WorldLoadResult.Fail("World service is disposed.");
            }

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

            return Load(new WorldLoadRequest(
                worldId,
                entry.GamemodeId,
                priority,
                preferSceneReplacement,
                allowAdditiveFallback));
        }

        internal WorldLoadResult Load(WorldLoadRequest request)
        {
            if (disposed)
            {
                return WorldLoadResult.Fail("World service is disposed.");
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

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

            // Do not supersede a dispatched scene load before its sceneLoaded/failure callback arrives. The
            // callback API can only identify the dispatch that failed; sceneLoaded itself has no generation
            // token, so allowing a second load here would let a late scene from the first dispatch resolve the
            // second transition and hide its eventual failure.
            if (transitionTracker.BlocksAdmission)
            {
                return transitionTracker.IsQuarantined
                    ? WorldLoadResult.Fail(
                        "The previous world load is still being retired. Wait for its scene change before retrying; "
                        + "if it never finishes, restart the game.")
                    : WorldLoadResult.Fail("A world is already loading; please wait.");
            }

            // Debounce rapid re-launches so a second click does not race a second scene load against the
            // first one's in-flight async load (which would overwrite the static checkpoint override). Stamp
            // immediately so even a launch that ultimately fails still throttles repeated attempts.
            if (Time.realtimeSinceStartup - lastLaunchTime < 1.5f)
            {
                return WorldLoadResult.Fail("A world is already loading; please wait.");
            }

            // Resolve the route before claiming or mutating the active session. Most importantly, automatic
            // startup loads must be refused before LaunchLevel/LoadScene/LaunchOpenSandbox has any side effect.
            var hasCustomContent = customWorldContent.TryGetValue(world.Id, out var content);
            // Custom content always wins, including when paired with the Sandbox gamemode. Otherwise Sandbox is
            // a story-free creator mode regardless of which catalog world the UI happened to retain.
            var useOpenSandbox = !hasCustomContent
                && (string.Equals(world.Id, OpenSandboxWorldId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(gamemode.Id, SandboxGamemodeId, StringComparison.OrdinalIgnoreCase));
            var hasCheckpoint = worldCheckpoints.TryGetValue(world.Id, out var checkpoint);
            var useSceneReplacement = !hasCustomContent && !useOpenSandbox
                && (hasCheckpoint
                    || (world.SupportsSceneReplacement
                        && !string.IsNullOrWhiteSpace(world.SceneName)
                        && (request.PreferSceneReplacement || !world.SupportsAdditiveArena)));
            var useAdditiveArena = !hasCustomContent && !useOpenSandbox && !useSceneReplacement
                && world.SupportsAdditiveArena
                && (!request.PreferSceneReplacement || request.AllowAdditiveFallback);

            if (!hasCustomContent && !useOpenSandbox && !useSceneReplacement && !useAdditiveArena)
            {
                return WorldLoadResult.Fail("World cannot be loaded with the requested mode: " + world.Name);
            }

            var targetScene = hasCustomContent || useOpenSandbox
                ? GameLevelBridge.SandboxSceneName
                : useSceneReplacement
                    ? world.SceneName
                    : SceneManager.GetActiveScene().name;

            IInternalSceneTransitionLease? launchClaim = null;

            {
                var claimResult = sceneTransitions.Acquire(
                    targetScene,
                    request.Priority == WorldLoadPriority.Automatic,
                    "world load");
                if (!claimResult.TryGetValue(out launchClaim))
                {
                    return WorldLoadResult.Fail(claimResult.ErrorMessage, claimResult.ErrorCode);
                }
            }

            lastLaunchTime = Time.realtimeSinceStartup;
            try
            {
                // A new approved launch replaces any live session: end it properly (arena teardown +
                // SessionEnded) before the incoming scene dispatch and SessionChanged notification.
                EndSession(WorldSessionEndReason.Superseded);

                WorldLoadResult result;
                if (hasCustomContent)
                {
                    result = LoadCustomWorld(world, gamemode, content!, launchClaim);
                }
                else if (useOpenSandbox)
                {
                    result = LoadOpenSandbox(world, gamemode, launchClaim);
                }
                else if (hasCheckpoint)
                {
                    UnloadArena();
                    var transition = transitionTracker.Begin(
                        Time.realtimeSinceStartup,
                        world.SceneName);
                    var dispatched = levelBridge.LaunchLevel(
                        checkpoint!, message => transitionTracker.ReportFailure(transition, message), launchClaim?.Transitions);
                    if (dispatched.Accepted)
                    {
                        result = StartSession(world, gamemode, "gameScene", world.SceneName, launchClaim);
                    }
                    else
                    {
                        transitionTracker.Cancel(transition);
                        if (!dispatched.CanFallback) return WorldLoadResult.Fail(dispatched.ErrorMessage, dispatched.ErrorCode);
                        logger.Warn("Worlds could not launch " + world.Name + " via the game loader; falling back.");
                        result = LoadSceneReplacement(world, gamemode, launchClaim);
                    }
                }
                else if (useSceneReplacement)
                {
                    result = LoadSceneReplacement(world, gamemode, launchClaim);
                }
                else
                {
                    BuildArena();
                    result = StartSession(
                        world,
                        gamemode,
                        "additiveArena",
                        SceneManager.GetActiveScene().name,
                        launchClaim);
                }

                if (result.Ok)
                {
                    // StartSession accepted the launch.
                    launchClaim = null;
                }

                return result;
            }
            finally
            {
                // Creation/dispatch failures must never leave an automatic transition permanently blocked.
                launchClaim?.Dispose();
            }
        }

        private WorldLoadResult LoadSceneReplacement(
            WorldDefinition world,
            GamemodeDefinition gamemode,
            IInternalSceneTransitionLease? launchClaim)
        {
            if (!world.SupportsSceneReplacement || string.IsNullOrWhiteSpace(world.SceneName))
            {
                return WorldLoadResult.Fail("World has no replacement scene: " + world.Name);
            }

            UnloadArena();
            var transition = transitionTracker.Begin(
                Time.realtimeSinceStartup,
                world.SceneName);
            var dispatched = levelBridge.LoadSceneByName(world.SceneName,
                message => transitionTracker.ReportFailure(transition, message), launchClaim?.Transitions);
            if (!dispatched.Accepted)
            {
                if (!dispatched.CanFallback)
                {
                    transitionTracker.Cancel(transition);
                    return WorldLoadResult.Fail(dispatched.ErrorMessage, dispatched.ErrorCode);
                }
                try
                {
                    // Last-resort fallback. First-party scenes are often addressable/streamed and not in
                    // build settings, so this can throw; degrade gracefully instead of crashing the game.
                    var direct = levelBridge.LoadSceneDirect(world.SceneName,
                        message => transitionTracker.ReportFailure(transition, message), launchClaim?.Transitions);
                    if (!direct.Accepted)
                    {
                        transitionTracker.Cancel(transition);
                        return WorldLoadResult.Fail(direct.ErrorMessage, direct.ErrorCode);
                    }
                }
                catch (Exception ex)
                {
                    transitionTracker.Cancel(transition);
                    logger.Warn("Worlds could not load scene '" + world.SceneName + "': " + ex.Message);
                    return WorldLoadResult.Fail("Could not load world scene: " + world.Name);
                }
            }

            return StartSession(world, gamemode, "sceneReplacement", world.SceneName, launchClaim);
        }

        // Launches the clean Open Sandbox arena: load the game's story-free play scene (which spawns a real
        // player), then build the arena geometry around that spawn once the async scene load completes.
        private WorldLoadResult LoadOpenSandbox(
            WorldDefinition selectedWorld,
            GamemodeDefinition gamemode,
            IInternalSceneTransitionLease? launchClaim)
        {
            UnloadArena();

            // Report the session as the Open Sandbox world (so the result message and SessionChanged reflect what
            // actually loaded), falling back to the requested world if the built-in sandbox world is missing.
            var sandboxWorld = worlds.FirstOrDefault(item =>
                string.Equals(item.Id, OpenSandboxWorldId, StringComparison.OrdinalIgnoreCase)) ?? selectedWorld;

            var transition = transitionTracker.Begin(
                Time.realtimeSinceStartup,
                GameLevelBridge.SandboxSceneName);
            var dispatched = levelBridge.LaunchOpenSandbox(
                message => transitionTracker.ReportFailure(transition, message), launchClaim?.Transitions);
            if (dispatched.Accepted)
            {
                ArmSandboxArena();
                return StartSession(
                    sandboxWorld,
                    gamemode,
                    "openSandbox",
                    GameLevelBridge.SandboxSceneName,
                    launchClaim);
            }

            transitionTracker.Cancel(transition);
            if (!dispatched.CanFallback) return WorldLoadResult.Fail(dispatched.ErrorMessage, dispatched.ErrorCode);

            // The game's play scene could not be loaded (missing symbol). A current-scene arena remains useful in
            // an existing gameplay scene, but creating one over a menu/boot/loader would leave a bogus session and
            // gameplay objects inside the shell scene.
            var activeScene = SceneManager.GetActiveScene().name;
            if (!OpenSandboxFallbackPolicy.CanBuildInScene(activeScene, KnownGameplaySceneNames()))
            {
                var displayScene = string.IsNullOrWhiteSpace(activeScene) ? "<unknown>" : activeScene;
                logger.Warn("Worlds could not load the game sandbox scene and refused to build an arena over "
                    + "non-gameplay scene '" + displayScene + "'.");
                return WorldLoadResult.Fail(
                    "Open Sandbox could not start because the sandbox play scene is unavailable while '"
                    + displayScene + "' is active.");
            }

            logger.Warn("Worlds could not load the game sandbox scene; building the arena over gameplay scene '"
                + activeScene + "'.");
            BuildArena();
            return StartSession(
                sandboxWorld,
                gamemode,
                "additiveArena",
                activeScene,
                launchClaim);
        }

        private IEnumerable<string?> KnownGameplaySceneNames()
        {
            // The dedicated play scene is valid even when its reflective launch bridge was the part that failed.
            yield return GameLevelBridge.SandboxSceneName;

            // Registered first-party and mod-provided worlds form the complete active runtime catalog.
            foreach (var world in worlds)
            {
                yield return world.SceneName;
            }

            // Preserve a safe fallback after Worlds is reloaded inside the currently active built-in level: normal
            // discovery skips the active scene to avoid duplicate menu entries, but build settings still prove it
            // is a real shipped scene rather than an arbitrary/unknown name.
            for (var index = 0; index < SceneManager.sceneCountInBuildSettings; index++)
            {
                var scenePath = SceneUtility.GetScenePathByBuildIndex(index);
                yield return Path.GetFileNameWithoutExtension(scenePath);
            }
        }

        // Arms a one-shot: when the sandbox play scene finishes its async load, build the arena around the player.
        // The persistent OnSceneLoaded hook (registered in the constructor) picks it up.
        private void ArmSandboxArena()
        {
            sandboxArenaPending = true;
        }

        // Launches a mod-provided custom world. The SDK content factory is invoked only after the clean play
        // scene arrives, so all assets/entities are created in their intended scene and remain opaque here.
        private WorldLoadResult LoadCustomWorld(
            WorldDefinition world,
            GamemodeDefinition gamemode,
            ICustomWorldContent content,
            IInternalSceneTransitionLease? launchClaim)
        {
            UnloadArena();

            var transition = transitionTracker.Begin(
                Time.realtimeSinceStartup,
                GameLevelBridge.SandboxSceneName);
            var dispatched = levelBridge.LaunchOpenSandbox(
                message => transitionTracker.ReportFailure(transition, message), launchClaim?.Transitions);
            if (!dispatched.Accepted)
            {
                transitionTracker.Cancel(transition);
                return WorldLoadResult.Fail(dispatched.ErrorMessage, dispatched.ErrorCode);
            }

            pendingCustomWorld = new PendingCustomWorld(world, content);
            sandboxArenaPending = false;
            return StartSession(
                world,
                gamemode,
                "customWorld",
                GameLevelBridge.SandboxSceneName,
                launchClaim);
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
    }
}
