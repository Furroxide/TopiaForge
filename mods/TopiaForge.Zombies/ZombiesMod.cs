using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Robot wave-survival gamemode authored entirely against V1 SDK contracts.</summary>
    public sealed class ZombiesMod : TopiaForgeMod
    {
        /// <summary>Gets the stable Zombies gamemode id.</summary>
        public const string GamemodeId = "io.github.furroxide.topiaforge.zombies.survival";

        private const string MenuEntryId = "io.github.furroxide.topiaforge.zombies.menu";

        private static readonly ConfigDefinition<ZombiesConfig> ConfigContract =
            new ConfigDefinition<ZombiesConfig>(
                1,
                () => new ZombiesConfig(),
                value =>
                {
                    value.Normalize();
                    return OperationResult<bool>.Success(true);
                });

        private ZombiesConfig config = new ZombiesConfig();
        private IWorldGamemodeService? worlds;
        private IRobotAgentService? robots;
        private ZombiesController? controller;
        private IDisposable? controllerLifetime;
        private IDisposable? pauseActionLifetime;

        /// <inheritdoc />
        protected override void OnLoad()
        {
            LoadConfig();
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

            EnsureRegistered(worldsService.RegisterGamemode(new GamemodeDefinition(
                GamemodeId,
                "Zombies",
                "Survive escalating waves of infected robots with the SDK zapper.")), "gamemode");
            EnsureRegistered(worldsService.RegisterMenuEntry(new GamemodeMenuEntry(
                MenuEntryId,
                "Zombies",
                "Safe-SDK robot wave survival.",
                GamemodeId,
                config.TargetWorldId)), "menu entry");

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

        private void EnsureRegistered(
            OperationResult<IWorldRegistration> result,
            string description)
        {
            if (result.Succeeded)
            {
                return;
            }

            Context.Diagnostics.Report(new DiagnosticEntry(
                "ZOMBIES_REGISTRATION_FAILED",
                "The Zombies " + description + " could not be registered.",
                DiagnosticSeverity.Error,
                result.ErrorMessage));
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
            controller = new ZombiesController(
                Context,
                config,
                robots,
                () => worlds?.EndSession(WorldSessionEndReason.EndedByGamemode));
            controllerLifetime = Context.Lifetime.Track(controller);

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
