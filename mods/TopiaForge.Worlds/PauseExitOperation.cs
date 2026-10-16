using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    /// <summary>Issues one generation-bound menu operation; no vanilla native callback is part of this path.</summary>
    internal sealed class PauseExitOperation
    {
        private int pending;
        internal async Task<OperationResult<bool>> RunAsync(WorldSessionSnapshot snapshot,
            Func<WorldPauseExitContext, WorldPauseExitDecision>? interceptor,
            Action<Exception> reportInterceptorFailure, CancellationToken cancellationToken = default)
        {
            if (snapshot.Phase == WorldSessionPhase.Idle)
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "There is no active world session.");
            if (snapshot.Phase != WorldSessionPhase.Running || snapshot.Session == null)
                return OperationResult<bool>.Failure(ModErrorCode.Conflict, "The session is Busy.");
            if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
                return OperationResult<bool>.Failure(ModErrorCode.Conflict, "A pause exit is already Busy.");
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Pause exit was cancelled.");
                var decision = WorldPauseExitDecision.ReturnToMainMenu;
                try { if (interceptor != null) decision = interceptor(new WorldPauseExitContext(snapshot.Session)); }
                catch (Exception error) { reportInterceptorFailure(error); }
                if (decision == WorldPauseExitDecision.Block) return OperationResult<bool>.Success(false);
                return await snapshot.Session.ReturnToMainMenuAsync(cancellationToken);
            }
            catch (OperationCanceledException) { return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Pause exit was cancelled."); }
            catch (Exception error) { return OperationResult<bool>.Failure(ModErrorCode.External, error.Message); }
            finally { Volatile.Write(ref pending, 0); }
        }
    }
}
