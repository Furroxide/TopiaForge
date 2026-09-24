using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.SdkAcceptance
{
    public sealed class AcceptanceGamemodeFactory : IGamemodeFactory
    {
        public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (cancellationToken.IsCancellationRequested || session.CancellationToken.IsCancellationRequested || session.Lifetime.IsStopping)
                return Task.FromResult(OperationResult<IGamemodeController>.Failure(ModErrorCode.Cancelled, "Acceptance startup cancelled."));
            if (!session.Context.TryGetExtension<IAcceptanceSessionProbe>(out var probe) || probe == null)
                return Task.FromResult(OperationResult<IGamemodeController>.Failure(ModErrorCode.Unavailable, "The owning acceptance probe is unavailable."));
            return Task.FromResult(probe.Begin(session));
        }
    }

    internal interface IAcceptanceSessionProbe
    {
        OperationResult<IGamemodeController> Begin(IGamemodeSession session);
    }

    public sealed partial class SdkAcceptanceMod
    {
        private sealed class AcceptanceSessionProbe : IAcceptanceSessionProbe
        {
            private readonly SdkAcceptanceMod owner;
            internal AcceptanceSessionProbe(SdkAcceptanceMod owner) { this.owner = owner; }
            public OperationResult<IGamemodeController> Begin(IGamemodeSession session) => owner.BeginAcceptanceSession(session);
        }
        private sealed class AcceptanceSessionRecord
        {
            internal AcceptanceSessionRecord(IGamemodeSession session) { Session = session; }
            internal IGamemodeSession Session { get; }
            internal readonly AcceptanceCleanupMarker ScopeCleanup = new AcceptanceCleanupMarker();
            internal bool LifecycleCompleted;
            internal bool ControllerDisposed;
        }
        private sealed class AcceptanceCleanupMarker : IDisposable
        {
            private int disposed;
            internal int DisposeCount { get; private set; }
            public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) DisposeCount++; }
        }
        private sealed class AcceptanceController : IGamemodeController
        {
            private AcceptanceSessionRecord? record;
            internal AcceptanceController(AcceptanceSessionRecord record) { this.record = record; }
            public void Dispose() { var previous = record; record = null; if (previous != null) previous.ControllerDisposed = true; }
        }
    }
}
