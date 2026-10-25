using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Exact scope lifetime and services of a consuming mod, never an arbitrary replacement context.</summary>
    internal sealed partial class PlayerContext : IInternalPlayerBackend, IDisposable
    {
        internal readonly MultiplayerPlayers Runtime;
        internal readonly string Mod;

        /// <summary>Whether every player runs this mod, so its actions and UI can reach them: false for a client-local or server-only mod.</summary>
        internal readonly bool Networked;

        /// <summary>The mod's own logger, which its failing UI callbacks are reported to, as the local UI kit reports them.</summary>
        internal readonly IModLogger Logger;
        internal readonly IModLifetime Lifetime;
        internal readonly ILocalPlayerService Local;
        internal readonly IUiService UiService;
        internal readonly ModInputActivity Activity;
        internal PlayerDirectory? Directory { get; set; }
        internal readonly Dictionary<ParticipantId, RemotePlayerInput> Inputs = new Dictionary<ParticipantId, RemotePlayerInput>();
        private readonly Dictionary<ParticipantId, CachedPose> poses = new Dictionary<ParticipantId, CachedPose>();
        private readonly Dictionary<ParticipantId, RemotePlayerUi> uis = new Dictionary<ParticipantId, RemotePlayerUi>();
        private readonly CachedPose localPose = new CachedPose();

        internal PlayerContext(MultiplayerPlayers runtime, string mod, bool networked, IModLogger logger, IModLifetime lifetime, ILocalPlayerService local, IUiService ui, ModInputActivity input)
        { Runtime = runtime; Mod = mod; Networked = networked; Logger = logger; Lifetime = lifetime; Local = local; UiService = ui; Activity = input; }

        public IEntity? Entity(ParticipantId id) => Runtime.IsLocal(id) ? Runtime.Presence.Backend.LocalPlayerEntity
            : Runtime.Presence.TryGetAvatar(id, out var entity) ? entity : null;
        public bool Snapshot(ParticipantId id, out PlayerSnapshot? snapshot)
        {
            if (Runtime.IsLocal(id))
            {
                snapshot = null;
                if (!Runtime.Presence.Backend.TryReadLocalPlayer(out var sample)) return false;
                var position = new Vec3(sample.Feet.X, sample.Feet.Y, sample.Feet.Z);
                var aim = new Ray(new Vec3(position.X, position.Y + sample.EyeHeight, position.Z), MultiplayerPresence.Forward(sample.View));
                if (localPose.Snapshot == null || !localPose.Snapshot.Position.Equals(position) || !localPose.Snapshot.AimRay.Equals(aim))
                    localPose.Snapshot = new PlayerSnapshot(position, aim);
                snapshot = localPose.Snapshot;
                return true;
            }
            var cache = ReadPose(id);
            snapshot = cache?.Snapshot;
            return snapshot != null;
        }
        public bool Health(ParticipantId id, out PlayerHealthSnapshot? health)
        {
            if (Runtime.IsLocal(id))
            {
                health = null;
                if (!Runtime.Presence.Backend.TryReadLocalHealth(out var current, out var maximum) || maximum <= 0) return false;
                if (localPose.Health == null || localPose.Health.Current != current || localPose.Health.Maximum != maximum)
                    localPose.Health = new PlayerHealthSnapshot(current, maximum);
                health = localPose.Health;
                return true;
            }
            var cache = ReadPose(id);
            health = cache?.Health;
            return health != null;
        }
        private CachedPose? ReadPose(ParticipantId id)
        {
            if (!Runtime.Presence.TryGetPose(id, out var pose)) return null;
            if (!poses.TryGetValue(id, out var cache)) poses.Add(id, cache = new CachedPose());
            if (cache.Snapshot == null || !cache.Snapshot.Position.Equals(pose.Position) || !cache.Snapshot.AimRay.Equals(pose.Aim))
                cache.Snapshot = new PlayerSnapshot(pose.Position, pose.Aim);
            if (pose.MaximumHealth <= 0) cache.Health = null;
            else if (cache.Health == null || cache.Health.Current != pose.Health || cache.Health.Maximum != pose.MaximumHealth)
                cache.Health = new PlayerHealthSnapshot(pose.Health, pose.MaximumHealth);
            return cache;
        }
        public OperationResult<PlayerHealthSnapshot> Damage(ParticipantId id, PlayerDamageRequest request) => request == null
            ? throw new ArgumentNullException(nameof(request)) : Runtime.ChangeHealth(this, id, request.Amount, request.Source, false);
        public OperationResult<PlayerHealthSnapshot> Heal(ParticipantId id, float amount, string source) => Runtime.ChangeHealth(this, id, amount, source, true);
        public OperationResult<bool> Teleport(ParticipantId id, Vec3 position, float? yaw) => Runtime.Teleport(this, id, position, yaw);
        public IPlayerInput Input(ParticipantId id) => InputState(id);
        internal RemotePlayerInput InputState(ParticipantId id)
        {
            if (!Inputs.TryGetValue(id, out var input)) Inputs.Add(id, input = new RemotePlayerInput());
            return input;
        }
        public IPlayerUi Ui(ParticipantId id)
        {
            if (!uis.TryGetValue(id, out var ui)) uis.Add(id, ui = new RemotePlayerUi(this, id));
            return ui;
        }
        public OperationResult<IScoreboardColumn> Column(ScoreboardColumnDefinition definition) => Runtime.RegisterColumn(this, definition);
        internal void Reset() { Inputs.Clear(); SentInputs.Clear(); poses.Clear(); uis.Clear(); }
        public void Dispose() { Runtime.ReleaseUi(this); Runtime.Remove(this); Reset(); }
        private sealed class CachedPose
        {
            internal PlayerSnapshot? Snapshot;
            internal PlayerHealthSnapshot? Health;
        }
    }
}
