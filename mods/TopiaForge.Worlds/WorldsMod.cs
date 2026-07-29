using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    public sealed class WorldsMod : TopiaForgeMod
    {
        // We hold the auto-load until the game has actually reached the menu so the gamemode's scene load is
        // a clean transition from the menu, not a race against the boot sequence.
        private const float AutoLoadMaxWaitSeconds = 12f;

        private static readonly ConfigDefinition<WorldsConfig> ConfigContract =
            new ConfigDefinition<WorldsConfig>(1, () => new WorldsConfig());
        private WorldsConfig? config;
        private WorldsService? service;
        private PauseMenuBridge? pauseBridge;
        private UiHost? ui;
        private bool pendingAutoLoad;
        private float autoLoadWait;

        protected override void OnLoad()
        {
            var loaded = Context.Config.Load(ConfigContract);
            config = loaded.TryGetValue(out var value) ? value : new WorldsConfig();
            Context.Config.Save(ConfigContract, config);

            if (!(Context is IInternalSceneTransitionContext internalContext))
            {
                throw new InvalidOperationException("The loader did not provide its scene-transition gate.");
            }

            service = new WorldsService(
                Context.Logger,
                Context.Files,
                internalContext.SceneTransitions,
                Context.Lifetime.StoppingToken);
            // Track native scene hooks immediately so any later discovery/UI/config failure still releases them.
            Context.Lifetime.Track(service);
            service.EndSessionOnMenuScene = config.EndSessionOnMenuScene;
            service.DiscoverBuiltIns();
            // Pin the entry to the Open Sandbox world: world routing is keyed on the world id (a blank id
            // would resolve to the first checkpoint level, i.e. the campaign tutorial), and an explicit world
            // selection with the Sandbox gamemode is honoured. The TopiaForge.Sandbox mod layers the actual
            // creator gameplay (spawn menu, tools) onto this session.
            service.RegisterMenuEntry(new GamemodeMenuEntry(
                "io.github.furroxide.topiaforge.worlds.sandbox.menu",
                "Sandbox",
                "Freeform creator sandbox: an open arena with a spawn menu for props and robots.",
                WorldsService.SandboxGamemodeId,
                WorldsService.OpenSandboxWorldId));
            service.WriteCatalog();

            ui = TopiaForgeUi.For(Context);
            Context.Lifetime.Track(ui);
            pauseBridge = new PauseMenuBridge(service, Context.Logger, ui, config.InterceptPauseMenu);
            Context.Lifetime.Track(pauseBridge);

            RegisterExtension<IWorldGamemodeService>(service);
            RegisterExtension<IWorldPauseMenuService>(pauseBridge);

            pendingAutoLoad = config.AutoLoadOnStart;
            autoLoadWait = AutoLoadMaxWaitSeconds;
            Context.Events.SubscribeUpdate(OnUpdate);
            Context.Logger.Info("TopiaForge Worlds loaded with " + service.Worlds.Count + " worlds and " + service.Gamemodes.Count + " gamemodes.");
        }

        protected override void OnUnload()
        {
            pendingAutoLoad = false;
        }

        private void OnUpdate(float deltaTime)
        {
            service?.UpdateTransition();
            pauseBridge?.Update(deltaTime);

            if (!pendingAutoLoad || service == null || config == null)
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
                Context.Logger.Warn("Auto-launch skipped: menu scene was never reached (active scene '" + activeScene + "').");
                return;
            }

            var result = AutoLoad(service, config, Context.Logger);
            if (result.Ok)
            {
                Context.Logger.Info("Auto-launch: " + result.Message);
            }
            else
            {
                Context.Logger.Warn("Auto-launch failed: " + result.Message);
            }
        }

        private static WorldsService.WorldLoadResult AutoLoad(
            WorldsService service,
            WorldsConfig config,
            IModLogger logger)
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

        private void RegisterExtension<T>(T provider) where T : class
        {
            var registration = Context.Extensions.Register(provider);
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(registration.ErrorMessage);
            }
        }
    }
}
