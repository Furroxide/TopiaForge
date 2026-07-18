using System;
using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>Registers a lifetime-owned gamemode and runs its active-session update loop.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        public const string GamemodeId = "{{MOD_ID}}.mode";

        private WorldSession? session;

        protected override void OnLoad()
        {
            var worlds = Context.RequireExtension<IWorldGamemodeService>();
            EnsureRegistered(worlds.RegisterGamemode(new GamemodeDefinition(
                GamemodeId,
                "{{DISPLAY_NAME}}",
                "Custom gamemode scaffolded from the gamemode template.")));
            EnsureRegistered(worlds.RegisterMenuEntry(new GamemodeMenuEntry(
                "{{MOD_ID}}.menu",
                "{{DISPLAY_NAME}}",
                "Custom gamemode scaffolded from the gamemode template.",
                GamemodeId,
                WellKnownWorldIds.OpenSandboxWorld)));

            worlds.SessionChanged += OnSessionChanged;
            worlds.SessionEnded += OnSessionEnded;
            Context.Lifetime.Defer(() =>
            {
                worlds.SessionChanged -= OnSessionChanged;
                worlds.SessionEnded -= OnSessionEnded;
            });
            Context.Events.SubscribeUpdate(OnUpdate);
            Context.Logger.Info("{{DISPLAY_NAME}} gamemode registered.");
        }

        protected override void OnUnload()
        {
            session = null;
        }

        private void OnSessionChanged(WorldSession newSession)
        {
            if (!string.Equals(newSession.GamemodeId, GamemodeId, StringComparison.Ordinal))
            {
                session = null;
                return;
            }

            session = newSession;
            Context.Logger.Info("{{DISPLAY_NAME}} session started in world " + newSession.WorldId + ".");

            // RobotKit is already declared by this scaffold. Resolve it when your round needs to spawn or
            // command robots: Context.RequireExtension<IRobotAgentService>().
        }

        private void OnSessionEnded(WorldSessionEnd end)
        {
            if (session == null || !string.Equals(end.Session.GamemodeId, GamemodeId, StringComparison.Ordinal))
            {
                return;
            }

            session = null;
            Context.Logger.Info("{{DISPLAY_NAME}} session ended (" + end.Reason + ").");
        }

        private void OnUpdate(float deltaTime)
        {
            if (session == null)
            {
                return;
            }

            // Per-frame gamemode logic (wave timers, win conditions, HUD updates) goes here.
        }

        private static void EnsureRegistered(OperationResult<IWorldRegistration> result)
        {
            if (!result.Succeeded)
            {
                throw new InvalidOperationException("Gamemode registration failed: " + result.ErrorMessage);
            }
        }
    }
}
