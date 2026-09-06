using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    /// <summary>Runs the prepared world's native gameplay without requiring creator tools.</summary>
    public sealed class FreePlayGamemode : IGamemodeFactory
    {
        public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (cancellationToken.IsCancellationRequested || session.CancellationToken.IsCancellationRequested || session.Lifetime.IsStopping)
                return Task.FromResult(OperationResult<IGamemodeController>.Failure(ModErrorCode.Cancelled, "Free Play startup was cancelled."));
            return Task.FromResult(OperationResult<IGamemodeController>.Success(new Controller()));
        }
        private sealed class Controller : IGamemodeController { public void Dispose() { } }
    }
}
