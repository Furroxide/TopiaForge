using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// A read-only player directory derived from session snapshots, for providers that do not replicate player
    /// state yet. Identity, name, and host data come from the participant records; live player state and every
    /// action report <see cref="ModErrorCode.Unavailable"/> instead of pretending to succeed.
    /// </summary>
    internal sealed class SnapshotPlayerDirectory : IPlayerDirectory
    {
        internal const string UnavailableMessage = "Player state and actions need the live session engine.";

        private readonly Func<MultiplayerSessionSnapshot> currentSnapshot;
        private readonly Dictionary<ParticipantId, SnapshotPlayer> handles = new Dictionary<ParticipantId, SnapshotPlayer>();
        private readonly object gate = new object();

        internal SnapshotPlayerDirectory(Func<MultiplayerSessionSnapshot> currentSnapshot)
        {
            this.currentSnapshot = currentSnapshot ?? throw new ArgumentNullException(nameof(currentSnapshot));
        }

        public IReadOnlyList<IPlayer> Players
        {
            get
            {
                var snapshot = currentSnapshot();
                var players = new List<IPlayer>(snapshot.Participants.Count);
                foreach (var participant in snapshot.Participants)
                {
                    players.Add(Handle(participant.Id));
                }

                return players.AsReadOnly();
            }
        }

        public IPlayer? Local
        {
            get
            {
                var local = currentSnapshot().LocalParticipantId;
                return local.HasValue ? Handle(local.Value) : null;
            }
        }

        public bool TryGet(ParticipantId id, out IPlayer? player)
        {
            if (currentSnapshot().TryGetParticipant(id, out _))
            {
                player = Handle(id);
                return true;
            }

            player = null;
            return false;
        }

        // Join and leave notifications need a provider that tracks participants as they change.
        public IDisposable SubscribeJoined(Action<IPlayer> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return EmptySubscription.Instance;
        }

        public IDisposable SubscribeLeft(Action<IPlayer> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return EmptySubscription.Instance;
        }

        public OperationResult<IScoreboardColumn> RegisterScoreboardColumn(ScoreboardColumnDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            return OperationResult<IScoreboardColumn>.Failure(ModErrorCode.Unavailable, UnavailableMessage);
        }

        private SnapshotPlayer Handle(ParticipantId id)
        {
            lock (gate)
            {
                if (!handles.TryGetValue(id, out var handle))
                {
                    handle = new SnapshotPlayer(this, id);
                    handles.Add(id, handle);
                }

                return handle;
            }
        }

        private MultiplayerParticipant? Record(ParticipantId id) =>
            currentSnapshot().TryGetParticipant(id, out var participant) ? participant : null;

        private sealed class SnapshotPlayer : IPlayer, IPlayerInput, IPlayerUi
        {
            private readonly SnapshotPlayerDirectory directory;
            private string lastDisplayName = string.Empty;
            private MultiplayerPlayerKey lastKey;

            internal SnapshotPlayer(SnapshotPlayerDirectory directory, ParticipantId id)
            {
                this.directory = directory;
                Id = id;
            }

            public ParticipantId Id { get; }

            public MultiplayerPlayerKey Key => Refresh()?.PlayerKey ?? lastKey;

            public string DisplayName => Refresh()?.DisplayName ?? lastDisplayName;

            public bool IsLocal => Refresh()?.IsLocal ?? false;

            public bool IsHost => Refresh()?.IsHost ?? false;

            public bool IsPresent => Refresh() != null;

            public IEntity? Entity => null;

            public IPlayerInput Input => this;

            public IPlayerUi Ui => this;

            public bool TryGetSnapshot(out PlayerSnapshot? snapshot)
            {
                snapshot = null;
                return false;
            }

            public bool TryGetHealth(out PlayerHealthSnapshot? health)
            {
                health = null;
                return false;
            }

            public OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request)
            {
                if (request == null) throw new ArgumentNullException(nameof(request));
                return Fail<PlayerHealthSnapshot>();
            }

            public OperationResult<PlayerHealthSnapshot> Heal(float amount, string source) => Fail<PlayerHealthSnapshot>();

            public OperationResult<bool> Teleport(Vec3 position, float? yawDegrees = null) => Fail<bool>();

            public bool IsHeld(string actionName) => false;

            public bool WasPressed(string actionName) => false;

            public bool WasReleased(string actionName) => false;

            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => Fail<bool>();

            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => Fail<IUiModal>();

            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => Fail<IUiSurface>();

            private OperationResult<T> Fail<T>() where T : notnull =>
                OperationResult<T>.Failure(
                    IsPresent ? ModErrorCode.Unavailable : ModErrorCode.NotFound,
                    IsPresent ? UnavailableMessage : "The player has left the session.");

            private MultiplayerParticipant? Refresh()
            {
                var record = directory.Record(Id);
                if (record != null)
                {
                    lastDisplayName = record.DisplayName;
                    lastKey = record.PlayerKey;
                }

                return record;
            }
        }

        private sealed class EmptySubscription : IDisposable
        {
            internal static readonly EmptySubscription Instance = new EmptySubscription();

            public void Dispose()
            {
            }
        }
    }
}
