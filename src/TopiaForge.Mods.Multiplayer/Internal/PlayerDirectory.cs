using System;
using System.Collections.Generic;
using System.Threading;

namespace TopiaForge.Mods.Internal
{
    /// <summary>The game-side services behind a facade's player handles. No transport identity escapes here.</summary>
    internal interface IInternalPlayerBackend
    {
        IEntity? Entity(ParticipantId id);
        bool Snapshot(ParticipantId id, out PlayerSnapshot? snapshot);
        bool Health(ParticipantId id, out PlayerHealthSnapshot? health);
        OperationResult<PlayerHealthSnapshot> Damage(ParticipantId id, PlayerDamageRequest request);
        OperationResult<PlayerHealthSnapshot> Heal(ParticipantId id, float amount, string source);
        OperationResult<bool> Teleport(ParticipantId id, Vec3 position, float? yaw);
        IPlayerInput Input(ParticipantId id);
        IPlayerUi Ui(ParticipantId id);
        OperationResult<IScoreboardColumn> Column(ScoreboardColumnDefinition definition);
    }

    /// <summary>Lets the provider bind Players to the exact consuming context, including a gamemode scope.</summary>
    internal interface IInternalPlayerContext
    {
        IPlayerDirectory CreatePlayers(IMultiplayerSession session, Func<ulong> generation);
    }

    /// <summary>
    /// Main-thread directory shared by runtime and testing. Generation, id and host-scoped key together name an
    /// incarnation. Retirement is permanent, even when a later list reuses all the same identifiers.
    /// </summary>
    internal sealed class PlayerDirectory : IPlayerDirectory, IDisposable
    {
        private readonly Func<MultiplayerSessionSnapshot> snapshot;
        private readonly Func<ulong> generation;
        private readonly IInternalPlayerBackend? backend;
        private readonly Action<Exception>? failed;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private readonly List<IPlayer> players = new List<IPlayer>();
        private readonly IReadOnlyList<IPlayer> view;
        private readonly Dictionary<ParticipantId, RemotePlayer> handles = new Dictionary<ParticipantId, RemotePlayer>();
        private readonly List<Action<IPlayer>> joined = new List<Action<IPlayer>>();
        private readonly List<Action<IPlayer>> left = new List<Action<IPlayer>>();
        private MultiplayerSessionSnapshot? seen;
        private ulong seenGeneration;
        private bool disposed;
        private bool refreshing;

        // failed hears what a join or leave handler threw, so its mod learns of it; the next handler still runs.
        internal PlayerDirectory(
            Func<MultiplayerSessionSnapshot> snapshot, Func<ulong> generation, IInternalPlayerBackend? backend = null, Action<Exception>? failed = null)
        {
            this.snapshot = snapshot;
            this.generation = generation;
            this.backend = backend;
            this.failed = failed;
            view = players.AsReadOnly();
            Refresh();
        }

        public IReadOnlyList<IPlayer> Players { get { Refresh(); return view; } }
        public IPlayer? Local
        {
            get
            {
                Refresh();
                for (var i = 0; i < players.Count; i++) if (((RemotePlayer)players[i]).Record.IsLocal) return players[i];
                return null;
            }
        }

        public bool TryGet(ParticipantId id, out IPlayer? player)
        {
            Refresh();
            var found = handles.TryGetValue(id, out var handle);
            player = handle;
            return found;
        }

        public IDisposable SubscribeJoined(Action<IPlayer> handler) => Subscribe(joined, handler);
        public IDisposable SubscribeLeft(Action<IPlayer> handler) => Subscribe(left, handler);
        public OperationResult<IScoreboardColumn> RegisterScoreboardColumn(ScoreboardColumnDefinition definition)
        {
            Refresh();
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            return !disposed && backend != null ? backend.Column(definition) : Unavailable<IScoreboardColumn>();
        }

        internal void Refresh()
        {
            CheckThread();
            if (disposed || refreshing) return;
            var current = snapshot();
            var epoch = generation();
            if (ReferenceEquals(current, seen) && epoch == seenGeneration) return;
            refreshing = true;
            var changedGeneration = epoch != seenGeneration;
            seen = current;
            seenGeneration = epoch;
            try
            {
                // Retire first; leave callbacks must never see their handle reappear as a different player.
                for (var i = players.Count - 1; i >= 0; i--)
                {
                    var handle = (RemotePlayer)players[i];
                    if (!changedGeneration && current.TryGetParticipant(handle.Id, out var record) && record!.PlayerKey.Equals(handle.Key))
                    {
                        handle.Record = record;
                        continue;
                    }
                    handle.Present = false;
                    handles.Remove(handle.Id);
                    players.RemoveAt(i);
                    Notify(left, handle);
                    if (disposed) return;
                }
                for (var i = 0; i < current.Participants.Count; i++)
                {
                    var record = current.Participants[i];
                    if (handles.ContainsKey(record.Id)) continue;
                    var handle = new RemotePlayer(this, record, backend);
                    handles.Add(record.Id, handle);
                    players.Add(handle);
                    Notify(joined, handle);
                    if (disposed) return;
                }
            }
            finally { refreshing = false; }
        }

        internal void CheckThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Use player handles on the game's main thread.");
        }

        public void Dispose()
        {
            CheckThread();
            if (disposed) return;
            disposed = true;
            for (var i = 0; i < players.Count; i++) ((RemotePlayer)players[i]).Present = false;
            players.Clear();
            handles.Clear();
            joined.Clear();
            left.Clear();
        }

        private IDisposable Subscribe(List<Action<IPlayer>> list, Action<IPlayer> handler)
        {
            Refresh();
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (disposed) throw new ObjectDisposedException(nameof(PlayerDirectory));
            list.Add(handler);
            return new Subscription(this, list, handler);
        }

        private void Notify(List<Action<IPlayer>> handlers, IPlayer player)
        {
            // Changes are rare. Copying permits callbacks to unsubscribe without skipping their neighbours.
            foreach (var callback in handlers.ToArray())
            {
                // One mod callback cannot prevent roster reconciliation, but its failure is reported, never dropped.
                try { callback(player); }
                catch (Exception exception) { Report(exception); }
            }
        }

        private void Report(Exception exception)
        {
            try { failed?.Invoke(exception); }
            catch (Exception) { /* A failing report cannot stop reconciliation either. */ }
        }

        internal static OperationResult<T> Unavailable<T>() where T : notnull =>
            OperationResult<T>.Failure(ModErrorCode.Unavailable, "Player services are unavailable; wait for the world to finish loading.");

        private sealed class Subscription : IDisposable
        {
            private readonly PlayerDirectory directory;
            private readonly List<Action<IPlayer>> list;
            private readonly Action<IPlayer> handler;
            internal Subscription(PlayerDirectory directory, List<Action<IPlayer>> list, Action<IPlayer> handler)
            { this.directory = directory; this.list = list; this.handler = handler; }
            public void Dispose() { directory.CheckThread(); list.Remove(handler); }
        }
    }
}
