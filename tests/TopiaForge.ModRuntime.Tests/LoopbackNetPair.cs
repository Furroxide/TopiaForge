using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>
    /// A listen host and its joined players over an in-memory network that keeps the runtime's system-channel contract:
    /// each stream is first in, first out; a sender keeps to a window per stream that the receiver's owner credits back
    /// at the receiver's next flush; a message to everyone skips a player whose window is full and names it; a message to
    /// one player, or a player's own, is refused whole when its window is full; a generation changes whenever a stream
    /// starts, restarts or ends; and what an ended stream had in flight or waiting is dropped. A message leaves at its
    /// sender's flush and arrives at its receiver's next pump. The ModRuntime and ModManager harnesses both compile it.
    /// </summary>
    internal sealed class LoopbackNetPair
    {
        private readonly List<LoopbackControl> players = new List<LoopbackControl>();
        private long nextStream = 1;

        internal LoopbackNetPair()
        {
            Host = new LoopbackControl(this, new ParticipantId("host"), true);
        }

        internal LoopbackControl Host { get; }

        internal IReadOnlyList<LoopbackControl> Players => players;

        /// <summary>Gets or sets the host's window per player and channel, in messages: the engine's 1,024 by default.</summary>
        internal int HostWindowMessages { get; set; } = InternalSystemLimits.HostWindowMessages;

        /// <summary>Gets or sets the host's window per player and channel, in bytes: the engine's 1 MiB by default.</summary>
        internal int HostWindowBytes { get; set; } = InternalSystemLimits.HostWindowBytes;

        internal LoopbackControl AddPlayer(string id)
        {
            var player = new LoopbackControl(this, new ParticipantId(id), false);
            players.Add(player);
            return player;
        }

        /// <summary>The host opens its session: its role is a listen host from its next pump.</summary>
        internal void StartHosting() => Host.NextRole = InternalSessionRole.ListenHost;

        /// <summary>The host stops: every player's stream ends and the players are back in single player.</summary>
        internal void StopHosting()
        {
            foreach (var player in players)
            {
                if (player.NextRole == InternalSessionRole.Client) Leave(player);
            }

            Host.NextRole = InternalSessionRole.Solo;
        }

        /// <summary>A player's link comes up and the host accepts it: a stream starts on both sides.</summary>
        internal void Join(LoopbackControl player)
        {
            if (player.IsHost) throw new InvalidOperationException("The host does not join itself.");
            player.NextRole = InternalSessionRole.Client;
            StartStream(player);
        }

        /// <summary>A player leaves: its stream ends on both sides and the player is back in single player.</summary>
        internal void Leave(LoopbackControl player)
        {
            EndStream(player);
            player.NextRole = InternalSessionRole.Solo;
        }

        /// <summary>A player rejoins within its grace: the session goes on, but its stream restarts and what was in flight is lost.</summary>
        internal void Rejoin(LoopbackControl player)
        {
            EndStream(player);
            StartStream(player);
        }

        /// <summary>The host replaces the session: every player's stream restarts.</summary>
        internal void ReplaceSession()
        {
            foreach (var player in players)
            {
                if (player.StreamId == 0) continue;
                EndStream(player);
                StartStream(player);
            }
        }

        private void StartStream(LoopbackControl player)
        {
            var stream = nextStream++;
            player.StreamId = stream;
            player.BumpGeneration();
            Host.BumpGeneration();
            foreach (var channel in Host.Channels) channel.StartStream(player.Id, stream);
            foreach (var channel in player.Channels) channel.StartStream(Host.Id, stream);
        }

        private void EndStream(LoopbackControl player)
        {
            if (player.StreamId == 0) return;
            player.StreamId = 0;
            player.BumpGeneration();
            Host.BumpGeneration();
            foreach (var channel in Host.Channels) channel.EndStream(player.Id);
            foreach (var channel in player.Channels) channel.EndStream(Host.Id);
        }

        internal LoopbackControl? Find(ParticipantId id)
        {
            if (Host.Id.Equals(id)) return Host;
            foreach (var player in players)
            {
                if (player.Id.Equals(id)) return player;
            }

            return null;
        }
    }

    /// <summary>One peer's control over the loopback network.</summary>
    internal sealed class LoopbackControl : IInternalMultiplayerControl
    {
        private readonly LoopbackNetPair network;
        private readonly Dictionary<InternalSystemChannelKind, LoopbackChannel> channels = new Dictionary<InternalSystemChannelKind, LoopbackChannel>();
        private ulong generation;
        private int holds;

        internal LoopbackControl(LoopbackNetPair network, ParticipantId id, bool isHost)
        {
            this.network = network;
            Id = id;
            IsHost = isHost;
        }

        internal ParticipantId Id { get; }

        internal bool IsHost { get; }

        internal LoopbackNetPair Network => network;

        internal long StreamId { get; set; }

        /// <summary>Gets or sets the role the next pump takes, as the provider's own role changes at a pump.</summary>
        internal InternalSessionRole NextRole { get; set; }

        internal ulong Generation => generation;

        internal IEnumerable<LoopbackChannel> Channels => channels.Values;

        internal IInternalEntityIdentitySource? IdentitySource { get; private set; }

        internal IInternalMultiplayerRuntimeHost? Runtime { get; private set; }

        internal int Holds => holds;

        internal int HoldsTaken { get; private set; }

        internal int ProgressReports { get; private set; }

        /// <summary>Gets or sets whether the next pump delivers nothing, as a stalled link would.</summary>
        internal bool Stalled { get; set; }

        /// <summary>Gets the channel of <paramref name="kind"/>, if it is open.</summary>
        internal LoopbackChannel? Channel(InternalSystemChannelKind kind) => channels.TryGetValue(kind, out var channel) && channel.IsOpen ? channel : null;

        public bool IsLiveAvailable => true;

        public InternalSessionRole Role { get; private set; }

        public bool IsReadinessHeld => holds > 0;

        public IInternalVoiceService? Voice => null;

        public IReadOnlyList<InternalLanListing> LanSessions => Array.Empty<InternalLanListing>();

        public bool AttachRuntime(IInternalMultiplayerRuntimeHost runtime)
        {
            if (Runtime != null) return ReferenceEquals(Runtime, runtime);
            Runtime = runtime;
            return true;
        }

        public void DetachRuntime(IInternalMultiplayerRuntimeHost runtime)
        {
            if (ReferenceEquals(Runtime, runtime)) Runtime = null;
        }

        public void Pump()
        {
            // As the provider's control does, a change of role tells the runtime whether the game must keep running in the
            // background: online it must, and in single player the game's own setting comes back.
            if (Role != NextRole) Runtime?.SetRunInBackground(NextRole != InternalSessionRole.Solo);
            Role = NextRole;
            if (Stalled) return;
            foreach (var channel in channels.Values) channel.Deliver();
        }

        public void Flush()
        {
            foreach (var channel in channels.Values) channel.Transmit(network);
        }

        internal void BumpGeneration() => generation++;

        public InternalLiveStatus GetStatus() => new InternalLiveStatus
        {
            Role = Role,
            IsLiveAvailable = true,
            IsReadinessHeld = holds > 0,
            HostPhase = Role == InternalSessionRole.ListenHost ? InternalHostPhase.Hosting : InternalHostPhase.Idle
        };

        public OperationResult<bool> StartHosting(InternalHostSettings settings)
        {
            if (!IsHost) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Only the loopback host hosts.");
            network.StartHosting();
            return OperationResult<bool>.Success(true);
        }

        public void StopHosting()
        {
            if (IsHost) network.StopHosting();
        }

        public bool TryCreateJoinTarget(string text, out IInternalJoinTarget? target)
        {
            target = null;
            return false;
        }

        public OperationResult<bool> Join(IInternalJoinTarget target, InternalJoinSettings settings)
        {
            if (IsHost) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The loopback host does not join.");
            network.Join(this);
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> SupplyPassword(string password) => Unsupported<bool>();

        public void Leave()
        {
            if (!IsHost && NextRole == InternalSessionRole.Client) network.Leave(this);
        }

        public IInternalServerQuery Query(IInternalJoinTarget target, bool allowDirect) => throw new NotSupportedException();

        public void StartBrowsing()
        {
        }

        public void StopBrowsing()
        {
        }

        public OperationResult<bool> Kick(ParticipantId participant, string message) => Unsupported<bool>();

        public OperationResult<bool> Ban(ParticipantId participant) => Unsupported<bool>();

        public OperationResult<bool> Unban(string banId) => Unsupported<bool>();

        public IReadOnlyList<InternalBanEntry> GetBans() => Array.Empty<InternalBanEntry>();

        public OperationResult<bool> SetServerMute(ParticipantId participant, bool muted) => Unsupported<bool>();

        public bool IsServerMuted(ParticipantId participant) => false;

        public bool IsParticipantReady(ParticipantId participant) => false;

        public OperationResult<bool> ReplaceSession()
        {
            if (!IsHost) return OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, "Only the host replaces the session.");
            network.ReplaceSession();
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<IInternalSystemChannel> OpenSystemChannel(InternalSystemChannelKind kind)
        {
            if (channels.TryGetValue(kind, out var existing) && existing.IsOpen)
            {
                return OperationResult<IInternalSystemChannel>.Failure(ModErrorCode.Conflict, "The channel is already open.");
            }

            var channel = new LoopbackChannel(this, kind);
            channels[kind] = channel;
            if (IsHost)
            {
                foreach (var player in network.Players)
                {
                    if (player.StreamId != 0) channel.StartStream(player.Id, player.StreamId);
                }
            }
            else if (StreamId != 0)
            {
                channel.StartStream(network.Host.Id, StreamId);
            }

            return OperationResult<IInternalSystemChannel>.Success(channel);
        }

        public IInternalReadinessHold HoldReadiness(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A hold names its reason.", nameof(reason));
            holds++;
            HoldsTaken++;
            return new Hold(this);
        }

        public IDisposable SetEntityIdentitySource(IInternalEntityIdentitySource source)
        {
            if (IdentitySource != null) throw new InvalidOperationException("An entity identity source is already set.");
            IdentitySource = source ?? throw new ArgumentNullException(nameof(source));
            return new Lease(() =>
            {
                if (ReferenceEquals(IdentitySource, source)) IdentitySource = null;
            });
        }

        public IDisposable SetAvatarSource(IInternalAvatarSource source) => new Lease(() => { });

        public InternalMultiplayerStatistics GetStatistics() => new InternalMultiplayerStatistics { Role = Role };

        public OperationResult<bool> CreateInvite() => Unsupported<bool>();

        public OperationResult<bool> RevokeInvite(string code) => Unsupported<bool>();

        public OperationResult<IInternalLaunchPlan> PlanLaunchSession(InternalLaunchSession session) => Unsupported<IInternalLaunchPlan>();

        internal LoopbackControl? Peer(ParticipantId id) => network.Find(id);

        private static OperationResult<T> Unsupported<T>() where T : notnull =>
            OperationResult<T>.Failure(ModErrorCode.Unavailable, "The loopback control does not do this.");

        private sealed class Hold : IInternalReadinessHold
        {
            private LoopbackControl? owner;

            internal Hold(LoopbackControl owner)
            {
                this.owner = owner;
            }

            public void ReportProgress()
            {
                if (owner != null) owner.ProgressReports++;
            }

            public void Dispose()
            {
                var current = owner;
                owner = null;
                if (current != null) current.holds--;
            }
        }

        private sealed class Lease : IDisposable
        {
            private Action? release;

            internal Lease(Action release)
            {
                this.release = release;
            }

            public void Dispose()
            {
                var current = release;
                release = null;
                current?.Invoke();
            }
        }
    }
}
