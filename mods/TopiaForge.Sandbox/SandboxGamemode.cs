using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    /// <summary>Starts the declared creator gamemode inside the supplied session scope.</summary>
    public sealed class SandboxGamemode : IGamemodeFactory
    {
        /// <inheritdoc />
        public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            SandboxController? controller = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                session.CancellationToken.ThrowIfCancellationRequested();
                var context = session.Context;
                if (!context.TryGetExtension<IRobotAgentService>(out var robots)
                    || !context.TryGetExtension<ICreatorContentService>(out var content)
                    || !context.TryGetExtension<ICreatorToolHostService>(out var router)
                    || !context.TryGetExtension<IWorldSessionService>(out var sessions))
                    return Failed(null, ModErrorCode.Unavailable, "Sandbox needs RobotKit, Creator Content, and the session service.");
                var config = SandboxMod.LoadConfiguration(context);
                controller = new SandboxController(context, config, robots, content, router, session.WorldId, () => IsRunning(session, sessions));
                var host = router.RegisterHost(new CreatorToolHostRegistrationRequest("sandbox", "Creator Sandbox", 200,
                    controller, string.Equals(config.SpawnMenuKey, "F5", StringComparison.OrdinalIgnoreCase) ? string.Empty : config.SpawnMenuKey));
                if (!host.TryGetValue(out var hostHandle)) return Failed(controller, host.ErrorCode, "Sandbox creator host: " + host.ErrorMessage);
                controller.Own(hostHandle);
                var commands = new SandboxSessionCommands(session, sessions, controller);
                if (context.TryGetExtension<IWorldPauseMenuService>(out var pause))
                {
                    var action = pause.RegisterAction(new WorldPauseAction("sandbox-cleanup", "CLEAN UP SANDBOX",
                        () => commands.CleanUpEverything(), closePauseMenu: true, order: 0, destructive: true));
                    if (action.TryGetValue(out var handle)) controller.Own(handle);
                    else context.Logger.Warn("Sandbox pause action unavailable: " + action.ErrorMessage);
                }
                cancellationToken.ThrowIfCancellationRequested();
                session.CancellationToken.ThrowIfCancellationRequested();
                var published = context.Extensions.Register(commands);
                if (!published.TryGetValue(out var registration)) return Failed(controller, published.ErrorCode, published.ErrorMessage);
                controller.Own(registration);
                return Task.FromResult(OperationResult<IGamemodeController>.Success(controller));
            }
            catch (OperationCanceledException exception) { return Failed(controller, ModErrorCode.Cancelled, exception.Message); }
            catch (Exception exception) { return Failed(controller, ModErrorCode.External, exception.ToString()); }
        }

        private static bool IsRunning(IGamemodeSession session, IWorldSessionService sessions)
        {
            var current = sessions.Current;
            return !session.CancellationToken.IsCancellationRequested && !session.Lifetime.IsStopping
                && current.Phase == WorldSessionPhase.Running
                && string.Equals(current.Session?.SessionId, session.SessionId, StringComparison.Ordinal);
        }

        private static Task<OperationResult<IGamemodeController>> Failed(SandboxController? controller, ModErrorCode code, string message)
        {
            try { controller?.Dispose(); }
            catch (Exception cleanup) { code = ModErrorCode.External; message += "\nSandbox startup cleanup failed: " + cleanup; }
            return Task.FromResult(OperationResult<IGamemodeController>.Failure(code, message));
        }
    }
}
