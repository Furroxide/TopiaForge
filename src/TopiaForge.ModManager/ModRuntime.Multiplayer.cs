using System;
using System.Diagnostics;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private readonly Stopwatch worldClock = Stopwatch.StartNew();
        private readonly FollowedScene followedScene = new FollowedScene();
        private Func<long>? networkClock;
        private MultiplayerReplication? replication;
        private SessionFollow? sessionFollow;

        /// <summary>Gets session follow (WS3.8), once the session runtime is active: a host's announcements and a joined player's follow.</summary>
        internal SessionFollow? SessionFollow => sessionFollow;

        /// <summary>Gets the active scene as session follow reads it.</summary>
        internal FollowedScene FollowedScene => followedScene;

        /// <summary>
        /// Gets the replication services every mod context's network seam binds to: the composition root's realm and spawn
        /// ledger, and what providers register. Each package context takes its network profile from them.
        /// </summary>
        internal MultiplayerReplication Replication => replication ??= new MultiplayerReplication(multiplayer);

        /// <summary>Gets the network world (WS3.6): the frame stages, census, lookups and references it fills.</summary>
        internal NetworkWorld NetworkWorld => networkWorld;

        /// <summary>
        /// Gets or sets the network world's clock, in microseconds: by default a monotonic stopwatch that starts with the
        /// runtime. Tests set one they move by hand, so snapshots and interpolation never depend on how fast a machine is.
        /// </summary>
        internal Func<long> NetworkClock
        {
            get => networkClock ?? ReadWorldClock;
            set => networkClock = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// The local session's phase, as the realm rules read it: idle until the session runtime is activated, and stopping
        /// while the orchestrator still changes an idle session, as a return to the main menu does until the menu has loaded.
        /// </summary>
        private SessionPhase CurrentSessionPhase() => activeSessions?.RealmPhase ?? SessionPhase.Idle;

        /// <summary>
        /// Creates the network world over the engine side the gameplay host gives it; a host without one replicates no scene
        /// object and adopts no spawn. It reads time from a monotonic clock in microseconds, and builds a joined player's
        /// asset replicas through the owning mod's own package assets.
        /// </summary>
        private NetworkWorld InstallNetworkWorld() =>
            NetworkWorld.Install(
                multiplayer,
                Replication,
                (coreGameplayServices as INetworkWorldBackendSource)?.NetworkWorldBackend ?? DetachedNetworkWorldBackend.Instance,
                ReadNetworkClock,
                FindPackageAssets);

        private long ReadNetworkClock() => networkClock != null ? networkClock() : ReadWorldClock();

        /// <summary>
        /// Gives the session's time (WS3.13) the network world's clock, which its send rate is measured by, and the game's
        /// clock from the gameplay host, which Chronos drives time through and the pause guard keeps running online. A host
        /// without a clock leaves nothing to guard.
        /// </summary>
        private void InstallSessionTime()
        {
            multiplayer.SessionTime.Clock = ReadNetworkClock;
            multiplayer.GameClock = (coreGameplayServices as IGameClockSource)?.GameClock;
        }

        private long ReadWorldClock() => worldClock.Elapsed.Ticks / 10;

        /// <summary>
        /// Installs session follow over the session orchestrator (WS3.8): it announces this game's world when it hosts, and
        /// follows the host's when it joins, in the frame's step 2, reporting to the launcher only for a join the launcher
        /// planned. A level the game's own menus loaded counts as a game the player is in when joining (#224). A provider
        /// already attached is told at once, as it would have been.
        /// </summary>
        private void InstallSessionFollow(GamemodeSessionOrchestrator sessions)
        {
            if (sessionFollow != null) return;
            var follow = new SessionFollow(sessions, followedScene, sceneAuthority.GrantFollow, LeaveForFollow, () => networkWorld.Progress,
                DiscoverWorldsAsync, () => multiplayer.Control == null, () => multiplayer.LaunchedJoin, nativeDispatcher, multiplayer.Log);
            sessionFollow = follow;
            multiplayer.NativeWorldSource = () => followedScene.IsGameplay;
            multiplayer.AddObserver(follow);
            multiplayer.SessionFollow = follow;
            var attached = multiplayer.Control;
            if (attached == null) return;
            follow.Attached(attached);
            if (multiplayer.Realm != Core.Multiplayer.NetRealm.Offline) follow.RealmChanged(Core.Multiplayer.NetRealm.Offline, multiplayer.Realm);
        }

        private void LeaveForFollow() => multiplayer.Leave();

        /// <summary>Gets the asset service the gameplay host made for a loaded mod's package context, beneath its network seam.</summary>
        private IAssetService? FindPackageAssets(string modId)
        {
            for (var index = 0; index < loadedMods.Count; index++)
            {
                var loaded = loadedMods[index];
                if (!string.Equals(loaded.Manifest.Id, modId, StringComparison.OrdinalIgnoreCase)) continue;
                var assets = loaded.Context.Assets;
                return assets is NetAssetService decorated ? decorated.Inner : assets;
            }

            return null;
        }

        /// <summary>
        /// Tells the network world a scene loaded, inside the load's own event: in every realm the world keys the scene's
        /// objects as they loaded, which a promotion later uses, and online it indexes them too. The cost is the load's;
        /// frames pay nothing.
        /// </summary>
        private void NetworkSceneLoaded(int sceneHandle)
        {
            if (sceneHandle == 0) return;
            try
            {
                networkWorld.SceneLoaded(sceneHandle);
            }
            catch (Exception exception)
            {
                multiplayer.Log.Error("Multiplayer: a loaded scene could not be keyed as it loaded (" + exception.GetType().Name + "); a session keys it from its objects as they are when it first needs them.");
            }
        }

        /// <summary>Tells the network world a scene unloaded, so it forgets the scene's keys and, online, its objects.</summary>
        private void NetworkSceneUnloaded(int sceneHandle)
        {
            if (sceneHandle == 0) return;
            try
            {
                networkWorld.SceneUnloaded(sceneHandle);
            }
            catch (Exception exception)
            {
                // Its keys are forgotten whatever happens; what failed is letting go of its objects online.
                multiplayer.Log.Error("Multiplayer: an unloaded scene's objects could not be let go of (" + exception.GetType().Name + "); they go when this game stops hosting or leaves.");
            }
        }
    }
}
