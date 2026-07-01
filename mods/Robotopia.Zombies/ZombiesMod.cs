using System;
using System.Reflection;
using Robotopia.Mods;

namespace Robotopia.Zombies
{
    public sealed class ZombiesMod : IRobotopiaMod
    {
        public const string GamemodeId = "robotopia.zombies.survival";

        private IModContext? context;
        private ZombiesConfig? config;
        private IWorldGamemodeService? worlds;
        private ZombiesController? controller;

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
                "robotopia.zombies.menu",
                "Zombies",
                "Survive escalating waves of infected robots with a built-in zapper.",
                GamemodeId,
                config.TargetWorldId));
            TryWriteCatalog(worlds, context.Logger);

            worlds.SessionChanged += OnSessionChanged;
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
            controller.SessionEnded = StopController;
            controller.Start(session);
        }

        private void StopController()
        {
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
