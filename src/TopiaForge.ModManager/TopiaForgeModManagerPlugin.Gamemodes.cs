using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        // Hold the launch until the game has actually reached the menu, so the gamemode's scene load is a
        // clean transition from the menu rather than a race against the boot sequence.
        private const float WorldLaunchMaxWaitSeconds = 12f;

        private WorldLaunchIntent? pendingWorldLaunch;
        private float pendingWorldLaunchWait;

        public IWorldGamemodeService? GetWorldService()
        {
            return runtime?.GetService<IWorldGamemodeService>();
        }

        /// <summary>
        /// The manager's remembered world/gamemode selection. It lives in the manager's own state file:
        /// the Worlds mod owns its config document, and having two processes merge into that one file is
        /// exactly what used to discard the player's choice without a trace.
        /// </summary>
        public WorldLaunchSettings ReadWorldLaunchSettings()
        {
            return state?.WorldLaunch ?? new WorldLaunchSettings();
        }

        public void SaveWorldLaunchSettings(WorldLaunchSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            settings.LoadMode = WorldLaunchSettings.NormalizeLoadMode(settings.LoadMode);
            state.WorldLaunch = settings;
            SaveState();
        }

        /// <summary>
        /// Arms the launcher's one-shot "start this gamemode" instruction, or the remembered selection
        /// when the game was started without the launcher. Mods have all loaded by the time this runs,
        /// so every gamemode -- including one contributed by a mod that loads after Worlds -- is already
        /// registered. See <see cref="WorldLaunchArming"/> for why "play normally" is a command rather
        /// than a silence.
        /// </summary>
        private void ArmWorldLaunch()
        {
            var intent = WorldLaunchArming.Resolve(launchProfile, state?.WorldLaunch);
            if (intent == null)
            {
                return;
            }

            pendingWorldLaunch = intent;
            pendingWorldLaunchWait = WorldLaunchMaxWaitSeconds;
            managerLogger.Info("Launch intent: gamemode '" + intent.GamemodeId + "'"
                + (string.IsNullOrEmpty(intent.WorldId) ? " in its default world" : " in world '" + intent.WorldId + "'")
                + " (" + intent.LoadMode + ").");
        }

        /// <summary>
        /// Waits for the menu, then starts the armed gamemode. This lives in the manager rather than in
        /// the Worlds mod because the manager is what receives the instruction -- routing it back out
        /// through a mod's config file is the coupling that broke in the first place -- and because the
        /// manager can report a concrete reason when there is no world service to launch into.
        /// </summary>
        private void UpdatePendingWorldLaunch(float deltaTime)
        {
            var intent = pendingWorldLaunch;
            if (intent == null)
            {
                return;
            }

            pendingWorldLaunchWait -= deltaTime;
            var activeScene = SceneManager.GetActiveScene().name;
            var atMenu = GameScenes.IsMainMenuScene(activeScene);
            if (!atMenu && pendingWorldLaunchWait > 0f)
            {
                return;
            }

            pendingWorldLaunch = null;

            // Timed out without reaching the menu (a slow boot, or a renamed menu scene). Only launch into
            // a real gameplay-capable scene; never build an arena over a boot/loader/splash scene, which
            // would race the very boot sequence the wait exists to avoid.
            if (!atMenu && GameScenes.IsNonGameplayScene(activeScene))
            {
                managerLogger.Warn("Launch intent skipped: the menu was never reached (active scene '"
                    + activeScene + "').");
                return;
            }

            var service = GetWorldService();
            if (service == null)
            {
                managerLogger.Warn("Launch intent skipped: no world/gamemode service is available. "
                    + "Enable the TopiaForge Worlds mod, or check whether startup recovery disabled it.");
                return;
            }

            StartIntent(service, intent);
        }

        private void StartIntent(IWorldGamemodeService service, WorldLaunchIntent intent)
        {
            try
            {
                var route = WorldLaunchRouter.Resolve(
                    service.Worlds,
                    service.MenuEntries,
                    intent.WorldId,
                    intent.GamemodeId);
                if (!route.Resolved)
                {
                    managerLogger.Warn("Launch intent failed: " + route.Warning);
                    return;
                }

                if (!string.IsNullOrEmpty(route.Warning))
                {
                    managerLogger.Warn(route.Warning);
                }

                // WorldsService completes synchronously; the task is drained rather than waited on so a
                // provider that ever goes truly async cannot block the frame or leak an unobserved task.
                var pending = service.LoadAsync(new WorldLoadRequest(
                    route.WorldId,
                    intent.GamemodeId,
                    WorldLoadPriority.Automatic,
                    intent.PreferSceneReplacement,
                    intent.AllowAdditiveFallback));
                pending.ContinueWith(
                    task => ReportIntentOutcome(intent, task),
                    TaskContinuationOptions.ExecuteSynchronously);
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Launch intent failed for gamemode '" + intent.GamemodeId + "'.");
            }
        }

        private void ReportIntentOutcome(WorldLaunchIntent intent, Task<OperationResult<WorldSession>> task)
        {
            if (task.IsFaulted)
            {
                managerLogger.Error(task.Exception!, "Launch intent failed for gamemode '" + intent.GamemodeId + "'.");
                return;
            }

            // Reading Result on a cancelled task throws, and this runs inside a continuation where that
            // becomes an unobserved exception rather than anything anyone sees. Shutdown during a load is
            // an ordinary outcome, so report it as one.
            if (task.IsCanceled)
            {
                managerLogger.Info("Launch intent for gamemode '" + intent.GamemodeId
                    + "' was cancelled before it finished.");
                return;
            }

            var result = task.Result;
            if (result.Succeeded)
            {
                managerLogger.Info("Launch intent started gamemode '" + intent.GamemodeId + "'.");
            }
            else
            {
                managerLogger.Warn("Launch intent failed for gamemode '" + intent.GamemodeId + "': "
                    + result.ErrorMessage);
            }
        }

        public async Task<(bool Ok, string Message)> LaunchGamemode(string entryId)
        {
            var service = GetWorldService();
            if (service == null)
            {
                return (false, "World/gamemode service unavailable. Enable the TopiaForge Worlds mod.");
            }

            try
            {
                var result = await service.LaunchMenuEntryAsync(entryId);
                var message = result.Succeeded
                    ? "Launched gamemode entry '" + entryId + "'."
                    : result.ErrorMessage;
                managerLogger.Info("Gamemode launch '" + entryId + "': " + message);
                return (result.Succeeded, message);
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Failed to launch gamemode '" + entryId + "'.");
                return (false, "Failed to launch: " + ex.Message);
            }
        }

        public async Task<(bool Ok, string Message)> LaunchGamemodeSelection(
            string entryId,
            string worldId,
            string gamemodeId,
            string loadMode)
        {
            var service = GetWorldService();
            if (service == null)
            {
                return (false, "World/gamemode service unavailable. Enable the TopiaForge Worlds mod.");
            }

            try
            {
                var world = service.Worlds.FirstOrDefault(item =>
                    string.Equals(item.Id, worldId, StringComparison.OrdinalIgnoreCase));
                if (world == null)
                {
                    return (false, "Unknown world: " + worldId);
                }

                var gamemode = service.Gamemodes.FirstOrDefault(item =>
                    string.Equals(item.Id, gamemodeId, StringComparison.OrdinalIgnoreCase));
                if (gamemode == null)
                {
                    return (false, "Unknown gamemode: " + gamemodeId);
                }

                var resolvedLoadMode = WorldLaunchSettings.ReconcileLoadMode(
                    world.SupportsSceneReplacement,
                    world.SupportsAdditiveArena,
                    loadMode);
                var existing = ReadWorldLaunchSettings();
                var settings = new WorldLaunchSettings
                {
                    SelectedWorldId = worldId,
                    SelectedGamemodeId = gamemodeId,
                    LoadMode = resolvedLoadMode,
                    AutoLoadOnStart = existing.AutoLoadOnStart,
                    AllowAdditiveFallback = existing.AllowAdditiveFallback
                };
                SaveWorldLaunchSettings(settings);

                var result = await service.LoadAsync(new WorldLoadRequest(
                    worldId,
                    gamemodeId,
                    preferSceneReplacement: settings.PreferSceneReplacement,
                    allowAdditiveFallback: settings.AllowAdditiveFallback));
                var message = result.Succeeded
                    ? "Launched '" + gamemode.Name + "' in '" + world.Name + "'."
                    : result.ErrorMessage;
                managerLogger.Info("Gamemode launch '" + entryId + "' world '" + world.Name + "' [" + world.Id
                    + "] gamemode '" + gamemode.Name + "' [" + gamemode.Id + "] loadMode '" + settings.LoadMode
                    + "': " + message);
                return (result.Succeeded, message);
            }
            catch (Exception ex)
            {
                managerLogger.Error(ex, "Failed to launch gamemode '" + entryId + "' for world '" + worldId + "'.");
                return (false, "Failed to launch: " + ex.Message);
            }
        }
    }
}
