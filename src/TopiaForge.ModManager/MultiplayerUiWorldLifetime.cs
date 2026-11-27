using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerRuntime
    {
        internal void FollowUiWorldSessions(GamemodeSessionOrchestrator sessions)
        {
            sessions.StateChanged += state =>
            {
                if (state.Phase == SessionPhase.Preparing) BeginUiWorld();
            };
        }
    }
}
