using System;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    /// <summary>Freeform Robotopia sandbox authored entirely against V1 SDK contracts.</summary>
    public sealed class SandboxMod : TopiaForgeMod
    {
        /// <summary>Gets the Sandbox creator gamemode id.</summary>
        /// <remarks>
        /// Owned by this package, not by the Worlds provider. Sandbox used to attach to a gamemode Worlds
        /// declared on its behalf, which gave the creator gameplay and the world infrastructure one shared
        /// identity: neither could be enabled, disabled or launched without the other, and the manifest of
        /// the package that implements the gameplay said nothing about it at all.
        /// </remarks>
        public const string GamemodeId = "io.github.furroxide.topiaforge.sandbox.creator";

        internal const string MenuEntryId = "io.github.furroxide.topiaforge.sandbox.creator.menu";

        private static readonly ConfigDefinition<SandboxConfig> ConfigContract =
            new ConfigDefinition<SandboxConfig>(
                2,
                () => new SandboxConfig(),
                validate: null,
                migrate: (storedVersion, value) =>
                {
                    // SandboxConfig is ISelfNormalizingConfig, so the config service bounds the migrated value
                    // on its own. This migration only reshapes what schema 1 meant.
                    if (storedVersion < 2
                        && string.Equals(value.SpawnMenuKey, "Q", StringComparison.OrdinalIgnoreCase))
                    {
                        value.SpawnMenuKey = "F5";
                    }
                    return OperationResult<SandboxConfig>.Success(value);
                });

        private SandboxConfig config = new SandboxConfig();
        private GamemodeHost<SandboxController>? host;
        private IDisposable? creatorHostLifetime;

        private SandboxController? controller => host?.Controller;

        /// <inheritdoc />
        protected override void OnLoad()
        {
            LoadConfig();
            RegisterCommands();

            if (!Context.TryGetExtension<IWorldGamemodeService>(out var worldsService))
            {
                Context.Logger.Warn("The Worlds module is unavailable; Sandbox commands will stay inactive.");
                return;
            }

            if (!Context.TryGetExtension<IRobotAgentService>(out var robotService))
            {
                Context.Logger.Warn("RobotKit is unavailable; Sandbox cannot create safe robot entities.");
                return;
            }

            if (!Context.TryGetExtension<ICreatorContentService>(out var contentService)
                || !Context.TryGetExtension<ICreatorToolHostService>(out var routerService))
            {
                Context.Logger.Warn("Creator Content is unavailable; the shared F5 Sandbox workbench cannot start.");
                return;
            }

            // Sandbox publishes its own gamemode now. The world is pinned to the generated Open Sandbox
            // arena because world routing is keyed on the world id: a blank id resolves to the first
            // checkpoint level, which is the campaign tutorial.
            var hosted = GamemodeHost<SandboxController>.Create(
                Context,
                worldsService,
                GamemodeId,
                session => CreateController(session, robotService, contentService, routerService),
                new GamemodeDefinition(
                    GamemodeId,
                    "Sandbox",
                    "Freeform creator sandbox: an open arena with a spawn menu for props and robots."),
                new GamemodeMenuEntry(
                    MenuEntryId,
                    "Sandbox",
                    "Freeform creator sandbox: an open arena with a spawn menu for props and robots.",
                    GamemodeId,
                    WellKnownWorldIds.OpenSandboxWorld));
            if (!hosted.TryGetValue(out var gamemodeHost))
            {
                Context.Logger.Warn("Sandbox could not host the sandbox gamemode: " + hosted.ErrorMessage);
                return;
            }

            host = gamemodeHost;
            host.AddPauseAction(new WorldPauseAction(
                "sandbox-cleanup",
                "CLEAN UP SANDBOX",
                () => host?.Controller?.CleanUpEverything(),
                closePauseMenu: true,
                order: 0,
                destructive: true));

            Context.Logger.Info("Sandbox V1 loaded with safe input, UI, physics, Worlds, and RobotKit services.");
        }

        /// <inheritdoc />
        protected override void OnUnload()
        {
            creatorHostLifetime?.Dispose();
            creatorHostLifetime = null;
        }

        private SandboxController CreateController(
            WorldSession session,
            IRobotAgentService robotService,
            ICreatorContentService contentService,
            ICreatorToolHostService routerService)
        {
            var created = new SandboxController(
                Context,
                config,
                robotService,
                contentService,
                routerService,
                session.WorldId);

            creatorHostLifetime?.Dispose();
            creatorHostLifetime = null;
            var registered = routerService.RegisterHost(new CreatorToolHostRegistrationRequest(
                "sandbox",
                "Creator Sandbox",
                priority: 200,
                created,
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

            return created;
        }

        private void LoadConfig() => config = ReadNormalizedConfig(Context);

        /// <summary>
        /// Loads and normalizes the stored configuration, writing the normalized form back so the next
        /// read is already bounded.
        /// </summary>
        /// <remarks>
        /// Static because <see cref="SandboxGamemode"/> needs the same value from a session context and has
        /// no reference to the mod instance. Normalization is idempotent and the document is saved
        /// normalized, so reading it twice yields the same configuration.
        /// </remarks>
        internal static SandboxConfig ReadNormalizedConfig(IModContext context)
        {
            var loaded = context.Config.Load(ConfigContract);
            if (!loaded.TryGetValue(out var value))
            {
                context.Logger.Warn("Sandbox config could not be loaded: " + loaded.ErrorMessage);
                var fallback = new SandboxConfig();
                fallback.Normalize();
                context.Config.Save(ConfigContract, fallback);
                return fallback;
            }

            value.Normalize();
            var saved = context.Config.Save(ConfigContract, value);
            if (!saved.Succeeded)
            {
                context.Logger.Warn("Sandbox config normalization could not be saved: " + saved.ErrorMessage);
            }

            return value;
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

        private static OperationResult<string> ToCommand(OperationResult<bool> result, string success) =>
            result.Succeeded
                ? OperationResult<string>.Success(success)
                : OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
    }
}
