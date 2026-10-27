using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Presence (WS3.10): every player sees the others in the host's world. Each player's own game sends its pose, 20 a
    /// second while it moves, on the <c>Avatars</c> channel's realtime lane; the host checks it and relays it to everyone
    /// else within a byte budget per player; and every peer shows each other player as an avatar, the game's own robot
    /// when RobotKit offers one, interpolated before the mods' LateUpdate. The host places joining players on the spawn
    /// ring. It owns the channel while a provider is attached, answers the session's avatar and pose lookups, and gives
    /// motion ownership the host's validated positions. Its host side and its joined side hold the role's state; this class
    /// wires them to the realm and the frame. Unity-free: the engine sits behind <see cref="IPresenceBackend"/>. Main
    /// thread only. See docs/internal/MultiplayerRuntimeIntegration.md, section 19.
    /// </summary>
    internal sealed partial class MultiplayerPresence : IMultiplayerRuntimeObserver, IInternalAvatarSource, IPresenceNametagSource
    {
        private readonly MultiplayerRuntime runtime;
        private readonly MultiplayerReplication replication;
        private readonly Func<long> clock;
        private readonly Dictionary<ParticipantId, PresencePeer> peersById = new Dictionary<ParticipantId, PresencePeer>();
        private readonly List<PresencePeer> peers = new List<PresencePeer>();
        private IInternalMultiplayerControl? control;
        private IInternalSystemChannel? channel;
        private IDisposable? avatarSourceLease;
        private MultiplayerSessionSnapshot? reconciled;
        private bool channelFailureLogged;
        private bool sourceFailureLogged;
        private bool snapshotFailureLogged;

        internal MultiplayerPresence(
            MultiplayerRuntime runtime,
            MultiplayerReplication replication,
            IPresenceBackend backend,
            Func<long> clock)
        {
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            this.replication = replication ?? throw new ArgumentNullException(nameof(replication));
            Backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Local = new PresenceLocalPlayer(backend);
            Host = new PresenceHost(this);
            Client = new PresenceClient(this);
            Receive = new Stage(this, StageKind.Receive);
            Send = new Stage(this, StageKind.Send);
            Avatars = new Stage(this, StageKind.Avatars);
        }

        internal IPresenceBackend Backend { get; }

        internal PresenceLocalPlayer Local { get; }

        internal PresenceHost Host { get; }

        internal PresenceClient Client { get; }

        internal PresenceCounters Counters { get; } = new PresenceCounters();

        /// <summary>Gets the rules the host checks poses by.</summary>
        internal PresencePolicy Policy { get; set; } = PresencePolicy.Default;

        /// <summary>Gets the indices that name players on the wire: the host's own, or the host's as a joined player keeps it.</summary>
        internal PresenceRoster Roster { get; } = new PresenceRoster();

        /// <summary>Gets this process's realm, as the last change told it.</summary>
        internal NetRealm Realm { get; private set; }

        /// <summary>Gets the local participant, as the session lists it, once it does.</summary>
        internal ParticipantId? LocalParticipant { get; private set; }

        /// <summary>Gets the participants presence knows, in the order the session first listed them.</summary>
        internal IReadOnlyList<PresencePeer> Peers => peers;

        /// <summary>Frame step 2, online: what other players' games sent, checked on the host.</summary>
        internal IMultiplayerFrameStage Receive { get; }

        /// <summary>Frame step 4, online: this player's own pose, and on the host the relay to every player.</summary>
        internal IMultiplayerFrameStage Send { get; }

        /// <summary>LateUpdate, online: the avatars move to the poses this process holds.</summary>
        internal IMultiplayerFrameStage Avatars { get; }

        /// <summary>
        /// Gets or sets what reports the active scene changing, which takes every avatar's body off for a new one, since a
        /// body may hold on to the scene it was made in. Session follow's <c>FollowedScene.Version</c> in the game.
        /// </summary>
        internal Func<int>? SceneVersion { get; set; }

        /// <summary>
        /// Gets or sets what names the world session this game runs, while it runs one: the host's own, or on a joined player
        /// the host's that it follows. Null at the menu, in a level outside a session, and while a session starts or stops.
        /// </summary>
        internal Func<string?>? RunningWorld { get; set; }

        /// <summary>Gets or sets where the running world session put its player: the readiness spawn the spawn ring is around.</summary>
        internal Func<TransformState?>? RunningSpawn { get; set; }

        /// <summary>Gets or sets whether the local player is typing in chat; the chat overlay (WS5b) sets it, and the pose carries it.</summary>
        internal bool LocalTyping
        {
            get => Local.Typing;
            set => Local.Typing = value;
        }

        internal IInternalSystemChannel? Channel => channel != null && channel.IsOpen ? channel : null;

        internal IInternalMultiplayerControl? Control => control;

        internal IMultiplayerLog Log => runtime.Log;

        internal IInternalAvatarBodyFactory? BodyFactory => replication.AvatarBodyFactory;

        /// <summary>Creates presence and fills the composition root's seams with it: its three stages and the observer list.</summary>
        internal static MultiplayerPresence Install(MultiplayerRuntime runtime, MultiplayerReplication replication, IPresenceBackend backend, Func<long> clock)
        {
            var presence = new MultiplayerPresence(runtime, replication, backend, clock);
            runtime.PresenceReceive = presence.Receive;
            runtime.PresenceSend = presence.Send;
            runtime.AvatarInterpolation = presence.Avatars;
            runtime.AddObserver(presence);
            return presence;
        }

        internal long Now() => clock();

        public void Attached(IInternalMultiplayerControl attached)
        {
            control = attached ?? throw new ArgumentNullException(nameof(attached));
            OpenChannel(attached);
            try
            {
                avatarSourceLease = attached.SetAvatarSource(this);
                sourceFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!sourceFailureLogged) Log.Warn("Multiplayer: the session's avatar and pose lookups are unavailable (" + exception.GetType().Name + "); mods will not find other players' avatars until the game restarts.");
                sourceFailureLogged = true;
            }
        }

        public void RealmChanged(NetRealm previous, NetRealm next)
        {
            if (previous == NetRealm.ListenHost) Host.Deactivate();
            if (previous == NetRealm.Client) Client.Deactivate();
            if (previous != NetRealm.Offline) Forget();
            Realm = next;
            if (next == NetRealm.Offline) return;
            if (Channel == null)
            {
                if (!channelFailureLogged)
                {
                    Log.Warn(next == NetRealm.ListenHost
                        ? "Multiplayer: hosting without the avatars channel, so players will not see each other; stop hosting, restart the game and open it again."
                        : "Multiplayer: joined without the avatars channel, so other players will not appear; leave, restart the game and join again.");
                }

                channelFailureLogged = true;
            }

            if (next == NetRealm.ListenHost) Host.Activate();
            else Client.Activate();
        }

        public void Detaching(IInternalMultiplayerControl detaching)
        {
            Host.Deactivate();
            Client.Deactivate();
            Forget();
            Realm = NetRealm.Offline;
            var open = channel;
            var lease = avatarSourceLease;
            channel = null;
            avatarSourceLease = null;
            control = null;
            try
            {
                open?.Dispose();
                lease?.Dispose();
            }
            catch (Exception)
            {
                // The provider is detaching anyway; its channels and sources go with it.
            }
        }

        public bool TryGetAvatar(ParticipantId participant, out IEntity? avatar)
        {
            avatar = null;
            if (Realm == NetRealm.Offline || !peersById.TryGetValue(participant, out var peer)) return false;
            avatar = peer.IsLocal ? Backend.LocalPlayerEntity : peer.Avatar != null && peer.Avatar.IsAlive ? peer.Avatar.Entity : null;
            return avatar != null;
        }

        public bool TryGetPose(ParticipantId participant, out MultiplayerPose pose)
        {
            pose = default;
            if (Realm == NetRealm.Offline || !peersById.TryGetValue(participant, out var peer) || !peer.HasPose) return false;
            pose = PoseOf(peer);
            return true;
        }

        /// <summary>
        /// Gets where the host last validated <paramref name="participant"/>'s player, for motion ownership's range checks
        /// (WS3.7, <c>NetworkWorldMotion.ParticipantPosition</c>): the host's own player as sampled, a joined player's last
        /// accepted pose while it is fresh, and null otherwise: not hosting, an unknown or departed participant, a lost link,
        /// no pose yet, or none accepted for <see cref="PresencePolicy.StaleAfterMicros"/>. So a check that needs a position
        /// fails closed instead of trusting what the player claims.
        /// </summary>
        internal NetVector3? ValidatedPosition(ParticipantId participant)
        {
            if (Realm != NetRealm.ListenHost || !peersById.TryGetValue(participant, out var peer) || !peer.HasPose) return null;
            if (peer.IsLocal) return Local.HasPlayer ? NetQuantization.DequantizePosition(peer.Position) : (NetVector3?)null;
            var check = peer.Plausibility;
            if (!peer.IsConnected || peer.Removed || check == null || !check.IsFresh(Now())) return null;
            return NetQuantization.DequantizePosition(check.Anchor);
        }

        internal bool TryGetPeer(ParticipantId participant, out PresencePeer? peer) => peersById.TryGetValue(participant, out peer);

        /// <summary>The public pose a peer's held pose and state stand for.</summary>
        internal static MultiplayerPose PoseOf(PresencePeer peer)
        {
            var feet = NetQuantization.DequantizePosition(peer.Position);
            var view = NetQuantization.DequantizeRotation(peer.Rotation);
            var velocity = NetQuantization.DequantizeVelocity(peer.Velocity);
            var eye = peer.EyeCentimeters / 100f;
            var aim = new Ray(new Vec3(feet.X, feet.Y + eye, feet.Z), Forward(view));
            var maximum = peer.HasState ? Math.Max(0f, peer.MaximumHealth) : 0f;
            var health = peer.HasState ? Math.Max(0f, Math.Min(peer.Health, maximum)) : 0f;
            return new MultiplayerPose(
                new NetworkTick((ulong)Math.Max(0, peer.PoseMicros) / (ulong)PresenceLocalPlayer.SendIntervalMicros),
                new Vec3(feet.X, feet.Y, feet.Z),
                aim,
                new Vec3(velocity.X, velocity.Y, velocity.Z),
                health,
                maximum,
                (MultiplayerPoseFlags)(int)(peer.Flags & (PresenceFlags.Crouching | PresenceFlags.Talking | PresenceFlags.Typing | PresenceFlags.Away)),
                peer.HeldItem);
        }

        /// <summary>Reconciles the peers with the session: names, links, arrivals and departures. A frame where nothing changed costs a compare.</summary>
        internal void Reconcile(long nowMicros)
        {
            var current = control;
            if (current == null) return;
            MultiplayerSessionSnapshot snapshot;
            try
            {
                snapshot = current.SessionSnapshot;
                snapshotFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!snapshotFailureLogged) Log.Error("Multiplayer: the session's players could not be read for presence (" + exception.GetType().Name + ").");
                snapshotFailureLogged = true;
                return;
            }

            if (snapshot == null || ReferenceEquals(snapshot, reconciled)) return;
            reconciled = snapshot;
            LocalParticipant = snapshot.LocalParticipantId;
            for (var index = 0; index < peers.Count; index++) peers[index].Seen = false;
            var participants = snapshot.Participants;
            for (var index = 0; index < participants.Count; index++)
            {
                var participant = participants[index];
                if (!peersById.TryGetValue(participant.Id, out var peer))
                {
                    peer = new PresencePeer(participant.Id, participant.DisplayName, participant.IsLocal);
                    peersById.Add(participant.Id, peer);
                    peers.Add(peer);
                    if (Realm == NetRealm.ListenHost) Host.Joined(peer, nowMicros);
                }
                else if (!string.Equals(peer.DisplayName, participant.DisplayName, StringComparison.Ordinal))
                {
                    peer.DisplayName = participant.DisplayName;
                    nametagVersion++;
                }

                if (peer.IsConnected != participant.IsConnected && Realm == NetRealm.ListenHost) Host.LinkChanged(peer, participant.IsConnected, nowMicros);
                peer.IsConnected = participant.IsConnected;
                peer.Seen = true;
            }

            for (var index = peers.Count - 1; index >= 0; index--)
            {
                if (!peers[index].Seen) Remove(peers[index], nowMicros);
            }
        }

        /// <summary>A participant left the session: its avatar goes at once, and on the host its index and slot.</summary>
        private void Remove(PresencePeer peer, long nowMicros)
        {
            peersById.Remove(peer.Id);
            peers.Remove(peer);
            if (Realm == NetRealm.ListenHost) Host.Left(peer, nowMicros);
            DisposeAvatar(peer);
        }

        /// <summary>Forgets every peer and avatar, as leaving the realm does.</summary>
        private void Forget()
        {
            for (var index = 0; index < peers.Count; index++) DisposeAvatar(peers[index]);
            peers.Clear();
            peersById.Clear();
            Roster.Clear();
            reconciled = null;
            LocalParticipant = null;
            Local.Restart();
            nametagVersion++;
        }

        private void DisposeAvatar(PresencePeer peer)
        {
            var avatar = peer.Avatar;
            peer.Avatar = null;
            if (avatar == null) return;
            nametagVersion++;
            try
            {
                avatar.Dispose();
            }
            catch (Exception exception)
            {
                Log.Error("Multiplayer: a player's avatar could not be removed (" + exception.GetType().Name + ").");
            }
        }

        private void OpenChannel(IInternalMultiplayerControl attached)
        {
            OperationResult<IInternalSystemChannel> opened;
            try
            {
                opened = attached.OpenSystemChannel(InternalSystemChannelKind.Avatars);
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
                Log.Warn("Multiplayer: the avatars channel could not be opened (" + opened.ErrorCode +
                         "), so players will not see each other when this game hosts or joins; restart the game before hosting or joining.");
            }

            channelFailureLogged = true;
        }

        internal static Vec3 Forward(NetQuaternion view)
        {
            double x = view.X, y = view.Y, z = view.Z, w = view.W;
            var forward = new Vec3((float)(2.0 * (x * z + w * y)), (float)(2.0 * (y * z - w * x)), (float)(1.0 - 2.0 * (x * x + y * y)));
            return forward.LengthSquared > 0.000001f ? forward : new Vec3(0f, 0f, 1f);
        }

        private enum StageKind
        {
            Receive,
            Send,
            Avatars
        }

        /// <summary>One of presence's frame stages.</summary>
        private sealed class Stage : IMultiplayerFrameStage
        {
            private readonly MultiplayerPresence presence;
            private readonly StageKind kind;

            internal Stage(MultiplayerPresence presence, StageKind kind)
            {
                this.presence = presence;
                this.kind = kind;
            }

            public void Run(float deltaTime)
            {
                var now = presence.Now();
                switch (kind)
                {
                    case StageKind.Receive:
                        presence.Reconcile(now);
                        if (presence.Realm == NetRealm.ListenHost) presence.Host.Receive(now);
                        else if (presence.Realm == NetRealm.Client) presence.Client.Receive(now);
                        break;
                    case StageKind.Send:
                        presence.SampleLocal(now);
                        if (presence.Realm == NetRealm.ListenHost) presence.Host.Send(now);
                        else if (presence.Realm == NetRealm.Client) presence.Client.Send(now);
                        break;
                    default:
                        presence.DriveAvatars(now, deltaTime);
                        break;
                }
            }
        }
    }
}
