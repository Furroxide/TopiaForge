using System;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    internal sealed class SandboxSessionCommands
    {
        private readonly IGamemodeSession session;
        private readonly IWorldSessionService sessions;
        private readonly SandboxController controller;
        internal SandboxSessionCommands(IGamemodeSession session, IWorldSessionService sessions, SandboxController controller)
        { this.session = session; this.sessions = sessions; this.controller = controller; }
        internal OperationResult<string> SpawnRobot() => Invoke(controller.SpawnRobot);
        internal OperationResult<string> Undo() => Invoke(controller.Undo);
        internal OperationResult<string> CleanUpEverything() => Invoke(controller.CleanUpEverything);
        internal OperationResult<string> Status() => Invoke(() => OperationResult<string>.Success(controller.DescribeStatus()));
        internal OperationResult<string> EndWorkbench() => Invoke(() =>
        {
            var result = controller.EndSession();
            return result.Succeeded ? OperationResult<string>.Success("Sandbox End Session & Restore completed.")
                : OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
        });
        private OperationResult<string> Invoke(Func<OperationResult<string>> operation)
        {
            var current = sessions.Current;
            return controller.IsDisposed || session.CancellationToken.IsCancellationRequested || session.Lifetime.IsStopping
                || current.Phase != WorldSessionPhase.Running
                || !string.Equals(current.Session?.SessionId, session.SessionId, StringComparison.Ordinal)
                    ? Inactive() : operation();
        }
        internal static OperationResult<string> Inactive() =>
            OperationResult<string>.Failure(ModErrorCode.InvalidState, "Start the Sandbox gamemode first.");
    }
}
