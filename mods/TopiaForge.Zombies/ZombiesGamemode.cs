using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Starts the declared survival gamemode after world readiness in the supplied session scope.</summary>
    public sealed class ZombiesGamemode : IGamemodeFactory
    {
        /// <inheritdoc />
        public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            ZombiesController? controller = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                session.CancellationToken.ThrowIfCancellationRequested();
                var context = session.Context;
                if (!context.TryGetExtension<IRobotAgentService>(out var robots)
                    || !context.TryGetExtension<IWorldSessionService>(out var sessions))
                    return Failed(null, ModErrorCode.Unavailable, "Zombies needs RobotKit and the session service.");
                var config = ZombiesMod.LoadConfiguration(context);
                ZombiesMod.ApplyAccessibility(context, config);
                controller = new ZombiesController(context, config, robots, session, () => IsRunning(session, sessions));
                var commands = new ZombiesSessionCommands(session, sessions, controller);
                if (context.TryGetExtension<IWorldPauseMenuService>(out var pause))
                {
                    var action = pause.RegisterAction(new WorldPauseAction("zombies-restart", "RESTART RUN",
                        () => commands.Restart(), closePauseMenu: true, order: 0, destructive: true));
                    if (action.TryGetValue(out var handle)) controller.Own(handle);
                    else context.Logger.Warn("Zombies pause action unavailable: " + action.ErrorMessage);
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

        private static Task<OperationResult<IGamemodeController>> Failed(ZombiesController? controller, ModErrorCode code, string message)
        {
            try { controller?.Dispose(); }
            catch (Exception cleanup) { code = ModErrorCode.External; message += "\nZombies startup cleanup failed: " + cleanup; }
            return Task.FromResult(OperationResult<IGamemodeController>.Failure(code, message));
        }
    }
}
