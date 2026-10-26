using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class GamemodeSessionOrchestrator
    {
        /// <summary>
        /// Gets the world session this game runs, by its id, while it runs one: the host's own, or on a joined player the
        /// host's that it follows. Null otherwise. Presence (WS3.10) tells the host a player arrived in its world by it.
        /// </summary>
        internal string? RunningWorldId
        {
            get
            {
                var state = lifecycle.Current;
                return state.Phase == SessionPhase.Running ? state.Identity?.SessionId : null;
            }
        }

        /// <summary>Gets where the running world session put its player as it became ready, which the spawn ring is around.</summary>
        internal TransformState? RunningSpawn => lifecycle.Current.Phase == SessionPhase.Running ? current?.Readiness?.Spawn : null;
    }
}
