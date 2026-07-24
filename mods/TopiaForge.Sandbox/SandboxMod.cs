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
                2,
                () => new SandboxConfig(),
                value =>
                {
                    value.Normalize();
                    return OperationResult<bool>.Success(true);
                },
                (storedVersion, value) =>
                {
                    if (storedVersion < 2
                        && string.Equals(value.SpawnMenuKey, "Q", StringComparison.OrdinalIgnoreCase))
                    {
                        value.SpawnMenuKey = "F5";
                    }
                    value.Normalize();
                    return OperationResult<SandboxConfig>.Success(value);
                });

        private SandboxConfig config = new SandboxConfig();
        private IWorldGamemodeService? worlds;
        private IRobotAgentService? robots;
        private ICreatorContentService? creatorContent;
        private ICreatorToolHostService? creatorRouter;
        private SandboxController? controller;
        private IDisposable? controllerLifetime;
        private IDisposable? creatorHostLifetime;
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

            if (!Context.Extensions.TryGet<ICreatorContentService>(out var contentService)
                || contentService == null
                || !Context.Extensions.TryGet<ICreatorToolHostService>(out var routerService)
                || routerService == null)
            {
                Context.Logger.Warn("Creator Content is unavailable; the shared F5 Sandbox workbench cannot start.");
                return;
            }
            creatorContent = contentService;
            creatorRouter = routerService;

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
            RegisterCommand(
                new CommandDefinition("sandbox-end", "Run End Session & Restore for the Sandbox creator session."),
                invocation => controller == null
                    ? OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Sandbox gamemode first.")
                    : ToCommand(controller.EndSession(), "Sandbox End Session & Restore completed."));
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

            if (robots == null || creatorContent == null || creatorRouter == null)
            {
                return;
            }

            StopController();
            controller = new SandboxController(
                Context,
                config,
                robots,
                creatorContent,
                creatorRouter,
                session.WorldId);
            controllerLifetime = Context.Lifetime.Track(controller);
            var registered = creatorRouter.RegisterHost(new CreatorToolHostRegistrationRequest(
                "sandbox",
                "Creator Sandbox",
                priority: 200,
                controller,
                toggleBinding: string.Equals(config.SpawnMenuKey, "F5", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : config.SpawnMenuKey));
            if (registered.TryGetValue(out var hostRegistration))
            {
                creatorHostLifetime = hostRegistration;
            }
            else
            {
                Context.Logger.Warn("Sandbox F5 host registration failed: " + registered.ErrorMessage);
            }

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
            creatorHostLifetime?.Dispose();
            creatorHostLifetime = null;
            controllerLifetime?.Dispose();
            controllerLifetime = null;
            controller = null;
        }

        private static OperationResult<string> ToCommand(OperationResult<bool> result, string success) =>
            result.Succeeded
                ? OperationResult<string>.Success(success)
                : OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
    }
}
