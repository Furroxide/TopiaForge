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
            // The gamemode, its world policy and its launch target are declared in
            // topiaforge.mod.json, so nothing is published from code. GamemodeHost still owns the
            // session subscription, replay of a session already running, one-controller-per-session,
            // and teardown.
            var hosted = GamemodeHost<{{TYPE_NAME}}Session>.Create(
                Context,
                Context.RequireExtension<IWorldGamemodeService>(),
                GamemodeId,
                session => new {{TYPE_NAME}}Session(Context, session));
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

    /// <summary>
    /// The type the manifest's <c>contributions.gamemodes[0].implementation</c> names.
    /// </summary>
    /// <remarks>
    /// The declaration says which type runs this gamemode; this is that type. Keeping the id on
    /// <see cref="{{TYPE_NAME}}Mod"/> means the manifest and the code cannot drift to two different
    /// strings without the compiler noticing.
    /// </remarks>
    public sealed class {{TYPE_NAME}}Gamemode : IGamemodeFactory
    {
        /// <inheritdoc />
        public string GamemodeId => {{TYPE_NAME}}Mod.GamemodeId;

        /// <inheritdoc />
        public OperationResult<IGamemodeController> CreateController(IGamemodeSession session)
        {
            if (session == null)
            {
                throw new System.ArgumentNullException(nameof(session));
            }

            return OperationResult<IGamemodeController>.Success(
                new {{TYPE_NAME}}Session(session.Mod, session.World));
        }
    }

    /// <summary>Runs one round. Created when a session starts, disposed when it ends.</summary>
    internal sealed class {{TYPE_NAME}}Session : IGamemodeController
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
