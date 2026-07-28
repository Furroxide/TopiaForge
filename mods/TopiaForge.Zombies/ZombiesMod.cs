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
                2,
                () => new ZombiesConfig(),
                validate: null,
                migrate: (storedSchemaVersion, value) =>
                {
                    value.MigrateFrom(storedSchemaVersion);
                    return OperationResult<ZombiesConfig>.Success(value);
                });

        private ZombiesConfig config = new ZombiesConfig();
        private IWorldGamemodeService? worlds;
        private IRobotAgentService? robots;
        private IWorldRegistration? gamemodeRegistration;
        private IWorldRegistration? menuRegistration;
        private ZombiesController? controller;
        private IDisposable? controllerLifetime;
        private IDisposable? pauseActionLifetime;

        /// <inheritdoc />
        protected override void OnLoad()
        {
            LoadConfig();
            ApplyAccessibility();
            RegisterCommands();

            if (!Context.Extensions.TryGet<IWorldGamemodeService>(out var worldsService)
                || worldsService == null)
            {
                Context.Logger.Warn("The Worlds module is unavailable; Zombies cannot register its gamemode.");
                return;
            }
            worlds = worldsService;

            if (!Context.Extensions.TryGet<IRobotAgentService>(out var robotService)
                || robotService == null)
            {
                Context.Logger.Warn("RobotKit is unavailable; Zombies cannot create infected robot entities.");
                return;
            }
            robots = robotService;

            if (!TryRetainRegistration(
                    worldsService.RegisterGamemode(new GamemodeDefinition(
                        GamemodeId,
                        "Zombies",
                        "Survive escalating waves of infected robots with the SDK zapper.")),
                    "gamemode",
                    out gamemodeRegistration))
            {
                return;
            }

            if (!TryRetainRegistration(
                    worldsService.RegisterMenuEntry(new GamemodeMenuEntry(
                        MenuEntryId,
                        "Zombies",
                        "Safe-SDK robot wave survival.",
                        GamemodeId,
                        config.TargetWorldId)),
                    "menu entry",
                    out menuRegistration))
            {
                gamemodeRegistration?.Dispose();
                gamemodeRegistration = null;
                return;
            }

            worldsService.SessionChanged += OnSessionChanged;
            worldsService.SessionEnded += OnSessionEnded;
            Context.Lifetime.Defer(() =>
            {
                worldsService.SessionChanged -= OnSessionChanged;
                worldsService.SessionEnded -= OnSessionEnded;
            });

            if (worldsService.CurrentSession != null)
            {
                OnSessionChanged(worldsService.CurrentSession);
            }

            Context.Logger.Info("Zombies V1 registered with safe Worlds, RobotKit, Chronos, input, physics, and UI APIs.");
        }

        /// <inheritdoc />
        protected override void OnUnload()
        {
            StopController();
            menuRegistration?.Dispose();
            menuRegistration = null;
            gamemodeRegistration?.Dispose();
            gamemodeRegistration = null;
        }

        private void LoadConfig()
        {
            var loaded = Context.Config.Load(ConfigContract);
            if (!loaded.TryGetValue(out var value))
            {
                Context.Logger.Warn("Zombies config could not be loaded: " + loaded.ErrorMessage);
                config = new ZombiesConfig();
                config.Normalize();
                Context.Config.Save(ConfigContract, config);
                return;
            }

            config = value;
            config.Normalize();
            var saved = Context.Config.Save(ConfigContract, config);
            if (!saved.Succeeded)
            {
                Context.Logger.Warn("Zombies config normalization could not be saved: " + saved.ErrorMessage);
            }
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

        private bool TryRetainRegistration(
            OperationResult<IWorldRegistration> result,
            string description,
            out IWorldRegistration? registration)
        {
            if (result.TryGetValue(out registration) && registration != null)
            {
                return true;
            }

            registration = null;
            Context.Diagnostics.Report(new DiagnosticEntry(
                "ZOMBIES_REGISTRATION_FAILED",
                "The Zombies " + description + " could not be registered.",
                DiagnosticSeverity.Error,
                result.ErrorMessage));
            return false;
        }

        private void OnSessionChanged(WorldSession session)
        {
            if (!string.Equals(session.GamemodeId, GamemodeId, StringComparison.OrdinalIgnoreCase))
            {
                StopController();
                return;
            }

            if (robots == null)
            {
                return;
            }

            StopController();
            try
            {
                controller = new ZombiesController(
                    Context,
                    config,
                    robots,
                    session,
                    cancellationToken => ReturnToMenuAsync(session, cancellationToken));
                controllerLifetime = Context.Lifetime.Track(controller);
            }
            catch (Exception exception)
            {
                var failedController = controller;
                controller = null;
                try
                {
                    failedController?.Dispose();
                }
                catch (Exception cleanupException)
                {
                    Context.Logger.Warn("Zombies failed-session cleanup encountered an error: "
                        + cleanupException.Message);
                }

                Context.Diagnostics.Report(new DiagnosticEntry(
                    "ZOMBIES_SESSION_START_FAILED",
                    "Zombies could not start a visible, controllable session.",
                    DiagnosticSeverity.Error,
                    exception.Message));
                worlds?.EndSession(WorldSessionEndReason.LoadFailed);
                return;
            }

            if (Context.Extensions.TryGet<IWorldPauseMenuService>(out var pauseMenu)
                && pauseMenu != null)
            {
                var result = pauseMenu.RegisterAction(new WorldPauseAction(
                    "zombies-restart",
                    "RESTART RUN",
                    () => controller?.Restart(),
                    closePauseMenu: true,
                    order: 0,
                    destructive: true));
                if (result.TryGetValue(out var registration))
                {
                    pauseActionLifetime = registration;
                }
                else
                {
                    Context.Logger.Debug("Zombies pause action unavailable: " + result.ErrorMessage);
                }
            }
        }

        private void OnSessionEnded(WorldSessionEnd ended)
        {
            StopController();
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

        private void StopController()
        {
            pauseActionLifetime?.Dispose();
            pauseActionLifetime = null;
            controllerLifetime?.Dispose();
            controllerLifetime = null;
            controller = null;
        }
    }
}
