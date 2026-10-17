using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private MultiplayerReplication? replication;

        /// <summary>
        /// Gets the replication services every mod context's network seam binds to: the composition root's realm and spawn
        /// ledger, and what providers register. Each package context takes its network profile from them.
        /// </summary>
        internal MultiplayerReplication Replication => replication ??= new MultiplayerReplication(multiplayer);

        /// <summary>The local session's phase, as the realm rules read it: idle until the session runtime is activated.</summary>
        private SessionPhase CurrentSessionPhase() => activeSessions?.Current.Phase ?? SessionPhase.Idle;
    }
}
