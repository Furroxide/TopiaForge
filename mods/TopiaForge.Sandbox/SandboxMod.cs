using System;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    /// <summary>
    /// The Sandbox gamemode content layer. TopiaForge Worlds owns the Open Sandbox world/arena and registers
    /// the sandbox gamemode; this mod attaches the actual creator gameplay (spawn menu, undo/freeze tools,
    /// HUD) to any session running that gamemode — the same provider/consumer split Zombies uses.
    /// </summary>
    public sealed class SandboxMod : ITopiaForgeMod
    {
        // Owned by TopiaForge.Worlds (WorldsService.SandboxGamemodeId). Kept as a local constant so this mod
        // binds to the stable id, not to the Worlds assembly.
        public const string GamemodeId = "io.github.furroxide.topiaforge.worlds.sandbox";

        private IModContext? context;
        private SandboxConfig? config;
        private IWorldGamemodeService? worlds;
        private SandboxController? controller;
        private IDisposable? cleanupPauseAction;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            config = context.LoadConfig(new SandboxConfig());
            config.Normalize();
            context.SaveConfig(config);

            worlds = context.GetService<IWorldGamemodeService>();
            if (worlds == null)
            {
                context.Logger.Warn("TopiaForge Worlds service is not available; the Sandbox gamemode stays vanilla.");
                return;
            }

            worlds.SessionChanged += OnSessionChanged;
            worlds.SessionEnded += OnSessionEnded;
            if (worlds.CurrentSession != null)
            {
                OnSessionChanged(worlds.CurrentSession);
            }

            context.Update += OnUpdate;
            context.Logger.Info("Sandbox gamemode content loaded (spawn menu, tools, robots).");
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
            controller = new SandboxController(context, config);
            controller.Start(session);

            // Surface the big red button in the TopiaForgeUi pause companion too, so a cluttered stage can be reset
            // without opening the spawn menu. The vanilla exit needs no interceptor: end-session-then-exit
            // (the Worlds default) already tears the sandbox down via SessionEnded below.
            var pauseMenu = context.GetService<IWorldPauseMenuService>();
            cleanupPauseAction = pauseMenu?.RegisterAction(new WorldPauseAction(
                "io.github.furroxide.topiaforge.sandbox.cleanup",
                "CLEAN UP EVERYTHING",
                () => controller?.CleanUpEverything(),
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
            cleanupPauseAction?.Dispose();
            cleanupPauseAction = null;
            controller?.Dispose();
            controller = null;
        }
    }
}
