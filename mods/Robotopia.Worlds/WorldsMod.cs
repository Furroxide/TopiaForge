using System;
using System.Linq;
using Robotopia.Mods;
using UnityEngine.SceneManagement;

namespace Robotopia.Worlds
{
    public sealed class WorldsMod : IRobotopiaMod
    {
        // The verified main-menu scene. We hold the auto-load until the game has actually reached the menu so
        // the gamemode's scene load is a clean transition from the menu, not a race against the boot sequence.
        private const string MenuSceneName = "TestCityStartMenu";
        private const float AutoLoadMaxWaitSeconds = 12f;

        private IModContext? context;
        private WorldsConfig? config;
        private WorldsService? service;
        private bool pendingAutoLoad;
        private float autoLoadWait;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            config = context.LoadConfig(new WorldsConfig());
            context.SaveConfig(config);

            service = new WorldsService(context.Logger, context.Paths.DataPath);
            service.DiscoverBuiltIns();
            // Leave the world id blank so the service resolves a real, checkpoint-backed level in correct HDRP
            // play state. Pinning OpenSandbox here builds an additive arena over whatever scene is active; when
            // launched/auto-launched from the menu that strands the player on the home screen (the documented
            // 2026-06-28 regression). The bare arena remains the last-resort fallback inside ResolveWorldId.
            service.RegisterMenuEntry(new GamemodeMenuEntry(
                "robotopia.worlds.sandbox.menu",
                "Sandbox",
                "Freeform creator sandbox in a real level.",
                WorldsService.SandboxGamemodeId));
            service.WriteCatalog();

            var registry = context.GetService<IModServiceRegistry>();
            registry?.Register<IWorldGamemodeService>(context.ModId, service);

            pendingAutoLoad = config.AutoLoadOnStart;
            autoLoadWait = AutoLoadMaxWaitSeconds;
            context.Update += OnUpdate;
            context.Logger.Info("Robotopia Worlds loaded with " + service.Worlds.Count + " worlds and " + service.Gamemodes.Count + " gamemodes.");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.Update -= OnUpdate;
                context.GetService<IModServiceRegistry>()?.UnregisterOwner(context.ModId);
            }

            service?.Dispose();
            service = null;
            config = null;
            context = null;
            pendingAutoLoad = false;
        }

        private void OnUpdate(float deltaTime)
        {
            if (!pendingAutoLoad || service == null || config == null || context == null)
            {
                return;
            }

            // Wait until the menu is the active scene (so launching replaces the menu cleanly), with a timeout
            // fallback in case the menu scene is named differently in a future build.
            autoLoadWait -= deltaTime;
            var activeScene = SceneManager.GetActiveScene().name;
            var atMenu = string.Equals(activeScene, MenuSceneName, StringComparison.OrdinalIgnoreCase);
            if (!atMenu && autoLoadWait > 0f)
            {
                return;
            }

            pendingAutoLoad = false;

            // Timed out without reaching the menu (a slow boot, or a renamed menu scene). Only launch into a
            // real gameplay-capable scene; never build an arena over / load a level into a boot/loader/splash
            // scene, which would race the boot sequence the wait exists to avoid.
            if (!atMenu && WorldsService.IsNonGameplayScene(activeScene))
            {
                context.Logger.Warn("Auto-launch skipped: menu scene was never reached (active scene '" + activeScene + "').");
                return;
            }

            var result = AutoLoad(service, config, context.Logger);
            if (result.Ok)
            {
                context.Logger.Info("Auto-launch: " + result.Message);
            }
            else
            {
                context.Logger.Warn("Auto-launch failed: " + result.Message);
            }
        }

        private static WorldLoadResult AutoLoad(WorldsService service, WorldsConfig config, IModLogger logger)
        {
            // If the user pinned a specific, still-registered world (anything other than the default sandbox),
            // honour it directly with the configured load mode. The sandbox default is deliberately NOT launched
            // directly from here: at the menu it would build an additive arena over the menu and strand the
            // player on the home screen, so it routes through the gamemode's menu entry below, which resolves a
            // real checkpoint-backed level. A stale/unknown world id falls through to the resilient menu path.
            var pinsWorld = !string.IsNullOrWhiteSpace(config.SelectedWorldId)
                && !string.Equals(config.SelectedWorldId, WorldsService.OpenSandboxWorldId, StringComparison.OrdinalIgnoreCase);
            var pinnedWorldRegistered = pinsWorld
                && service.Worlds.Any(world => string.Equals(world.Id, config.SelectedWorldId, StringComparison.OrdinalIgnoreCase));
            if (pinnedWorldRegistered)
            {
                return service.Load(new WorldLoadRequest(
                    config.SelectedWorldId,
                    config.SelectedGamemodeId,
                    config.PreferSceneReplacement,
                    config.AllowAdditiveFallback));
            }

            if (pinsWorld)
            {
                // The config asks for a specific world that no longer exists in the registry (most often the
                // game's curated level list failed to load this run, so only the bare sandbox is available).
                // Surface that loudly instead of silently substituting a different world via the fallback below.
                logger.Warn("Auto-launch: configured world '" + config.SelectedWorldId
                    + "' is not registered (the level list may not have loaded); falling back to the gamemode's default world.");
            }

            // Prefer the configured gamemode's registered menu entry: that resolves a real, playable level and
            // loads it through the game's own loader (leaving the menu), exactly like the in-game Gamemodes
            // button, honouring the configured load mode. Fall back to the raw world/gamemode only as a default.
            foreach (var entry in service.MenuEntries)
            {
                if (string.Equals(entry.GamemodeId, config.SelectedGamemodeId, StringComparison.OrdinalIgnoreCase))
                {
                    return service.LaunchMenuEntry(entry.Id, config.PreferSceneReplacement, config.AllowAdditiveFallback);
                }
            }

            return service.Load(new WorldLoadRequest(
                config.SelectedWorldId,
                config.SelectedGamemodeId,
                config.PreferSceneReplacement,
                config.AllowAdditiveFallback));
        }
    }
}
