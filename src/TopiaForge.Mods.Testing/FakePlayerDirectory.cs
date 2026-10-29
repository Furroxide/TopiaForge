using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic player directory with the runtime's handle lifetime rules and one local player by default.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed partial class FakePlayerDirectory : IPlayerDirectory, IDisposable
    {
        /// <summary>Gets the world view used by these fake players, including its shared controls.</summary>
        [RequiresTopiaForge("0.2.0")]
        public FakeWorldView WorldView { get; } = new FakeWorldView();
        private readonly FakeModLifetime lifetime = new FakeModLifetime();
        private readonly List<MultiplayerParticipant> records = new List<MultiplayerParticipant>();
        private readonly Dictionary<ParticipantId, FakePlayerState> states = new Dictionary<ParticipantId, FakePlayerState>();
        private readonly List<string> callbackErrors = new List<string>();
        private readonly PlayerDirectory directory;
        private MultiplayerSessionSnapshot snapshot;
        private ulong generation;
        private ulong tick;
        private bool disposed;

        /// <summary>Creates a solo directory whose local participant is named Sam.</summary>
        public FakePlayerDirectory()
        {
            AddRecord(new ParticipantId("local"), "Sam", new MultiplayerPlayerKey("local-key"), true);
            snapshot = BuildSnapshot();
            directory = new PlayerDirectory(() => snapshot, () => generation, new Backend(this),
                exception => callbackErrors.Add("join or leave handler failed: " + exception.Message));
        }
        /// <summary>Gets or sets whether mutations of other players and scoreboard values are allowed.</summary>
        public bool IsAuthoritative { get; set; } = true;
        /// <summary>Gets what join and leave handlers threw, in order; as in the game, the next handler still ran.</summary>
        public IReadOnlyList<string> CallbackErrors => callbackErrors.AsReadOnly();
        /// <inheritdoc/>
        public IReadOnlyList<IPlayer> Players => directory.Players;
        /// <inheritdoc/>
        public IPlayer? Local => directory.Local;
        /// <inheritdoc/>
        public bool TryGet(ParticipantId id, out IPlayer? player) => directory.TryGet(id, out player);
        /// <inheritdoc/>
        public IDisposable SubscribeJoined(Action<IPlayer> handler) => directory.SubscribeJoined(handler);
        /// <inheritdoc/>
        public IDisposable SubscribeLeft(Action<IPlayer> handler) => directory.SubscribeLeft(handler);
        /// <inheritdoc/>
        public OperationResult<IScoreboardColumn> RegisterScoreboardColumn(ScoreboardColumnDefinition definition) => directory.RegisterScoreboardColumn(definition);

        /// <summary>Adds a remote player and reports its join. Reusing a departed id creates a new handle.</summary>
        public IPlayer Add(ParticipantId id, string displayName, MultiplayerPlayerKey key = default)
        {
            Check();
            if (states.ContainsKey(id)) throw new ArgumentException("That player is already present.", nameof(id));
            AddRecord(id, displayName, key, false); Refresh();
            directory.TryGet(id, out var player); return player!;
        }
        /// <summary>Removes a remote player, permanently retiring its handles and cancelling the modals it was asked.</summary>
        public bool Remove(ParticipantId id)
        {
            Check();
            if (id.Value == "local") throw new InvalidOperationException("A solo directory keeps its local player.");
            if (!states.TryGetValue(id, out var state)) return false;
            WorldView.RemovePlayer(id);
            RetireAppearance(id);
            states.Remove(id);
            records.RemoveAll(record => record.Id.Equals(id)); Refresh();
            // As in the game, a player who leaves answers no modal, so each one it was asked reports a cancellation.
            foreach (var modal in new List<FakeUiModal>(state.Ui.Modals)) modal.Close();
            return true;
        }
        /// <summary>Replaces the participant list generation, even if every id and key is unchanged.</summary>
        public void ReplaceGeneration() { Check(); foreach (var id in states.Keys) { WorldView.RemovePlayer(id); RetireAppearance(id); } generation++; Refresh(); }
        /// <summary>Gets a present player's configurable game services.</summary>
        public FakePlayerState State(ParticipantId id) => states.TryGetValue(id, out var state) ? state : throw new ArgumentException("The player is absent.", nameof(id));
        /// <summary>Clears all press/release edges, preserving held actions, for the next simulated tick.</summary>
        public void AdvanceTick()
        {
            Check(); tick++;
            foreach (var state in states.Values) state.Input.AdvanceTick();
        }
        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; directory.Dispose(); lifetime.Dispose(); WorldView.Dispose();
            var retired = new List<Column>(columns.Values);
            for (var i = 0; i < retired.Count; i++) retired[i].Dispose();
        }
        private void Check() { if (disposed) throw new ObjectDisposedException(nameof(FakePlayerDirectory)); }
        private void AddRecord(ParticipantId id, string name, MultiplayerPlayerKey key, bool local)
        {
            records.Add(new MultiplayerParticipant(id, name, local, true, local, default, null, key));
            states.Add(id, new FakePlayerState(lifetime));
        }
        private void Refresh() { snapshot = BuildSnapshot(); directory.Refresh(); }
        private MultiplayerSessionSnapshot BuildSnapshot() => new MultiplayerSessionSnapshot(
            new MultiplayerSessionId("testing-players"), MultiplayerSessionState.Ready, MultiplayerProcessKind.Interactive,
            MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server, new ParticipantId("local"), records,
            new NetworkTick(tick), new SessionSeed(1), new ParticipantId("local"), MultiplayerSessionEndReason.None);

        private IEntity? BackendEntity(ParticipantId id) => State(id).Entity;
        private bool BackendSnapshot(ParticipantId id, out PlayerSnapshot? result) => State(id).Player.TryGetSnapshot(out result);
        private bool BackendHealth(ParticipantId id, out PlayerHealthSnapshot? result) => State(id).Player.TryGetHealth(out result);
        private OperationResult<PlayerHealthSnapshot> BackendDamage(ParticipantId id, PlayerDamageRequest request) =>
            Allowed(id) ? State(id).Player.Damage(request) : Refused<PlayerHealthSnapshot>();
        private OperationResult<PlayerHealthSnapshot> BackendHeal(ParticipantId id, float amount, string source) =>
            Allowed(id) ? State(id).Player.Heal(amount, source) : Refused<PlayerHealthSnapshot>();
        private OperationResult<bool> BackendTeleport(ParticipantId id, Vec3 position, float? yaw)
        {
            if (!Allowed(id)) return Refused<bool>();
            if (!Finite(position.X) || !Finite(position.Y) || !Finite(position.Z) || (yaw.HasValue && !Finite(yaw.Value)))
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Use a finite position and facing.");
            var player = State(id).Player;
            var aim = player.Snapshot?.AimRay ?? new Ray(position, new Vec3(0, 0, 1));
            player.Snapshot = new PlayerSnapshot(position, new Ray(position, aim.Direction));
            return OperationResult<bool>.Success(true);
        }
        private IPlayerInput BackendInput(ParticipantId id) => State(id).Input;
        private IPlayerUi BackendUi(ParticipantId id) => new FakeTargetUi(this, id);
        private bool Allowed(ParticipantId id) => IsAuthoritative || id.Value == "local";
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static OperationResult<T> Refused<T>() where T : notnull => OperationResult<T>.Failure(ModErrorCode.NotAuthoritative, "Only the host can act on another player.");
        private sealed class FakeTargetUi : IPlayerUi
        {
            private readonly FakePlayerDirectory owner;
            private readonly ParticipantId id;
            internal FakeTargetUi(FakePlayerDirectory owner, ParticipantId id) { this.owner = owner; this.id = id; }
            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => owner.Allowed(id) ? owner.State(id).Ui.ShowToast(message, tone) : Refused<bool>();
            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => owner.Allowed(id) ? owner.State(id).Ui.ShowModal(request, completed) : Refused<IUiModal>();
            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => owner.Allowed(id) ? owner.State(id).Ui.CreateSurface(request) : Refused<IUiSurface>();
        }
    }
}
