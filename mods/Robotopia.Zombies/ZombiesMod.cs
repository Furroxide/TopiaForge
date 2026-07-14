using System;
using System.Reflection;
using Robotopia.Mods;

namespace Robotopia.Zombies
{
    public sealed class ZombiesMod : IRobotopiaMod
    {
        public const string GamemodeId = "robotopia.zombies.survival";
        private const string MenuEntryId = "robotopia.zombies.menu";

        private IModContext? context;
        private ZombiesConfig? config;
        private IWorldGamemodeService? worlds;
        private ZombiesController? controller;
        private IDisposable? restartPauseAction;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            config = context.LoadConfig(new ZombiesConfig());
            config.Normalize();
            context.SaveConfig(config);

            worlds = context.GetService<IWorldGamemodeService>();
            if (worlds == null)
            {
                context.Logger.Warn("Robotopia Worlds service is not available; Zombies cannot register its gamemode.");
                return;
            }

            worlds.RegisterGamemode(new GamemodeDefinition(
                GamemodeId,
                "Zombies",
                "Survive escalating waves of infected robots with a built-in zapper."));
            worlds.RegisterMenuEntry(new GamemodeMenuEntry(
                MenuEntryId,
                "Zombies",
                "Survive escalating waves of infected robots with a built-in zapper.",
                GamemodeId,
                config.TargetWorldId));
            TryWriteCatalog(worlds, context.Logger);

            worlds.SessionChanged += OnSessionChanged;
            worlds.SessionEnded += OnSessionEnded;
            if (worlds.CurrentSession != null)
            {
                OnSessionChanged(worlds.CurrentSession);
            }

            context.Update += OnUpdate;
            context.Logger.Info("Zombies gamemode registered.");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.Update -= OnUpdate;
            }

            if (worlds != null)
            {
                worlds.SessionChanged -= OnSessionChanged;
                worlds.SessionEnded -= OnSessionEnded;
                if (worlds is IWorldRegistrationService registrations)
                {
                    registrations.UnregisterMenuEntry(MenuEntryId);
                    registrations.UnregisterGamemode(GamemodeId);
                }
            }

            StopController();
            worlds = null;
            config = null;
            context = null;
        }

        private void OnUpdate(float deltaTime)
        {
            controller?.Update(deltaTime);
        }

        private void OnSessionChanged(WorldSession session)
        {
            if (!string.Equals(session.GamemodeId, GamemodeId, StringComparison.OrdinalIgnoreCase))
            {
                StopController();
                return;
            }

            if (context == null || config == null)
            {
                return;
            }

            StopController();
            controller = new ZombiesController(context, config);
            // Route the controller's self-terminating exit (game-over "RETURN TO MENU") through the Worlds
            // session so the provider also clears CurrentSession/arena; its SessionEnded event then reaches
            // OnSessionEnded below, which stops the controller. The direct StopController after it is the
            // fallback for a session the provider no longer tracks (EndSession no-ops). Re-entrancy is safe:
            // EndSession clears the session before firing, and StopController/Dispose are guarded.
            controller.SessionEnded = () =>
            {
                worlds?.EndSession(WorldSessionEndReason.EndedByGamemode);
                StopController();
            };
            controller.Start(session);

            // Surface a confirmed run restart in the QwUi pause companion while our session runs. The vanilla exit
            // button needs no interceptor: the default (end the session, then exit) is exactly what we want.
            var pauseMenu = context.GetService<IWorldPauseMenuService>();
            restartPauseAction = pauseMenu?.RegisterAction(new WorldPauseAction(
                "robotopia.zombies.restart",
                "RESTART RUN",
                () => controller?.Restart(),
                closePauseMenu: true,
                order: 0,
                destructive: true));
        }

        private void OnSessionEnded(WorldSessionEnd end)
        {
            StopController();
        }

        private void StopController()
        {
            restartPauseAction?.Dispose();
            restartPauseAction = null;
            controller?.Dispose();
            controller = null;
        }

        private static void TryWriteCatalog(IWorldGamemodeService worlds, IModLogger logger)
        {
            try
            {
                var method = worlds.GetType().GetMethod(
                    "WriteCatalog",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                method?.Invoke(worlds, Array.Empty<object>());
            }
            catch (Exception ex)
            {
                logger.Debug("Zombies could not refresh the Worlds catalog: " + ex.Message);
            }
        }
    }
}
