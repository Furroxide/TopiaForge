using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private MultiplayerPresence? presence;

        /// <summary>Gets presence (WS3.10): players' poses, their avatars and the spawn ring.</summary>
        internal MultiplayerPresence Presence => presence ?? throw new InvalidOperationException("Presence has not been installed.");

        /// <summary>
        /// Creates presence over the engine side the gameplay host gives it; a host without one shows no avatars and sends
        /// no pose. It reads the network world's clock, the active scene from session follow, and the running world session
        /// and its spawn from the session orchestrator, once that exists.
        /// </summary>
        private MultiplayerPresence InstallPresence()
        {
            var installed = MultiplayerPresence.Install(
                multiplayer,
                Replication,
                (coreGameplayServices as IPresenceBackendSource)?.PresenceBackend ?? DetachedPresenceBackend.Instance,
                ReadNetworkClock);
            installed.SceneVersion = () => followedScene.Version;
            installed.RunningWorld = () => activeSessions?.RunningWorldId;
            installed.RunningSpawn = () => activeSessions?.RunningSpawn;
            networkWorld.Motion.ParticipantPosition = installed.ValidatedPosition;
            Replication.Players = MultiplayerPlayers.Install(multiplayer, installed);
            Replication.Players.Presentation = new MultiplayerPresentation(Replication.Players, CreatePresentationSink);
            return installed;
        }
    }

    /// <summary>A gameplay host's engine side for presence when the host has none: no player, no avatars, no ground.</summary>
    internal sealed class DetachedPresenceBackend : IPresenceBackend
    {
        internal static readonly DetachedPresenceBackend Instance = new DetachedPresenceBackend();

        private DetachedPresenceBackend()
        {
        }

        public IEntity? LocalPlayerEntity => null;

        public bool TryReadLocalPlayer(out PresenceLocalSample sample)
        {
            sample = default;
            return false;
        }

        public bool TryReadLocalHealth(out float health, out float maximum)
        {
            health = 0f;
            maximum = 0f;
            return false;
        }

        public string? ReadLocalHeldItem() => null;

        public bool TeleportLocalPlayer(NetVector3 feet, float yawDegrees, bool keepFacing) => false;

        public IPresenceAvatarObject CreateAvatar(int index, PresenceTint tint) =>
            throw new NotSupportedException("This gameplay host shows no avatars.");

        public NetVector3? FindGround(NetVector3 from) => null;

        public bool IsClear(NetVector3 feet) => false;

        public bool TryGetWorldBounds(out PresenceBounds bounds)
        {
            bounds = PresenceBounds.Everywhere;
            return false;
        }
    }
}
