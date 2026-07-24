using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools
{
    /// <summary>Optional ordinary-game host for the shared F5 creator workbench.</summary>
    public sealed class CreatorToolsMod : TopiaForgeMod
    {
        private static readonly ConfigDefinition<CreatorToolsConfig> ConfigContract =
            new ConfigDefinition<CreatorToolsConfig>(
                1,
                () => new CreatorToolsConfig(),
                value =>
                {
                    value.Normalize();
                    return OperationResult<bool>.Success(true);
                });

        private GlobalCreatorToolsHost? host;
        private ICreatorToolHostService? router;
        private IDisposable? hostRegistration;

        /// <inheritdoc />
        protected override void OnLoad()
        {
            var loaded = Context.Config.Load(ConfigContract);
            var config = loaded.TryGetValue(out var value) ? value : new CreatorToolsConfig();
            config.Normalize();
            Context.Config.Save(ConfigContract, config);
            RegisterCommands();
            if (!config.Enabled)
            {
                Context.Logger.Info("Global Creator Tools is disabled by config.");
                return;
            }

            if (!Context.Extensions.TryGet<ICreatorContentService>(out var content) || content == null
                || !Context.Extensions.TryGet<ICreatorToolHostService>(out router) || router == null
                || !Context.Extensions.TryGet<IRobotAgentService>(out var robots) || robots == null)
            {
                Context.Logger.Warn("Global Creator Tools dependencies are unavailable.");
                return;
            }

            host = new GlobalCreatorToolsHost(Context, config, content, router, robots);
            Context.Lifetime.Track(host);
            var registered = router.RegisterHost(new CreatorToolHostRegistrationRequest(
                "global",
                "Global Creator Tools",
                priority: 50,
                host));
            if (registered.TryGetValue(out var registration))
            {
                hostRegistration = registration;
                Context.Logger.Info("Global Creator Tools registered as the ordinary-game F5 host.");
            }
            else
            {
                Context.Logger.Warn("Global Creator Tools host registration failed: " + registered.ErrorMessage);
            }
        }

        /// <inheritdoc />
        protected override void OnUnload()
        {
            hostRegistration?.Dispose();
            hostRegistration = null;
            host?.Dispose();
            host = null;
            router = null;
        }

        private void RegisterCommands()
        {
            RegisterCommand(
                new CommandDefinition("creator-tools", "Toggle the shared F5 Creator Tools workbench."),
                invocation => ToCommand(router?.Toggle()
                    ?? OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Creator Tools is disabled or unavailable."),
                    "Creator Tools toggled."));
            RegisterCommand(
                new CommandDefinition("creator-tools-end", "Run End Session & Restore for the global creator session."),
                invocation => ToCommand(host?.EndSession()
                    ?? OperationResult<bool>.Failure(ModErrorCode.InvalidState, "No global creator session is active."),
                    "Global End Session & Restore completed."));
            RegisterCommand(
                new CommandDefinition("creator-tools-status", "Describe the global creator session."),
                invocation => host == null
                    ? OperationResult<string>.Failure(ModErrorCode.Unavailable, "Global Creator Tools is disabled or unavailable.")
                    : OperationResult<string>.Success(host.DescribeStatus()));
        }

        private void RegisterCommand(CommandDefinition definition, Func<CommandInvocation, OperationResult<string>> handler)
        {
            var result = Context.Commands.Register(definition, handler);
            if (!result.Succeeded) Context.Logger.Warn("Could not register /" + definition.Name + ": " + result.ErrorMessage);
        }

        private static OperationResult<string> ToCommand(OperationResult<bool> result, string success) =>
            result.Succeeded
                ? OperationResult<string>.Success(success)
                : OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
    }
}
