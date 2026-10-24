using System;
using System.Runtime.ExceptionServices;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The network world (WS3.6): what makes a hosted session's objects exist, move and baseline on every player. It owns
    /// the <c>WorldReplication</c> system channel for as long as a provider is attached, fills the composition root's frame
    /// stages and its census seam, names entities for the session's lookups and the replication seam's references, and
    /// takes a joined player's world content. Its host side and its joined side hold the state; this class wires them to
    /// the realm. Unity-free: the engine sits behind <see cref="INetworkWorldBackend"/>. Main thread only. See
    /// docs/internal/MultiplayerRuntimeIntegration.md, section 13.
    /// </summary>
    internal sealed class NetworkWorld :
        IMultiplayerRuntimeObserver,
        IMultiplayerPromotionCensus,
        IInternalEntityIdentitySource,
        IMultiplayerReferenceSource,
        IMultiplayerLocalContent,
        IMultiplayerAuthority
    {
        private readonly MultiplayerRuntime runtime;
        private readonly NetworkWorldChannel channel = new NetworkWorldChannel();
        private IInternalMultiplayerControl? control;
        private IDisposable? identityLease;
        private bool channelFailureLogged;
        private bool identityFailureLogged;
        private bool restoreFailureLogged;

        internal NetworkWorld(
            MultiplayerRuntime runtime,
            MultiplayerReplication replication,
            INetworkWorldBackend backend,
            Func<long> clock,
            Func<string, IAssetService?> findAssets)
        {
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            if (replication == null) throw new ArgumentNullException(nameof(replication));
            if (backend == null) throw new ArgumentNullException(nameof(backend));
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (findAssets == null) throw new ArgumentNullException(nameof(findAssets));
            Counters = new NetworkWorldCounters();
            SceneKeys = new SceneKeyRegistry(backend);
            Host = new NetworkWorldHost(channel, backend, SceneKeys, clock, Counters);
            Client = new NetworkWorldClient(channel, backend, SceneKeys, clock, Counters, kind => replication.TryGetReplicaFactory(kind, out var factory) ? factory : null, findAssets,
                runtime.Spawns.ShadowKeys, replication.AssetLoads);
            Proxies = new NetworkWorldProxies(channel, Host, runtime.Spawns, runtime.Intents, replication, findAssets, clock, Counters)
            {
                IsSharedMod = modId => runtime.TryGetNetworkClass(modId, out var networkClass) && networkClass == ModNetworkClass.Shared
            };
            Backend = backend;
            Motion = new NetworkWorldMotion(this, runtime, clock);
            Proxies.ParticipantReleased = Motion.Disconnect;
            WorldEvents = new Stage(this, StageKind.WorldEvents);
            HostSnapshots = new Stage(this, StageKind.HostSnapshots);
            ReplicaInterpolation = new Stage(this, StageKind.ReplicaInterpolation);
        }

        internal NetworkWorldHost Host { get; }

        internal NetworkWorldClient Client { get; }

        /// <summary>Gets the listen host's side of authority routing (WS3.7): its players' requests and the scopes that own their objects.</summary>
        internal NetworkWorldProxies Proxies { get; }

        internal NetworkWorldCounters Counters { get; }

        /// <summary>Gets the keys every loaded scene's objects took as the scene loaded, offline as much as online.</summary>
        internal SceneKeyRegistry SceneKeys { get; }

        internal INetworkWorldBackend Backend { get; }

        internal NetworkWorldMotion Motion { get; }

        /// <summary>Gets the channel's end, so authority routing (WS3.7) can register its message types on it.</summary>
        internal NetworkWorldChannel Channel => channel;

        /// <summary>Frame step 2: what the host's players or a joined player's host sent.</summary>
        internal IMultiplayerFrameStage WorldEvents { get; }

        /// <summary>Frame step 4, listen host only: announcements, snapshots and baselines.</summary>
        internal IMultiplayerFrameStage HostSnapshots { get; }

        /// <summary>LateUpdate, joined players only: replicas move to the host's samples before the mods see them.</summary>
        internal IMultiplayerFrameStage ReplicaInterpolation { get; }

        /// <summary>Gets where this process's world stands, for the connecting overlay. It allocates nothing.</summary>
        internal NetworkWorldProgress Progress => Host.IsActive
            ? new NetworkWorldProgress(NetworkWorldPhase.Hosting, 0, 0, 0)
            : Client.Progress;

        /// <summary>Gets whether a joined player's world still holds it short of Ready.</summary>
        internal bool IsHoldingReadiness => Client.HoldsReadiness;

        /// <summary>
        /// Creates the network world and fills the composition root's seams with it: the three frame stages, the census, the
        /// observer list, and the replication seam's references and world content.
        /// </summary>
        internal static NetworkWorld Install(
            MultiplayerRuntime runtime,
            MultiplayerReplication replication,
            INetworkWorldBackend backend,
            Func<long> clock,
            Func<string, IAssetService?> findAssets)
        {
            var world = new NetworkWorld(runtime, replication, backend, clock, findAssets);
            runtime.WorldEvents = world.WorldEvents;
            runtime.HostSnapshots = world.HostSnapshots;
            runtime.ReplicaInterpolation = world.ReplicaInterpolation;
            runtime.PromotionCensus = world;
            runtime.AddObserver(world);
            replication.References = world;
            replication.LocalContent = world;
            replication.Authority = world;
            replication.Motion = world.Motion;
            runtime.MotionSend = world.Motion;
            return world;
        }

        public void Attached(IInternalMultiplayerControl attached)
        {
            if (attached == null) throw new ArgumentNullException(nameof(attached));
            control = attached;
            OpenChannel(attached);
            try
            {
                identityLease = attached.SetEntityIdentitySource(this);
                identityFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!identityFailureLogged) runtime.Log.Warn("Multiplayer: the session's entity lookups are unavailable (" + exception.GetType().Name + "); mods' network references will not resolve until the game restarts.");
                identityFailureLogged = true;
            }
        }

        public void RealmChanged(NetRealm previous, NetRealm next)
        {
            Motion.Reset();
            if (previous == NetRealm.ListenHost)
            {
                Proxies.Deactivate();
                Host.Deactivate();
            }

            if (previous == NetRealm.Client) LogRestore(Client.Deactivate());
            if (next != NetRealm.Client) return;
            var joined = control;
            if (joined == null || !channel.IsOpen)
            {
                if (!channelFailureLogged) runtime.Log.Warn("Multiplayer: joined without the world replication channel, so the host's world will not appear; leave, restart the game and join again.");
                channelFailureLogged = true;
                return;
            }

            Client.Activate(joined);
        }

        public void TakeCensus(MultiplayerPromotion promotion)
        {
            if (promotion == null) throw new ArgumentNullException(nameof(promotion));
            if (!channel.IsOpen)
            {
                if (!channelFailureLogged) runtime.Log.Warn("Multiplayer: hosting without the world replication channel, so players will not see this world; stop hosting, restart the game and open it again.");
                channelFailureLogged = true;
                return;
            }

            Host.Activate(promotion, runtime.Spawns);
            Proxies.Activate();
        }

        public void Detaching(IInternalMultiplayerControl detaching)
        {
            Motion.Reset();
            Proxies.Deactivate();
            Host.Deactivate();
            LogRestore(Client.Deactivate());
            channel.Close();
            var lease = identityLease;
            identityLease = null;
            try
            {
                lease?.Dispose();
            }
            catch (Exception)
            {
                // The provider is detaching anyway; its sources go with it.
            }

            control = null;
        }

        /// <summary>
        /// A scene loaded: its objects are keyed now, as it loaded, in every realm; online, the host or the joined player
        /// indexes them, and a joined player turns the scene's native authority off. A capture that fails is reported to
        /// the caller once the online side has had the scene, which it then keys itself.
        /// </summary>
        internal void SceneLoaded(int sceneHandle)
        {
            Exception? failure = null;
            try
            {
                SceneKeys.Capture(sceneHandle);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (Host.IsActive) Host.SceneLoaded(sceneHandle);
            if (Client.IsActive) Client.SceneLoaded(sceneHandle);
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>A scene unloaded: online, its objects are forgotten, and in every realm its keys.</summary>
        internal void SceneUnloaded(int sceneHandle)
        {
            try
            {
                if (Host.IsActive) Host.SceneUnloaded(sceneHandle);
                if (Client.IsActive) Client.SceneUnloaded(sceneHandle);
            }
            finally
            {
                SceneKeys.Forget(sceneHandle);
            }
        }

        /// <summary>Gets whether this process hosts the world or mirrors a host's.</summary>
        internal bool IsOnline => Host.IsActive || Client.IsActive;

        public bool TryGetNetworkObjectId(IEntity entity, out NetworkObjectId id)
        {
            id = default;
            if (entity == null || !TryGetId(entity, out var networkId)) return false;
            id = new NetworkObjectId(NetworkWorldIds.Format(networkId));
            return true;
        }

        public bool TryGetEntity(NetworkObjectId id, out IEntity? entity)
        {
            entity = null;
            if (!NetworkWorldIds.TryParse(id.Value, out var networkId)) return false;
            return TryGetEntity(networkId, out entity);
        }

        public bool TryCreateRef(IEntity entity, out InternalNetRef reference)
        {
            reference = default;
            if (entity == null || !TryGetId(entity, out var networkId)) return false;
            reference = new InternalNetRef(InternalNetRefKind.Entity, NetworkWorldIds.Format(networkId));
            return true;
        }

        public bool TryResolveRef(InternalNetRef reference, out IEntity? entity)
        {
            entity = null;

            // Players are the players directory's to name (WS3.11).
            if (reference.Kind != InternalNetRefKind.Entity || !NetworkWorldIds.TryParse(reference.Value, out var networkId)) return false;
            return TryGetEntity(networkId, out entity);
        }

        public void RegisterLocalContent(object owner, string contentKey, IEntity entity) => Client.RegisterLocalContent(owner, contentKey, entity);

        public void ReleaseOwner(object owner) => Client.ReleaseOwner(owner);

        public bool IsJoined => Client.IsActive;

        public bool CanShadow => Client.CanShadow;

        public bool IsLocal(IEntity entity) => Client.IsLocal(entity);

        public SpawnRefusal CheckPrediction() => Client.CheckPrediction();

        public void Predict(string modId, string kind, byte[] description, IEntity entity, IDisposable? owned, IInternalSpawnHandler? handler, IntentSource source,
            bool fromInputFrame, TransformState transform, IUiService? ui) =>
            Client.Predict(modId, kind, description, entity, owned, handler, source, fromInputFrame, transform, ui);

        public void Shadow(string modId, string kind, byte[] description, IEntity entity, IDisposable? owned, IInternalSpawnHandler? handler) =>
            Client.Shadow(modId, kind, description, entity, owned, handler);

        public OperationResult<TransformState>? RouteTransform(IEntity entity, TransformState transform, IEntityService inner, bool scripted) =>
            Client.RouteTransform(entity, transform, inner, scripted);

        public OperationResult<bool>? RouteDestroy(IEntity entity, bool scripted) => Client.RouteDestroy(entity, scripted);

        public void YieldBundle(string modId, string bundlePath)
        {
            Proxies.YieldBundle(modId, bundlePath);
            Client.YieldBundle(modId, bundlePath);
        }

        private bool TryGetId(IEntity entity, out NetworkEntityId networkId)
        {
            networkId = NetworkEntityId.None;
            if (!Host.IsActive && !Client.IsActive) return false;
            if (!Backend.TryGetNetworkId(entity, out networkId)) return false;

            // A mark only names an entity this session replicates; anything else is left from an earlier one.
            return Host.IsActive ? Host.Knows(networkId) : Client.Knows(networkId);
        }

        private bool TryGetEntity(NetworkEntityId networkId, out IEntity? entity)
        {
            entity = null;
            if (Host.IsActive && Host.TryGetEntity(networkId, out var hosted))
            {
                entity = hosted;
                return true;
            }

            if (Client.IsActive && Client.TryGetEntity(networkId, out var mirrored))
            {
                entity = mirrored;
                return true;
            }

            return false;
        }

        private void OpenChannel(IInternalMultiplayerControl attached)
        {
            OperationResult<IInternalSystemChannel> opened;
            try
            {
                opened = attached.OpenSystemChannel(InternalSystemChannelKind.WorldReplication);
            }
            catch (Exception exception)
            {
                opened = OperationResult<IInternalSystemChannel>.Failure(ModErrorCode.External, exception.GetType().Name);
            }

            if (opened.TryGetValue(out var opening))
            {
                channel.Open(opening);
                channelFailureLogged = false;
                return;
            }

            // This warning stands for the ones hosting or joining without the channel would give, so it says what they would.
            if (!channelFailureLogged)
            {
                runtime.Log.Warn("Multiplayer: the world replication channel could not be opened (" + opened.ErrorCode +
                                 "), so this game cannot share its world when it hosts or show the host's world when it joins; restart the game before hosting or joining.");
            }

            channelFailureLogged = true;
        }

        private void LogRestore(NativeAuthorityRestore restore)
        {
            if (restore.Failed == 0)
            {
                restoreFailureLogged = false;
                return;
            }

            if (!restoreFailureLogged)
            {
                runtime.Log.Error("Multiplayer: " + restore.Failed + " native setting(s) could not be restored after leaving; " + restore.Restored + " were. Load the world again to put the rest back.");
            }

            restoreFailureLogged = true;
        }

        private enum StageKind
        {
            WorldEvents,
            HostSnapshots,
            ReplicaInterpolation
        }

        /// <summary>One of the network world's frame stages.</summary>
        private sealed class Stage : IMultiplayerFrameStage
        {
            private readonly NetworkWorld world;
            private readonly StageKind kind;

            internal Stage(NetworkWorld world, StageKind kind)
            {
                this.world = world;
                this.kind = kind;
            }

            public void Run(float deltaTime)
            {
                switch (kind)
                {
                    case StageKind.WorldEvents:
                        world.Motion.BeforeReceive();
                        world.Host.Receive();
                        world.Proxies.Pump();
                        world.Client.Receive();
                        break;
                    case StageKind.HostSnapshots:
                        world.Host.Send();
                        break;
                    default:
                        world.Client.Drive();
                        break;
                }
            }
        }
    }
}
