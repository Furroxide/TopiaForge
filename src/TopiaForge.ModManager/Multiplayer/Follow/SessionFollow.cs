using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Session follow (WS3.8) in the multiplayer runtime. It owns the <c>SceneFollow</c> system channel while a provider is
    /// attached; as a listen host it announces what this game is in (<see cref="SessionFollowHost"/>), and as a joined
    /// player it follows the host's announcements (<see cref="SessionFollower"/>). It runs in the frame's step 2, after the
    /// network world's events, so a world that arrives this frame is seen by the follow the same frame. A frame at rest
    /// allocates nothing. Unity-free; main thread. See docs/internal/MultiplayerRuntimeIntegration.md, section 19.
    /// </summary>
    internal sealed class SessionFollow : IMultiplayerRuntimeObserver, IMultiplayerFrameStage
    {
        private readonly GamemodeSessionOrchestrator sessions;
        private readonly FollowedScene scene;
        private readonly Func<bool> detaching;
        private readonly Func<string?> launchedJoin;
        private readonly IMultiplayerLog log;
        private IInternalSystemChannel? channel;
        private IInternalMultiplayerControl? control;
        private NetRealm realm;
        private HostWorldState? described;
        private bool channelFailureLogged;

        /// <param name="sessions">The session orchestrator, whose world this game announces or follows.</param>
        /// <param name="scene">The active scene, as the runtime tells it.</param>
        /// <param name="grantFollow">Takes the multiplayer authority policy's follow grant.</param>
        /// <param name="leave">Leaves the session through the realm, as the player's own leave does.</param>
        /// <param name="world">Reads the network world's progress, which the follow's Synchronizing stage waits on.</param>
        /// <param name="discover">Looks for the game's own worlds once more, for a host's world this game has not listed yet.</param>
        /// <param name="detaching">Gets whether the runtime is detaching from its provider, as the game shuts down.</param>
        /// <param name="launchedJoin">
        /// Gets the request the launcher-planned join this game went online with reports under, or null for a join started in
        /// game (<see cref="MultiplayerRuntime.LaunchedJoin"/>): the follow of that join reports to the launcher under it.
        /// </param>
        internal SessionFollow(GamemodeSessionOrchestrator sessions, FollowedScene scene, Func<IDisposable> grantFollow, Action leave,
            Func<NetworkWorldProgress> world, Func<CancellationToken, Task>? discover, Func<bool> detaching, Func<string?> launchedJoin,
            IHostDispatcher dispatcher, IMultiplayerLog log)
        {
            this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            this.scene = scene ?? throw new ArgumentNullException(nameof(scene));
            this.detaching = detaching ?? throw new ArgumentNullException(nameof(detaching));
            this.launchedJoin = launchedJoin ?? throw new ArgumentNullException(nameof(launchedJoin));
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            Host = new SessionFollowHost(() => channel, Describe);
            Follower = new SessionFollower(sessions, scene, () => channel, grantFollow, leave, world, discover, dispatcher, log);
            sessions.Follower = Follower;
        }

        internal SessionFollowHost Host { get; }

        internal SessionFollower Follower { get; }

        /// <summary>Gets whether the channel is open.</summary>
        internal bool IsOpen => channel != null && channel.IsOpen;

        public void Attached(IInternalMultiplayerControl attached)
        {
            control = attached ?? throw new ArgumentNullException(nameof(attached));
            OperationResult<IInternalSystemChannel> opened;
            try
            {
                opened = attached.OpenSystemChannel(InternalSystemChannelKind.SceneFollow);
            }
            catch (Exception exception)
            {
                opened = OperationResult<IInternalSystemChannel>.Failure(ModErrorCode.External, exception.GetType().Name);
            }

            if (opened.TryGetValue(out var opening))
            {
                channel = opening;
                channelFailureLogged = false;
                return;
            }

            if (!channelFailureLogged)
            {
                log.Warn("Multiplayer: the session follow channel could not be opened (" + opened.ErrorCode +
                         "), so players will not follow this game's world when it hosts, nor this game the host's when it joins; restart the game before hosting or joining.");
            }

            channelFailureLogged = true;
        }

        public void RealmChanged(NetRealm previous, NetRealm next)
        {
            realm = next;
            if (previous == NetRealm.Client) Follower.Left(detaching());
            if (previous == NetRealm.ListenHost) Host.Deactivate();
            if (next == NetRealm.ListenHost) Host.Activate();
            if (next == NetRealm.Client && control != null) Follower.Joined(control, launchedJoin());
        }

        public void Detaching(IInternalMultiplayerControl detached)
        {
            Follower.Left(detaching: true);
            Host.Deactivate();
            realm = NetRealm.Offline;
            var open = channel;
            channel = null;
            control = null;
            described = null;
            try
            {
                open?.Dispose();
            }
            catch (Exception exception)
            {
                log.Error("Multiplayer: the session follow channel could not be closed: " + exception.GetType().Name + ".");
            }
        }

        /// <summary>Step 2, online: a host announces its world; a joined player follows the host's.</summary>
        public void Run(float deltaTime)
        {
            if (realm == NetRealm.ListenHost) Host.Pump();
            else if (realm == NetRealm.Client) Follower.Pump(deltaTime);
        }

        /// <summary>
        /// What this host's game is in: its world session once it runs; while one starts or stops, what it announced last;
        /// otherwise the level it is in outside a session, or the main menu. The same instance comes back while nothing
        /// changed, so a frame at rest allocates nothing.
        /// </summary>
        private HostWorldState Describe()
        {
            var running = sessions.DescribeRunningWorld();
            if (running != null) return described = running;
            if (sessions.RealmPhase != SessionPhase.Idle) return described ?? HostWorldState.Menu;
            if (scene.IsMainMenu) return described = HostWorldState.Menu;
            var name = scene.Name;
            if (!scene.IsGameplay || name == null || !SessionFollowWire.IsSceneName(name)) return described ?? HostWorldState.Menu;
            if (described == null || described.Kind != HostWorldKind.Level || !string.Equals(described.Scene, name, StringComparison.Ordinal))
                described = HostWorldState.Level(name);
            return described;
        }
    }
}
