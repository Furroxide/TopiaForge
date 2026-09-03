using System;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Robot wave-survival gamemode authored entirely against V1 SDK contracts.</summary>
    public sealed class ZombiesMod : TopiaForgeMod
    {
        /// <summary>Gets the stable Zombies gamemode id.</summary>
        public const string GamemodeId = "io.github.furroxide.topiaforge.zombies.survival";

        internal const string MenuEntryId = "io.github.furroxide.topiaforge.zombies.menu";

        private static readonly ConfigDefinition<ZombiesConfig> ConfigContract =
            // ZombiesConfig is ISelfNormalizingConfig; the config service bounds every stored and migrated
            // document, so this contract only has to describe the schema reshape.
            new ConfigDefinition<ZombiesConfig>(
                3,
                () => new ZombiesConfig(),
                validate: null,
                migrate: (storedSchemaVersion, value) =>
                {
                    value.MigrateFrom(storedSchemaVersion);
                    return OperationResult<ZombiesConfig>.Success(value);
                });

        private ZombiesConfig config = new ZombiesConfig();
        private IWorldGamemodeService? worlds;
        private GamemodeHost<ZombiesController>? host;

        private ZombiesController? controller => host?.Controller;

        /// <inheritdoc />
        protected override void OnLoad()
        {
            LoadConfig();
            ApplyAccessibility();
            RegisterCommands();

            if (!Context.TryGetExtension<IWorldGamemodeService>(out var worldsService))
            {
                Context.Logger.Warn("The Worlds module is unavailable; Zombies cannot register its gamemode.");
                return;
            }
            worlds = worldsService;

            if (!Context.TryGetExtension<IRobotAgentService>(out var robotService))
            {
                Context.Logger.Warn("RobotKit is unavailable; Zombies cannot create infected robot entities.");
                return;
            }

            // GamemodeHost owns registration rollback, the session subscription and its lifetime-deferred
            // unsubscribe, replay of an already-running session, one-controller-per-session, and teardown.
            var hosted = GamemodeHost<ZombiesController>.Create(
                Context,
                worldsService,
                GamemodeId,
                session => new ZombiesController(
                    Context,
                    config,
                    robotService,
                    session,
                    cancellationToken => ReturnToMenuAsync(session, cancellationToken)),
                new GamemodeDefinition(
                    GamemodeId,
                    "Zombies",
                    "Survive escalating waves of infected robots with the SDK zapper."),
                new GamemodeMenuEntry(
                    MenuEntryId,
                    "Zombies",
                    "Safe-SDK robot wave survival.",
                    GamemodeId,
                    config.TargetWorldId));
            if (!hosted.TryGetValue(out var gamemodeHost))
            {
                Context.Diagnostics.Report(new DiagnosticEntry(
                    "ZOMBIES_REGISTRATION_FAILED",
                    "Zombies could not register its gamemode.",
                    DiagnosticSeverity.Error,
                    hosted.ErrorMessage));
                return;
            }

            host = gamemodeHost;
            host.AddPauseAction(new WorldPauseAction(
                "zombies-restart",
                "RESTART RUN",
                () => host?.Controller?.Restart(),
                closePauseMenu: true,
                order: 0,
                destructive: true));

            Context.Logger.Info("Zombies V1 registered with safe Worlds, RobotKit, Chronos, input, physics, and UI APIs.");
        }

        private void LoadConfig() => config = ReadNormalizedConfig(Context);

        /// <summary>
        /// Loads and normalizes the stored configuration, writing the normalized form back so the next
        /// read is already bounded.
        /// </summary>
        /// <remarks>
        /// Static because <see cref="ZombiesGamemode"/> needs the same value from a session context and
        /// has no reference to the mod instance. Normalization is idempotent and the document is saved
        /// normalized, so reading it twice yields the same configuration.
        /// </remarks>
        internal static ZombiesConfig ReadNormalizedConfig(IModContext context)
        {
            var loaded = context.Config.Load(ConfigContract);
            if (!loaded.TryGetValue(out var value))
            {
                context.Logger.Warn("Zombies config could not be loaded: " + loaded.ErrorMessage);
                var fallback = new ZombiesConfig();
                fallback.Normalize();
                context.Config.Save(ConfigContract, fallback);
                return fallback;
            }

            value.Normalize();
            var saved = context.Config.Save(ConfigContract, value);
            if (!saved.Succeeded)
            {
                context.Logger.Warn("Zombies config normalization could not be saved: " + saved.ErrorMessage);
            }

            return value;
        }

        private void RegisterCommands()
        {
            RegisterCommand(
                new CommandDefinition("zombies-restart", "Restart the current Zombies run."),
                invocation => controller?.Restart()
                    ?? OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Zombies gamemode first."));
            RegisterCommand(
                new CommandDefinition("zombies-stand-down", "Temporarily halt the infected robot horde."),
                invocation => controller?.BroadcastStandDown()
                    ?? OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Zombies gamemode first."));
            RegisterCommand(
                new CommandDefinition("zombies-status", "Describe the current Zombies run."),
                invocation => controller == null
                    ? OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Zombies gamemode first.")
                    : OperationResult<string>.Success(controller.DescribeStatus()));
        }

        private void ApplyAccessibility()
        {
            var current = Context.Ui.Accessibility;
            var result = Context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(
                config.HudHighContrast,
                config.HudScale,
                current.ReducedMotion || config.HudMotionIntensity <= 0f,
                config.HudMotionIntensity));
            if (!result.Succeeded)
            {
                Context.Diagnostics.Report(new DiagnosticEntry(
                    "ZOMBIES_ACCESSIBILITY_UNAVAILABLE",
                    "Zombies could not apply its configured UI accessibility profile.",
                    DiagnosticSeverity.Warning,
                    result.ErrorMessage));
            }
        }

        private void RegisterCommand(
            CommandDefinition definition,
            Func<CommandInvocation, OperationResult<string>> handler)
        {
            var result = Context.Commands.Register(definition, handler);
            if (!result.Succeeded)
            {
                Context.Logger.Warn("Could not register /" + definition.Name + ": " + result.ErrorMessage);
            }
        }

        private async Task<OperationResult<SceneSnapshot>> ReturnToMenuAsync(
            WorldSession originatingSession,
            System.Threading.CancellationToken cancellationToken)
        {
            var result = await Context.Scenes.LoadAsync(
                new SceneLoadRequest(GameScenes.MainMenuSceneName, SceneLoadMode.Single),
                cancellationToken);
            if (result.Succeeded && ReferenceEquals(worlds?.CurrentSession, originatingSession))
            {
                worlds?.EndSession(WorldSessionEndReason.EndedByGamemode);
            }

            return result;
        }
    }
}
