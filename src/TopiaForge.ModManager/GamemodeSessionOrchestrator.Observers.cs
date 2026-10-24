using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class GamemodeSessionOrchestrator : IWorldSessionService
    {
        private WorldSessionSnapshot publicState = new WorldSessionSnapshot(WorldSessionPhase.Idle, null, null, 0);
        private event Action<WorldSessionSnapshot>? publicStateChanged;
        WorldSessionSnapshot IWorldSessionService.Current => publicState;
        event Action<WorldSessionSnapshot> IWorldSessionService.StateChanged
        {
            add { publicStateChanged += value; }
            remove { publicStateChanged -= value; }
        }
        private WorldSessionSnapshot CapturePublicState(SessionStateSnapshot state, SessionRecord record)
        {
            var phase = (WorldSessionPhase)(int)state.Phase;
            return new WorldSessionSnapshot(phase, state.Identity == null ? null : new BoundWorldSession(this, state.Identity),
                phase == WorldSessionPhase.Idle ? null : record.Readiness, state.Sequence);
        }

        /// <summary>
        /// Hands <paramref name="progress"/> each progress record published under <paramref name="requestId"/>, until the
        /// returned handle is disposed: a live join's records take the progress of the session follow that runs the host's
        /// world for that join (WS3.8), as the runtime's publisher takes a launch request's from <see cref="Progress"/>.
        /// Records are published on the dispatcher's thread, the game's main thread.
        /// </summary>
        internal IDisposable SubscribeProgress(string requestId, Action<LaunchProgress> progress)
        {
            LaunchContractValues.Token(requestId, nameof(requestId));
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            Action<LaunchProgress> handler = record =>
            {
                if (string.Equals(record.RequestId, requestId, StringComparison.Ordinal)) progress(record);
            };
            Progress += handler;
            return new ProgressSubscription(this, handler);
        }

        /// <summary>A subscription to nothing, for a caller that has no orchestrator to subscribe to.</summary>
        internal static IDisposable NoProgress { get; } = new ProgressSubscription(null, _ => { });

        /// <summary>A subscription to the orchestrator's progress; disposing it ends it, and disposing it again does nothing.</summary>
        private sealed class ProgressSubscription : IDisposable
        {
            private GamemodeSessionOrchestrator? owner;
            private readonly Action<LaunchProgress> handler;

            internal ProgressSubscription(GamemodeSessionOrchestrator? owner, Action<LaunchProgress> handler)
            {
                this.owner = owner;
                this.handler = handler;
            }

            public void Dispose()
            {
                var current = Interlocked.Exchange(ref owner, null);
                if (current != null) current.Progress -= handler;
            }
        }
        private sealed class BoundWorldSession : IWorldSession
        {
            private readonly GamemodeSessionOrchestrator owner;
            private readonly SessionIdentity identity;
            internal BoundWorldSession(GamemodeSessionOrchestrator owner, SessionIdentity identity)
            { this.owner = owner; this.identity = identity; }
            public string SessionId => identity.SessionId;
            public string TargetId => identity.Selection.TargetId;
            public string GamemodeId => identity.Selection.GamemodeId;
            public string WorldId => identity.Selection.WorldId;
            public string? WorldFamilyId => identity.Selection.WorldFamilyId;
            public Task<OperationResult<bool>> StopAsync(CancellationToken token = default) => owner.StopAsync(SessionId, token);
            public Task<OperationResult<bool>> RestartAsync(CancellationToken token = default) => owner.RestartAsync(SessionId, token);
            public Task<OperationResult<bool>> ReturnToMainMenuAsync(CancellationToken token = default) => owner.ReturnToMainMenuAsync(SessionId, token);
        }
    }
}
