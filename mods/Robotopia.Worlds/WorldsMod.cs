using System;
using Robotopia.Mods;
using Robotopia.Mods.UnityUi;
using UnityEngine.SceneManagement;

namespace Robotopia.Worlds
{
    public sealed class WorldsMod : IRobotopiaMod
    {
        // We hold the auto-load until the game has actually reached the menu so the gamemode's scene load is
        // a clean transition from the menu, not a race against the boot sequence.
        private const float AutoLoadMaxWaitSeconds = 12f;

        private IModContext? context;
        private WorldsConfig? config;
        private WorldsService? service;
        private PauseMenuBridge? pauseBridge;
        private UiHost? ui;
        private bool pendingAutoLoad;
        private float autoLoadWait;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            config = context.LoadConfig(new WorldsConfig());
            context.SaveConfig(config);

            service = new WorldsService(context.Logger, context.Paths.DataPath);
            service.EndSessionOnMenuScene = config.EndSessionOnMenuScene;
            var sceneCoordinator = context.GetService<ISceneCoordinator>();
            if (sceneCoordinator != null)
            {
                service.AttachSceneCoordinator(sceneCoordinator, context.ModId);
            }

            service.DiscoverBuiltIns();
            // Pin the entry to the Open Sandbox world: world routing is keyed on the world id (a blank id
            // would resolve to the first checkpoint level, i.e. the campaign tutorial), and an explicit world
            // selection with the Sandbox gamemode is honoured. The Robotopia.Sandbox mod layers the actual
            // creator gameplay (spawn menu, tools) onto this session.
            service.RegisterMenuEntry(new GamemodeMenuEntry(
                "robotopia.worlds.sandbox.menu",
                "Sandbox",
                "Freeform creator sandbox: an open arena with a spawn menu for props and robots.",
                WorldsService.SandboxGamemodeId,
                WorldsService.OpenSandboxWorldId));
            service.WriteCatalog();

            ui = QwUi.For(context);
            pauseBridge = new PauseMenuBridge(service, context.Logger, ui, config.InterceptPauseMenu);

            var registry = context.GetService<IModServiceRegistry>();
            registry?.Register<IWorldGamemodeService>(context.ModId, service);
            registry?.Register<IWorldPauseMenuService>(context.ModId, pauseBridge);

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

            pauseBridge?.Dispose();
            pauseBridge = null;
            ui?.Dispose();
            ui = null;
            service?.Dispose();
            service = null;
            config = null;
            context = null;
            pendingAutoLoad = false;
        }

        private void OnUpdate(float deltaTime)
        {
            service?.UpdateTransition();
            pauseBridge?.Update(deltaTime);

            if (!pendingAutoLoad || service == null || config == null || context == null)
            {
                return;
            }

            // Wait until the menu is the active scene (so launching replaces the menu cleanly), with a timeout
            // fallback in case the menu scene is named differently in a future build.
            autoLoadWait -= deltaTime;
            var activeScene = SceneManager.GetActiveScene().name;
            var atMenu = GameScenes.IsMainMenuScene(activeScene);
            if (!atMenu && autoLoadWait > 0f)
            {
                return;
            }

            pendingAutoLoad = false;

            // Timed out without reaching the menu (a slow boot, or a renamed menu scene). Only launch into a
            // real gameplay-capable scene; never build an arena over / load a level into a boot/loader/splash
            // scene, which would race the boot sequence the wait exists to avoid.
            if (!atMenu && GameScenes.IsNonGameplayScene(activeScene))
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
            // Honour any explicitly selected, registered world directly, including Open Sandbox. WorldsService.Load
            // routes Open Sandbox through the clean UgcPlay scene; falling back through a blank gamemode menu entry
            // can instead choose the first story checkpoint, which may be the tutorial.
            var route = WorldAutoLoadRouter.Resolve(
                service.Worlds,
                service.MenuEntries,
                config.SelectedWorldId,
                config.SelectedGamemodeId,
                config.PreferSceneReplacement,
                config.AllowAdditiveFallback);

            if (!string.IsNullOrWhiteSpace(route.Warning))
            {
                logger.Warn(route.Warning);
            }

            if (route.Kind == WorldAutoLoadRouteKind.LaunchMenuEntry)
            {
                return service.LaunchMenuEntry(
                    route.MenuEntryId,
                    route.PreferSceneReplacement,
                    route.AllowAdditiveFallback,
                    route.Priority);
            }

            return service.Load(route.Request!);
        }
    }
}
