using Robotopia.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// Registers the "{{DISPLAY_NAME}}" gamemode with the Worlds service so it appears in the game's
    /// level-select menu, and runs its session loop while a session is active. Requires the robotopia.worlds
    /// and robotopia.robotkit framework mods (declared in robotopia.mod.json).
    /// </summary>
    public sealed class {{TYPE_NAME}}Mod : IRobotopiaMod
    {
        public const string GamemodeId = "{{MOD_ID}}.mode";

        private IModContext? context;
        private IWorldGamemodeService? worlds;
        private WorldSession? session;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            worlds = context.GetService<IWorldGamemodeService>();
            if (worlds == null)
            {
                context.Logger.Warn("Robotopia Worlds service is not available; {{DISPLAY_NAME}} cannot register its gamemode.");
                return;
            }

            worlds.RegisterGamemode(new GamemodeDefinition(
                GamemodeId,
                "{{DISPLAY_NAME}}",
                "Custom gamemode scaffolded from the gamemode template."));
            worlds.RegisterMenuEntry(new GamemodeMenuEntry(
                "{{MOD_ID}}.menu",
                "{{DISPLAY_NAME}}",
                "Custom gamemode scaffolded from the gamemode template.",
                GamemodeId,
                worldId: string.Empty));

            worlds.SessionChanged += OnSessionChanged;
            worlds.SessionEnded += OnSessionEnded;
            context.Update += OnUpdate;
            context.Logger.Info("{{DISPLAY_NAME}} gamemode registered.");
        }

        public void OnUnload()
        {
            if (worlds != null)
            {
                worlds.SessionChanged -= OnSessionChanged;
                worlds.SessionEnded -= OnSessionEnded;
            }

            if (context != null)
            {
                context.Update -= OnUpdate;
            }

            session = null;
            worlds = null;
            context = null;
        }

        private void OnSessionChanged(WorldSession newSession)
        {
            if (newSession.GamemodeId != GamemodeId)
            {
                session = null;
                return;
            }

            session = newSession;
            context?.Logger.Info("{{DISPLAY_NAME}} session started in world " + newSession.WorldId + ".");
            // Spawn agents via context.GetService<IRobotAgentService>() and set up the round here.
        }

        private void OnSessionEnded(WorldSessionEnd end)
        {
            if (session == null)
            {
                return;
            }

            session = null;
            context?.Logger.Info("{{DISPLAY_NAME}} session ended.");
        }

        private void OnUpdate(float deltaTime)
        {
            if (session == null)
            {
                return;
            }

            // Per-frame gamemode logic (wave timers, win conditions, HUD updates) goes here.
        }
    }
}
