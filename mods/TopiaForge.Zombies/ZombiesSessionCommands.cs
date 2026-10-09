using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    internal sealed class ZombiesSessionCommands
    {
        private readonly IGamemodeSession session;
        private readonly IWorldSessionService sessions;
        private readonly ZombiesController controller;
        internal ZombiesSessionCommands(IGamemodeSession session, IWorldSessionService sessions, ZombiesController controller)
        { this.session = session; this.sessions = sessions; this.controller = controller; }
        internal OperationResult<string> Restart() => Invoke(controller.Restart);
        internal OperationResult<string> StandDown() => Invoke(controller.BroadcastStandDown);
        internal OperationResult<string> Status() => Invoke(() => OperationResult<string>.Success(controller.DescribeStatus()));
        private OperationResult<string> Invoke(Func<OperationResult<string>> operation)
        {
            var current = sessions.Current;
            return controller.IsDisposed || session.CancellationToken.IsCancellationRequested || session.Lifetime.IsStopping
                || current.Phase != WorldSessionPhase.Running
                || !string.Equals(current.Session?.SessionId, session.SessionId, StringComparison.Ordinal)
                    ? Inactive() : operation();
        }
        internal static OperationResult<string> Inactive() =>
            OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Zombies gamemode first.");
    }
}
