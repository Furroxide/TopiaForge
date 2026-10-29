using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPresence
    {
        /// <summary>How long after an avatar could not be made it is tried again.</summary>
        internal const long AvatarRetryMicros = 1_000_000;

        // The fastest an avatar is shown moving from one pose to the next, falling included, and the slack on top: a bigger
        // step is a teleport, and the avatar snaps there instead of streaking across the world between two poses.
        private const double SnapMetersPerSecond = PresencePolicy.DefaultMaximumFallSpeedMetersPerSecond;
        private const double SnapSlackMeters = 2.0;

        private readonly List<PresencePeer> shown = new List<PresencePeer>();
        private int nametagVersion;
        private int shownVersion = -1;
        private bool avatarFailureLogged;
        private bool bodyFailureLogged;

        public int NametagCount
        {
            get
            {
                RefreshShown();
                return shown.Count;
            }
        }

        public int NametagVersion => nametagVersion;

        public bool TryGetNametag(int slot, out PresenceNametag nametag)
        {
            RefreshShown();
            nametag = default;
            if (slot < 0 || slot >= shown.Count) return false;
            var peer = shown[slot];
            var avatar = peer.Avatar;
            if (avatar == null || !avatar.IsAlive) return false;
            nametag = new PresenceNametag(peer.DisplayName, avatar.HeadPosition);
            return true;
        }

        /// <summary>Samples the local player after the mods' Update, and keeps its own peer's pose and state, which TryGetPose reads.</summary>
        internal void SampleLocal(long nowMicros)
        {
            Local.Sample(nowMicros, IsTransmitting());
        }

        /// <summary>
        /// Moves every remote player's avatar to its pose for now, before the mods' LateUpdate: an avatar is made the first
        /// time a player's pose arrives, wears a provider's body when one can be made, and is made again if the game
        /// destroyed it. A world or scene change retires the root and creates a current avatar.
        /// Nothing here allocates once the avatars exist.
        /// </summary>
        internal void DriveAvatars(long nowMicros, float deltaTime)
        {
            var factory = BodyFactory;
            var sceneVersion = SceneVersion?.Invoke() ?? 0;
            var world = RunningWorld?.Invoke();
            for (var index = 0; index < peers.Count; index++)
            {
                var peer = peers[index];
                var buffer = peer.Buffer;
                if (peer.IsLocal) continue;
                var avatar = peer.Avatar;
                if (avatar != null && (!avatar.IsAlive || peer.AvatarWorld != world || peer.AvatarSceneVersion != sceneVersion))
                {
                    DisposeAvatar(peer);
                    avatar = null;
                }

                if (!peer.HasPose || buffer == null) continue;
                if (avatar == null)
                {
                    if (nowMicros < peer.NextAvatarMicros) continue;
                    avatar = MakeAvatar(peer, nowMicros);
                    if (avatar == null) continue;
                }

                var wasWearing = avatar.IsWearingBody;
                avatar.Dress(factory, nowMicros);
                if (!wasWearing && avatar.IsWearingBody) Counters.BodiesWorn++;
                if (avatar.BodyError != null && !bodyFailureLogged)
                {
                    bodyFailureLogged = true;
                    Log.Warn("Multiplayer: the provider of players' bodies failed (" + avatar.BodyError + "); avatars wear capsules until it makes one.");
                }
                if (!buffer.TrySample(nowMicros, out var shownPose)) continue;
                avatar.Draw(shownPose.Position, RemotePlayerAvatar.YawOf(shownPose.Rotation), (peer.Flags & PresenceFlags.Crouching) != 0, deltaTime);
            }
        }

        /// <summary>
        /// Feeds a remote peer's interpolation buffer with a pose stamped on the host's clock, arriving now. A step no player
        /// could run or fall is a teleport: the buffer starts again from it, so the avatar snaps there.
        /// </summary>
        internal static void FeedBuffer(PresencePeer peer, long hostMicros, long nowMicros)
        {
            var buffer = peer.Buffer ??= new SnapshotInterpolationBuffer(PresenceLocalPlayer.SendIntervalMicros);
            if (hostMicros < 0 || hostMicros >= SnapshotInterpolationBuffer.MaximumTimeMicros) return;
            var feet = NetQuantization.DequantizePosition(peer.Position);
            if (peer.HasFed)
            {
                var dx = (double)feet.X - peer.FedFeet.X;
                var dy = (double)feet.Y - peer.FedFeet.Y;
                var dz = (double)feet.Z - peer.FedFeet.Z;
                var reach = SnapSlackMeters + SnapMetersPerSecond * Math.Max(0, hostMicros - peer.FedMicros) / 1_000_000.0;
                if (dx * dx + dy * dy + dz * dz > reach * reach) buffer.Reset();
            }

            peer.HasFed = true;
            peer.FedFeet = feet;
            peer.FedMicros = hostMicros;
            buffer.Add(new NetTransformSample(hostMicros, feet, NetQuantization.DequantizeRotation(peer.Rotation), NetQuantization.DequantizeVelocity(peer.Velocity)), nowMicros);
        }

        private RemotePlayerAvatar? MakeAvatar(PresencePeer peer, long nowMicros)
        {
            var tint = PresenceTint.For(peer.Index);
            try
            {
                var made = new RemotePlayerAvatar(Backend.CreateAvatar(peer.Index, tint), tint);
                peer.Avatar = made;
                peer.AvatarWorld = RunningWorld?.Invoke();
                peer.AvatarSceneVersion = SceneVersion?.Invoke() ?? 0;
                Counters.AvatarsMade++;
                nametagVersion++;
                avatarFailureLogged = false;
                return made;
            }
            catch (Exception exception)
            {
                peer.NextAvatarMicros = nowMicros + AvatarRetryMicros;
                if (!avatarFailureLogged) Log.Error("Multiplayer: a player's avatar could not be made (" + exception.GetType().Name + "); presence tries again each second.");
                avatarFailureLogged = true;
                return null;
            }
        }

        private void RefreshShown()
        {
            if (shownVersion == nametagVersion) return;
            shownVersion = nametagVersion;
            shown.Clear();
            for (var index = 0; index < peers.Count; index++)
            {
                if (!peers[index].IsLocal && peers[index].Avatar != null) shown.Add(peers[index]);
            }
        }

        /// <summary>Gets whether this player's voice is going out now: the talking flag's source, the voice service.</summary>
        private bool IsTransmitting()
        {
            try
            {
                var voice = control?.Voice;
                return voice != null && voice.TransmitState == InternalVoiceTransmitState.Transmitting;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Gets whether the host's voice service heard <paramref name="peer"/> lately: the truth a joined player's talking claim is checked by.</summary>
        internal bool IsHeard(PresencePeer peer)
        {
            try
            {
                var voice = control?.Voice;
                return voice != null && voice.IsSpeaking(peer.Id);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
