using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// One body's host machine and its players on a virtual clock, linked as the runtime's ordered system channel links
    /// them: each direction is a queue with a fixed one-way delay, in order, losing nothing while the link lasts. The host
    /// handles each message as it arrives, answers a request at once with its number, limits grabs as the default
    /// request policy does, polls once a frame, and announces every change of owner to every connected player at the end
    /// of the frame. A link can close, losing what is in flight both ways, and come back with the world baseline; and the
    /// host's inbox can drop a player's requests, as a channel past its bounds does, without the player knowing.
    /// </summary>
    internal sealed class OwnershipRig
    {
        public const long StepMicros = 1_000;
        public const long HostFrameMicros = 16_000;
        public static readonly NetQuantizedPosition Near = new NetQuantizedPosition(0, 0, 0);

        private readonly List<RigPlayer> players = new List<RigPlayer>();
        private readonly List<RigReply> announcements = new List<RigReply>();
        private readonly PlayerRequestLimiter limiter = new PlayerRequestLimiter(PermissionPolicy.Default);
        private readonly Dictionary<int, int> droppedRequests = new Dictionary<int, int>();
        private readonly Dictionary<int, long> cooldownsBefore = new Dictionary<int, long>();

        public OwnershipRig(OwnershipPolicy? policy = null)
        {
            Host = new OwnershipStateMachine(policy);
        }

        public OwnershipStateMachine Host { get; }

        public long Now { get; private set; }

        /// <summary>Who won each new hold, in order: grants that changed the epoch, steals included.</summary>
        public List<int> Grantees { get; } = new List<int>();

        /// <summary>When each of <see cref="Grantees"/> won its hold.</summary>
        public List<long> GrantTimes { get; } = new List<long>();

        /// <summary>
        /// Whether a closed link only reclaims the body, as the delivery contract once said, instead of
        /// <see cref="OwnershipStateMachine.LinkClosed"/>; for showing what that let a griefer do.
        /// </summary>
        public bool LinkCloseOnlyReclaims { get; set; }

        /// <summary>Every reclaim the host made, with the participant it took the body from.</summary>
        public List<(int Participant, OwnershipReclaimReason Reason)> Reclaims { get; } = new List<(int, OwnershipReclaimReason)>();

        /// <summary>The participants that were made to yield, each time a cooldown started.</summary>
        public List<int> Yields { get; } = new List<int>();

        /// <summary>How many requests the host's inbox dropped.</summary>
        public int DroppedRequests { get; private set; }

        public void Add(RigPlayer player)
        {
            players.Add(player);
            player.Rig = this;
        }

        /// <summary>The host's inbox drops the player's next <paramref name="count"/> requests, and nobody tells the player.</summary>
        public void DropRequests(RigPlayer player, int count) => droppedRequests[player.Participant] = count;

        /// <summary>Runs until <paramref name="untilMicros"/>, a millisecond at a time.</summary>
        public void Run(long untilMicros)
        {
            while (Now < untilMicros)
            {
                Now += StepMicros;
                foreach (var player in players) player.DeliverDue(Now);
                foreach (var player in players) HandleArrivals(player);
                foreach (var player in players) player.Act(Now);
                foreach (var player in players) HandleArrivals(player);
                if (Now % HostFrameMicros == 0) EndFrame();
            }
        }

        /// <summary>
        /// The player's link closes: what is in flight both ways is lost, and the host takes back a body the player
        /// holds, as the delivery contract says.
        /// </summary>
        public void CloseLink(RigPlayer player)
        {
            player.Disconnect();
            var participant = player.Participant;
            Remember();
            if (!LinkCloseOnlyReclaims) Note(Host.LinkClosed(participant, Now), participant);
            else if (Host.State != OwnershipState.Server && Host.Owner.Participant == participant) Note(Host.Reclaim(), participant);
        }

        /// <summary>The player comes back on a new link, and the runtime gives it the body's owner and epoch from the baseline.</summary>
        public void Rejoin(RigPlayer player)
        {
            player.Reconnect();
            player.Reply(RigReply.Changed(Host.Owner, Host.Epoch), Now);
        }

        private void HandleArrivals(RigPlayer player)
        {
            while (player.TryTakeArrived(Now, out var message)) Handle(player, message);
        }

        private void Handle(RigPlayer player, RigMessage message)
        {
            var participant = player.Participant;
            Remember();
            OwnershipOutcome outcome;
            switch (message.Kind)
            {
                case RigMessageKind.Request:
                    if (droppedRequests.TryGetValue(participant, out var drops) && drops > 0)
                    {
                        droppedRequests[participant] = drops - 1;
                        DroppedRequests++;
                        return;
                    }

                    var permission = limiter.Evaluate(participant, false, NetRequestKind.MotionGrab, NetTargetRelation.Server, 0, Now);
                    if (permission != NetPermission.Allowed)
                    {
                        player.Reply(RigReply.Refused(message.Number, Host.Epoch, Host.Owner), Now);
                        return;
                    }

                    outcome = Host.Request(participant, message.Epoch, message.Body, message.Avatar, Now);
                    var granted = outcome.Result == OwnershipResult.Granted || outcome.Result == OwnershipResult.AlreadyOwned;
                    player.Reply(granted ? RigReply.Granted(message.Number, outcome.Epoch) : RigReply.Refused(message.Number, outcome.Epoch, outcome.Owner), Now);
                    if (outcome.Result == OwnershipResult.Granted && outcome.EpochChanged)
                    {
                        Grantees.Add(participant);
                        GrantTimes.Add(Now);
                    }

                    break;
                case RigMessageKind.Sample:
                    outcome = Host.Stream(participant, message.Epoch, message.Body, message.Avatar, Now);
                    break;
                case RigMessageKind.Throw:
                    outcome = Host.BeginRelease(participant, message.Epoch, message.Avatar, Now);
                    break;
                default:
                    outcome = Host.Handback(participant, message.Epoch, new MotionHandback(message.Body, NetVector3.Zero, NetVector3.Zero), message.Avatar, Now, out _);
                    break;
            }

            Note(outcome, participant);
        }

        private void EndFrame()
        {
            var owner = Host.Owner.Participant;
            Remember();
            Note(Host.Poll(Now), owner);
            foreach (var announcement in announcements)
            {
                foreach (var player in players) player.Reply(announcement, Now);
            }

            announcements.Clear();
        }

        /// <summary>Notes every player's cooldown before an event, so the event's yields can be told apart.</summary>
        private void Remember()
        {
            foreach (var player in players) cooldownsBefore[player.Participant] = Host.CooldownOf(player.Participant).UntilMicros;
        }

        private void Note(OwnershipOutcome outcome, int sender)
        {
            if (outcome.EpochChanged) announcements.Add(RigReply.Changed(outcome.Owner, outcome.Epoch));
            if (outcome.Result == OwnershipResult.Reclaimed || outcome.Result == OwnershipResult.Rejected)
            {
                Reclaims.Add((sender, outcome.ReclaimReason));
            }

            foreach (var player in players)
            {
                if (Host.CooldownOf(player.Participant).UntilMicros > cooldownsBefore[player.Participant]) Yields.Add(player.Participant);
            }
        }
    }

    internal enum RigMessageKind
    {
        Request,
        Sample,
        Throw,
        Handback
    }

    /// <summary>
    /// A message to the host: the epoch it names, a request's number, and the body's and the sender's avatar's positions.
    /// </summary>
    internal readonly struct RigMessage
    {
        public RigMessage(RigMessageKind kind, uint epoch, NetQuantizedPosition body, NetQuantizedPosition avatar, uint number = 0)
        {
            Kind = kind;
            Epoch = epoch;
            Body = body;
            Avatar = avatar;
            Number = number;
        }

        public RigMessageKind Kind { get; }

        public uint Epoch { get; }

        public NetQuantizedPosition Body { get; }

        public NetQuantizedPosition Avatar { get; }

        public uint Number { get; }

        public static RigMessage Of(RigMessageKind kind, uint epoch) => new RigMessage(kind, epoch, OwnershipRig.Near, OwnershipRig.Near);

        public static RigMessage Of(OwnershipRequest request) =>
            new RigMessage(RigMessageKind.Request, request.Epoch, OwnershipRig.Near, OwnershipRig.Near, request.Number);
    }

    /// <summary>A reply from the host: an answer to the request it names, or an announcement of a new owner.</summary>
    internal readonly struct RigReply
    {
        private RigReply(bool isAnswer, bool granted, uint number, uint epoch, NetOwner owner)
        {
            IsAnswer = isAnswer;
            IsGranted = granted;
            Number = number;
            Epoch = epoch;
            Owner = owner;
        }

        public bool IsAnswer { get; }

        public bool IsGranted { get; }

        /// <summary>The number of the request an answer settles.</summary>
        public uint Number { get; }

        public uint Epoch { get; }

        public NetOwner Owner { get; }

        public static RigReply Granted(uint number, uint epoch) => new RigReply(true, true, number, epoch, default);

        public static RigReply Refused(uint number, uint epoch, NetOwner owner) => new RigReply(true, false, number, epoch, owner);

        public static RigReply Changed(NetOwner owner, uint epoch) => new RigReply(false, false, 0, epoch, owner);

        /// <summary>Hands the reply to a claim as the runtime would.</summary>
        public void DeliverTo(OwnershipClaim claim)
        {
            if (!IsAnswer) claim.OnOwnerChanged(Owner, Epoch);
            else if (IsGranted) claim.OnGranted(Number, Epoch);
            else claim.OnRefused(Number, Epoch, Owner);
        }
    }

    /// <summary>
    /// A player on the rig: its link's two ordered queues, each with the same one-way delay, and its own behaviour, which
    /// receives the host's replies in order and acts once a step. A closed link loses what is in flight both ways and
    /// carries nothing until the player rejoins.
    /// </summary>
    internal abstract class RigPlayer
    {
        private readonly Queue<(long At, RigMessage Message)> toHost = new Queue<(long, RigMessage)>();
        private readonly Queue<(long At, RigReply Reply)> toPlayer = new Queue<(long, RigReply)>();

        protected RigPlayer(int participant, long roundTripMicros)
        {
            if (roundTripMicros < 0 || roundTripMicros % (2 * OwnershipRig.StepMicros) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(roundTripMicros));
            }

            Participant = participant;
            OneWayMicros = roundTripMicros / 2;
        }

        public OwnershipRig Rig { get; set; } = null!;

        public int Participant { get; }

        public long OneWayMicros { get; }

        public bool Connected { get; private set; } = true;

        public void Send(RigMessage message, long now)
        {
            if (Connected) toHost.Enqueue((now + OneWayMicros, message));
        }

        public void Reply(RigReply reply, long now)
        {
            if (Connected) toPlayer.Enqueue((now + OneWayMicros, reply));
        }

        public bool TryTakeArrived(long now, out RigMessage message)
        {
            if (toHost.Count > 0 && toHost.Peek().At <= now)
            {
                message = toHost.Dequeue().Message;
                return true;
            }

            message = default;
            return false;
        }

        public void DeliverDue(long now)
        {
            while (toPlayer.Count > 0 && toPlayer.Peek().At <= now) Receive(toPlayer.Dequeue().Reply, now);
        }

        /// <summary>The link closes: whatever is in flight either way is gone.</summary>
        public virtual void Disconnect()
        {
            Connected = false;
            toHost.Clear();
            toPlayer.Clear();
        }

        public virtual void Reconnect() => Connected = true;

        public abstract void Act(long now);

        protected abstract void Receive(RigReply reply, long now);
    }

    /// <summary>
    /// A player who uses an <see cref="OwnershipClaim"/>: it grabs whenever it does not hold the body, streams at 30 Hz
    /// while it simulates, throws after holding for a while, and hands back when its window closes. It polls its claim
    /// every step and resets it when its link closes or comes back, as the delivery contract says.
    /// </summary>
    internal sealed class ClaimPlayer : RigPlayer
    {
        public const long SampleIntervalMicros = 33_333;
        private readonly long holdMicros;
        private long holdingSince = -1;
        private long nextSample;

        public ClaimPlayer(int participant, long roundTripMicros, long holdMicros = 500_000, long grabFromMicros = 0)
            : base(participant, roundTripMicros)
        {
            this.holdMicros = holdMicros;
            GrabFromMicros = grabFromMicros;
            Claim = new OwnershipClaim(participant);
        }

        public OwnershipClaim Claim { get; }

        /// <summary>When the player starts reaching for the body.</summary>
        public long GrabFromMicros { get; set; }

        /// <summary>Whether the player keeps grabbing the body once it has let go.</summary>
        public bool GrabsAgain { get; set; } = true;

        /// <summary>How many times a reply left this player holding a body the host confirmed.</summary>
        public int ConfirmedHolds { get; private set; }

        public override void Act(long now)
        {
            Claim.Poll(now);
            if (Claim.State == OwnershipState.Server && now >= GrabFromMicros && (GrabsAgain || ConfirmedHolds == 0) && Claim.Grab(now, out var request))
            {
                Send(RigMessage.Of(request), now);
                nextSample = now;
            }

            if (Claim.IsSimulating)
            {
                if (holdingSince < 0) holdingSince = now;
                if (now >= nextSample)
                {
                    Send(RigMessage.Of(RigMessageKind.Sample, Claim.StreamEpoch), now);
                    nextSample = now + SampleIntervalMicros;
                }

                if (Claim.State == OwnershipState.Owned && now - holdingSince >= holdMicros && Claim.BeginRelease(now))
                {
                    Send(RigMessage.Of(RigMessageKind.Throw, Claim.StreamEpoch), now);
                }

                if (Claim.TryCompleteHandback(now, out var handbackEpoch)) Send(RigMessage.Of(RigMessageKind.Handback, handbackEpoch), now);
            }

            if (!Claim.IsSimulating) holdingSince = -1;
        }

        public override void Disconnect()
        {
            base.Disconnect();
            Claim.Reset(OwnershipClaimRecovery.LinkClosed);
        }

        public override void Reconnect()
        {
            base.Reconnect();
            Claim.Reset(OwnershipClaimRecovery.Reconnected);
        }

        protected override void Receive(RigReply reply, long now)
        {
            var before = Claim.State;
            reply.DeliverTo(Claim);
            if (Claim.State == OwnershipState.Owned && before != OwnershipState.Owned) ConfirmedHolds++;
        }
    }
}
