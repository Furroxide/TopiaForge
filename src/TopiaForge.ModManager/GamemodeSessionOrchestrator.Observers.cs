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
