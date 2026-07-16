using System;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    /// <summary>Freeform Robotopia sandbox authored entirely against V1 SDK contracts.</summary>
    public sealed class SandboxMod : TopiaForgeMod
    {
        /// <summary>Gets the Worlds provider's built-in sandbox gamemode id.</summary>
        public const string GamemodeId = WellKnownWorldIds.SandboxGamemode;

        private static readonly ConfigDefinition<SandboxConfig> ConfigContract =
            new ConfigDefinition<SandboxConfig>(
                1,
                () => new SandboxConfig(),
                value =>
                {
                    value.Normalize();
                    return OperationResult<bool>.Success(true);
                });

        private SandboxConfig config = new SandboxConfig();
        private IWorldGamemodeService? worlds;
        private IRobotAgentService? robots;
        private SandboxController? controller;
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
                Context.Logger.Warn("The Worlds module is unavailable; Sandbox commands will stay inactive.");
                return;
            }
            worlds = worldsService;

            if (!Context.Extensions.TryGet<IRobotAgentService>(out var robotService)
                || robotService == null)
            {
                Context.Logger.Warn("RobotKit is unavailable; Sandbox cannot create safe robot entities.");
                return;
            }
            robots = robotService;

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

            Context.Logger.Info("Sandbox V1 loaded with safe input, UI, physics, Worlds, and RobotKit services.");
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
                Context.Logger.Warn("Sandbox config could not be loaded: " + loaded.ErrorMessage);
                config = new SandboxConfig();
                config.Normalize();
                Context.Config.Save(ConfigContract, config);
                return;
            }

            config = value;
            config.Normalize();
            var saved = Context.Config.Save(ConfigContract, config);
            if (!saved.Succeeded)
            {
                Context.Logger.Warn("Sandbox config normalization could not be saved: " + saved.ErrorMessage);
            }
        }

        private void RegisterCommands()
        {
            RegisterCommand(
                new CommandDefinition("sandbox-spawn", "Spawn a safe RobotKit robot at the aim point."),
                invocation => controller?.SpawnRobot()
                    ?? OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Sandbox gamemode first."));
            RegisterCommand(
                new CommandDefinition("sandbox-undo", "Remove the most recently spawned sandbox robot."),
                invocation => controller?.Undo()
                    ?? OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Sandbox gamemode first."));
            RegisterCommand(
                new CommandDefinition("sandbox-clear", "Remove every robot spawned by this Sandbox session."),
                invocation => controller?.CleanUpEverything()
                    ?? OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Sandbox gamemode first."));
            RegisterCommand(
                new CommandDefinition("sandbox-status", "Describe the active Sandbox session."),
                invocation => controller == null
                    ? OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Sandbox gamemode first.")
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
            controller = new SandboxController(Context, config, robots);
            controllerLifetime = Context.Lifetime.Track(controller);

            if (Context.Extensions.TryGet<IWorldPauseMenuService>(out var pauseMenu)
                && pauseMenu != null)
            {
                var result = pauseMenu.RegisterAction(new WorldPauseAction(
                    "sandbox-cleanup",
                    "CLEAN UP SANDBOX",
                    () => controller?.CleanUpEverything(),
                    closePauseMenu: true,
                    order: 0,
                    destructive: true));
                if (result.TryGetValue(out var registration))
                {
                    pauseActionLifetime = registration;
                }
                else
                {
                    Context.Logger.Debug("Sandbox pause action unavailable: " + result.ErrorMessage);
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
