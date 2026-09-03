using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.Worlds
{
    public sealed class WorldsMod : TopiaForgeMod
    {
        // Schema 2 dropped the world/gamemode selection members: choosing what to launch is the
        // launcher's and the manager overlay's job now, and this document is provider-owned again. The
        // removed members simply vanish through the DTO round-trip, so the migration has nothing to
        // reshape -- but it must exist, or the loader refuses to read every schema-1 document on disk.
        private static readonly ConfigDefinition<WorldsConfig> ConfigContract =
            new ConfigDefinition<WorldsConfig>(
                2,
                () => new WorldsConfig(),
                migrate: (storedVersion, value) => OperationResult<WorldsConfig>.Success(value));
        private WorldsConfig? config;
        private WorldsService? service;
        private PauseMenuBridge? pauseBridge;
        private UiHost? ui;

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
            service.EnableLocalWorlds = config.EnableLocalWorlds;
            service.LocalWorldFolder = config.LocalWorldFolder;
            service.DiscoverBuiltIns();
            // Pin the entry to the Open Sandbox world: world routing is keyed on the world id, and a blank
            // id would resolve to the first checkpoint level, which is the campaign tutorial. The creator
            // sandbox entry moved to the TopiaForge.Sandbox package, which owns that gameplay.
            service.RegisterMenuEntry(new GamemodeMenuEntry(
                FreePlayGamemode.MenuEntryId,
                "Free Play",
                "Explore any installed world with no gameplay rules.",
                FreePlayGamemode.FreePlayGamemodeId,
                WorldsService.OpenSandboxWorldId));
            service.MarkCatalogDirty();

            ui = TopiaForgeUi.For(Context);
            Context.Lifetime.Track(ui);
            pauseBridge = new PauseMenuBridge(service, Context.Logger, ui, config.InterceptPauseMenu);
            Context.Lifetime.Track(pauseBridge);

            RegisterExtension<IWorldGamemodeService>(service);
            RegisterExtension<IWorldPauseMenuService>(pauseBridge);

            Context.Events.SubscribeUpdate(OnUpdate);
            Context.Logger.Info("TopiaForge Worlds loaded with " + service.Worlds.Count + " worlds and " + service.Gamemodes.Count + " gamemodes.");
        }

        private void OnUpdate(float deltaTime)
        {
            service?.UpdateTransition();
            pauseBridge?.Update(deltaTime);
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
