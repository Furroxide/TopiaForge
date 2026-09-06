using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Reads the reserved multiplayer provider afresh before framework world mutations.</summary>
    internal sealed class MultiplayerSceneTransitionAuthorityPolicy : ISceneTransitionAuthorityPolicy
    {
        private readonly Func<MultiplayerSessionSnapshot?> readSnapshot;

        public MultiplayerSceneTransitionAuthorityPolicy(ModServiceRegistry registry)
        {
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            readSnapshot = () =>
            {
                var provider = registry.Get<IMultiplayerSession>();
                if (provider == null) return null;
                return provider.Snapshot ?? throw new InvalidOperationException("The multiplayer provider has no authority snapshot.");
            };
        }

        internal MultiplayerSceneTransitionAuthorityPolicy(Func<MultiplayerSessionSnapshot?> readSnapshot)
        {
            this.readSnapshot = readSnapshot ?? throw new ArgumentNullException(nameof(readSnapshot));
        }

        public SceneTransitionAuthorityDecision Evaluate(SceneTransitionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            MultiplayerSessionSnapshot? snapshot;
            try
            {
                snapshot = readSnapshot();
            }
            catch (Exception)
            {
                return SceneTransitionAuthorityDecision.Deny("Multiplayer world authority is unavailable.");
            }

            if (snapshot == null) return SceneTransitionAuthorityDecision.Allow();
            if (snapshot.State != MultiplayerSessionState.Ready)
                return SceneTransitionAuthorityDecision.Deny("The multiplayer session is not ready for world changes.");
            return snapshot.HasWorldAuthority
                ? SceneTransitionAuthorityDecision.Allow()
                : SceneTransitionAuthorityDecision.Deny("Only the canonical multiplayer server may change the shared world.");
        }
    }
}
