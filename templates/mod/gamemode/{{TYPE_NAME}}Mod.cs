using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>Registers a lifetime-owned gamemode and runs one controller per active session.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        public const string GamemodeId = "{{MOD_ID}}.mode";

        private GamemodeHost<{{TYPE_NAME}}Session>? host;

        protected override void OnLoad()
        {
            // GamemodeHost owns registration rollback, the session subscription and its lifetime-deferred
            // unsubscribe, replay of a session that is already running, one-controller-per-session, and teardown.
            var hosted = GamemodeHost<{{TYPE_NAME}}Session>.Create(
                Context,
                Context.RequireExtension<IWorldGamemodeService>(),
                GamemodeId,
                session => new {{TYPE_NAME}}Session(Context, session),
                new GamemodeDefinition(
                    GamemodeId,
                    "{{DISPLAY_NAME}}",
                    "Custom gamemode scaffolded from the gamemode template."),
                new GamemodeMenuEntry(
                    "{{MOD_ID}}.menu",
                    "{{DISPLAY_NAME}}",
                    "Custom gamemode scaffolded from the gamemode template.",
                    GamemodeId,
                    WellKnownWorldIds.OpenSandboxWorld));
            if (!hosted.TryGetValue(out var gamemodeHost))
            {
                Context.Logger.Warn("{{DISPLAY_NAME}} could not register: " + hosted.ErrorMessage);
                return;
            }

            host = gamemodeHost;

            // Actions are re-registered for every session, so this is declared once here rather than per session.
            host.AddPauseAction(new WorldPauseAction(
                "{{MOD_ID}}.restart",
                "RESTART ROUND",
                () => host?.Controller?.Restart(),
                destructive: true));

            Context.Logger.Info("{{DISPLAY_NAME}} gamemode registered.");
        }
    }

    /// <summary>Runs one round. Created when a session starts, disposed when it ends.</summary>
    internal sealed class {{TYPE_NAME}}Session : System.IDisposable
    {
        private readonly IModContext context;
        private readonly WorldSession session;
        private readonly System.IDisposable updateSubscription;

        public {{TYPE_NAME}}Session(IModContext context, WorldSession session)
        {
            this.context = context;
            this.session = session;

            // Subscribing here means the loop only runs during a session, and stops when this object is disposed.
            updateSubscription = context.Events.SubscribeUpdate(OnUpdate);
            context.Logger.Info("{{DISPLAY_NAME}} session started in world " + session.WorldId + ".");
        }

        public OperationResult<string> Restart()
        {
            // Reset round state here.
            context.Ui.ShowToast("Round restarted.", UiTone.Warning);
            return OperationResult<string>.Success("Round restarted.");
        }

        public void Dispose()
        {
            updateSubscription.Dispose();
            context.Logger.Info("{{DISPLAY_NAME}} session ended.");
        }

        private void OnUpdate(float deltaTime)
        {
            // Per-round logic (wave timers, win conditions, HUD updates) goes here.
            // To spawn or command robots, run `topiaforge mod add robotkit` — it adds the package reference and
            // the manifest dependency together — then resolve Context.RequireExtension<IRobotAgentService>().
        }
    }
}
