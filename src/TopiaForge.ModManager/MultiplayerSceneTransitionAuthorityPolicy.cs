using System;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Reads the reserved multiplayer provider afresh before framework world mutations. A joined player changes no shared
    /// world, with one exception: its session follower (WS3.8) loads the host's world, returns to the main menu when the
    /// player leaves, and loads the level the host is in, while it holds a <see cref="FollowGrant"/>. Only a request made
    /// with <see cref="SceneTransitionPriority.SessionFollow"/> uses the grant, and only the follower reserves with it.
    /// </summary>
    internal sealed class MultiplayerSceneTransitionAuthorityPolicy : ISceneTransitionAuthorityPolicy
    {
        private readonly Func<MultiplayerSessionSnapshot?> readSnapshot;
        private int followGrants;

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

        /// <summary>Gets whether a session follower holds a grant now.</summary>
        internal bool IsFollowing => Volatile.Read(ref followGrants) > 0;

        /// <summary>
        /// Lets the session follower change this joined player's world, with <see cref="SceneTransitionPriority.SessionFollow"/>,
        /// until the returned grant is disposed. A joined player is never the canonical server, and its session is not Ready
        /// while it follows the host's world, so without the grant every one of those changes would be refused.
        /// </summary>
        internal FollowGrant GrantFollow()
        {
            Interlocked.Increment(ref followGrants);
            return new FollowGrant(this);
        }

        public SceneTransitionAuthorityDecision Evaluate(SceneTransitionRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            // The follower's own changes pass on its grant, ahead of the session's readiness and authority. Without a grant
            // they are decided as every other request is, so a follow that outlives its session is a single player's again.
            if (request.Priority == SceneTransitionPriority.SessionFollow && IsFollowing) return SceneTransitionAuthorityDecision.Allow();
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

        /// <summary>The session follower's leave to change this joined player's world. Disposing it ends it; disposing it again does nothing.</summary>
        internal sealed class FollowGrant : IDisposable
        {
            private MultiplayerSceneTransitionAuthorityPolicy? owner;

            internal FollowGrant(MultiplayerSceneTransitionAuthorityPolicy owner)
            {
                this.owner = owner;
            }

            public void Dispose()
            {
                var current = Interlocked.Exchange(ref owner, null);
                if (current != null) Interlocked.Decrement(ref current.followGrants);
            }
        }
    }
}
