using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    /// <summary>The runtime's own channels between peers, apart from any mod's contract.</summary>
    internal enum InternalSystemChannelKind : byte
    {
        /// <summary>The host's world: spawns, baselines and replica state.</summary>
        WorldReplication = 1,

        /// <summary>Players' avatars and poses.</summary>
        Avatars = 2,

        /// <summary>Text chat. A server-muted player's messages are dropped when they reach the host.</summary>
        Chat = 3,

        /// <summary>The host's scene and gamemode, which joined players follow.</summary>
        SceneFollow = 4,

        /// <summary>
        /// The session's time, which joined players follow and only the host sends: a joined player's send is refused, and a
        /// message a player sends the host anyway ends its connection as a protocol violation.
        /// </summary>
        Time = 5
    }

    /// <summary>Who a system message goes to.</summary>
    internal enum InternalSystemTargetKind
    {
        /// <summary>The host. The only target a joined player may use.</summary>
        Host = 0,

        /// <summary>Every joined player. Host only.</summary>
        Everyone = 1,

        /// <summary>One joined player. Host only.</summary>
        Only = 2,

        /// <summary>Every joined player but one. Host only.</summary>
        AllExcept = 3
    }

    /// <summary>The recipients of one system message.</summary>
    internal readonly struct InternalSystemTarget : IEquatable<InternalSystemTarget>
    {
        private InternalSystemTarget(InternalSystemTargetKind kind, ParticipantId participant)
        {
            Kind = kind;
            Participant = participant;
        }

        internal static InternalSystemTarget Host => new InternalSystemTarget(InternalSystemTargetKind.Host, default);

        internal static InternalSystemTarget Everyone => new InternalSystemTarget(InternalSystemTargetKind.Everyone, default);

        internal InternalSystemTargetKind Kind { get; }

        /// <summary>Gets the named player for <see cref="InternalSystemTargetKind.Only"/> and <see cref="InternalSystemTargetKind.AllExcept"/>.</summary>
        internal ParticipantId Participant { get; }

        internal static InternalSystemTarget Only(ParticipantId participant) =>
            new InternalSystemTarget(InternalSystemTargetKind.Only, Require(participant));

        internal static InternalSystemTarget AllExcept(ParticipantId participant) =>
            new InternalSystemTarget(InternalSystemTargetKind.AllExcept, Require(participant));

        /// <summary>Returns whether a host's message to this target reaches <paramref name="participant"/>.</summary>
        internal bool Selects(ParticipantId participant)
        {
            switch (Kind)
            {
                case InternalSystemTargetKind.Everyone:
                    return true;
                case InternalSystemTargetKind.Only:
                    return participant.Equals(Participant);
                case InternalSystemTargetKind.AllExcept:
                    return !participant.Equals(Participant);
                default:
                    return false;
            }
        }

        public bool Equals(InternalSystemTarget other) => Kind == other.Kind && Participant.Equals(other.Participant);

        public override bool Equals(object? obj) => obj is InternalSystemTarget other && Equals(other);

        public override int GetHashCode() => ((int)Kind * 397) ^ Participant.GetHashCode();

        public override string ToString() => Participant.IsValid ? Kind + "(" + Participant + ")" : Kind.ToString();

        private static ParticipantId Require(ParticipantId participant)
        {
            if (!participant.IsValid) throw new ArgumentException("A participant is required.", nameof(participant));
            return participant;
        }
    }

    /// <summary>One received system message. The receiver owns the payload.</summary>
    internal readonly struct InternalSystemMessage
    {
        internal InternalSystemMessage(ParticipantId sender, byte[] payload)
        {
            Sender = sender;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        /// <summary>
        /// Gets the sender a joined player sees on what a host without a player of its own sends, such as a dedicated
        /// server. No host gives a player this id.
        /// </summary>
        internal static ParticipantId HeadlessHostSender { get; } = new ParticipantId("headless-host");

        /// <summary>
        /// Gets who sent it: the joined player, on a host; on a joined player, the host's player, or
        /// <see cref="HeadlessHostSender"/> when the host has none.
        /// </summary>
        internal ParticipantId Sender { get; }

        internal byte[] Payload { get; }
    }

    /// <summary>One datagram received on a system channel's realtime lane. It lives until the next pump; copy what you keep.</summary>
    internal readonly struct InternalSystemDatagram
    {
        internal InternalSystemDatagram(ParticipantId sender, ArraySegment<byte> payload)
        {
            if (payload.Array == null) throw new ArgumentNullException(nameof(payload));
            Sender = sender;
            Payload = payload;
        }

        /// <summary>Gets who sent it, as for a system message: the joined player on a host, the host's player on a joined player.</summary>
        internal ParticipantId Sender { get; }

        internal ArraySegment<byte> Payload { get; }
    }

    /// <summary>
    /// Bounds every system channel applies. Each stream, one channel between this process and one peer, has a window: the
    /// messages and bytes its sender may have outstanding, sent but not yet taken by the receiving owner. The receiver
    /// reports what its owner took, and the sender sends only within what is left, so a receiver never holds more than a
    /// window for any one sender and an honest sender is never dropped. A full window holds back only its own stream.
    /// </summary>
    internal static class InternalSystemLimits
    {
        /// <summary>
        /// How long a Ready player's window on a channel may stay full, with the host waiting to send and the player crediting
        /// nothing, before the host ends the player as a protocol violation.
        /// </summary>
        internal const int StallSeconds = 120;

        /// <summary>The largest payload one message carries: 32 KiB. A baseline larger than that is sent in chunks.</summary>
        internal const int MaximumPayloadBytes = 32 * 1024;

        /// <summary>The messages a host may have outstanding to one player on one channel.</summary>
        internal const int HostWindowMessages = 1024;

        /// <summary>The payload bytes a host may have outstanding to one player on one channel: 1 MiB.</summary>
        internal const int HostWindowBytes = 1024 * 1024;

        /// <summary>The messages one player may have outstanding to its host on one channel.</summary>
        internal const int PlayerWindowMessages = 64;

        /// <summary>The payload bytes one player may have outstanding to its host on one channel: 64 KiB.</summary>
        internal const int PlayerWindowBytes = 64 * 1024;

        /// <summary>The messages a joined player may send at once over all channels, before <see cref="PlayerMessagesPerSecond"/> applies.</summary>
        internal const int PlayerMessageBurst = 256;

        /// <summary>The steady rate at which a joined player may send messages over all channels.</summary>
        internal const int PlayerMessagesPerSecond = 256;

        /// <summary>
        /// The largest payload one datagram on a channel's realtime lane carries: 1 KiB, so it fits one transport packet with
        /// the engine's header.
        /// </summary>
        internal const int MaximumDatagramBytes = 1024;

        /// <summary>The datagrams a joined player may send at once over all channels, before <see cref="PlayerDatagramsPerSecond"/> applies.</summary>
        internal const int PlayerDatagramBurst = 64;

        /// <summary>
        /// The steady rate at which a joined player may send datagrams over all channels: poses at 20 a second, and room for
        /// the samples of a held body. Its host takes twice that and drops the rest, uncounted against the player's link.
        /// </summary>
        internal const int PlayerDatagramsPerSecond = 64;

        /// <summary>The datagrams one channel keeps for its owner; past it the oldest goes, as on any unreliable lane.</summary>
        internal const int DatagramQueueLength = 256;
    }

    /// <summary>
    /// One of the runtime's own channels between peers. Messages travel reliably and in order within a stream: the
    /// channel between this process and one peer, from the moment that peer joined or the session was last replaced. A
    /// stream restarts whenever the player rejoins, even within the grace period that keeps its session, and whenever the
    /// host replaces the session; what was in flight on the old stream is lost, and what the channel held from it is
    /// dropped and counted. <see cref="Generation"/> and <see cref="GetGeneration"/> say when that happened, so an owner
    /// that keeps state in step with a peer, such as world replication or session follow, starts it again from a
    /// baseline. Main thread only.
    /// </summary>
    /// <remarks>
    /// A host sends to joined players, including players still synchronizing, so a joining player can receive the world
    /// before it is Ready. A joined player sends only to the host. Each stream has a window
    /// (<see cref="InternalSystemLimits"/>): a message is never queued past it, never dropped later, and the owner sends
    /// again once <see cref="CanSend"/> says there is room. One player's full window holds back only its own stream: a
    /// message to everyone goes to every other player. A peer that passes its window, a player that sends faster than
    /// <see cref="InternalSystemLimits.PlayerMessagesPerSecond"/> allows, and a Ready player that leaves a window full for
    /// <see cref="InternalSystemLimits.StallSeconds"/> break the protocol and lose their connection. Nobody may rely on the
    /// channel for a message a peer sent before this channel was opened.
    /// </remarks>
    internal interface IInternalSystemChannel : IDisposable
    {
        InternalSystemChannelKind Kind { get; }

        /// <summary>Gets whether the channel is open; disposing it closes it.</summary>
        bool IsOpen { get; }

        /// <summary>
        /// Gets a number that changes whenever a stream through this channel starts, restarts or ends: on a joined player,
        /// when it joins, rejoins, is moved to a replaced session or leaves; on a host, when any player does. Compare it
        /// each frame, and ask <see cref="GetGeneration"/> about the peers when it changes.
        /// </summary>
        ulong Generation { get; }

        /// <summary>
        /// Gets the generation of the stream between this process and <paramref name="peer"/>: on a host, a joined player;
        /// on a joined player, the host. It is zero when there is no such stream, and it changes whenever the stream
        /// restarts, so a changed value means the peer needs a new baseline.
        /// </summary>
        ulong GetGeneration(ParticipantId peer);

        /// <summary>
        /// Gets whether a message of <paramref name="payloadBytes"/> bytes for <paramref name="target"/> would reach every
        /// selected peer now: within each one's window and, on a joined player, within its rate.
        /// </summary>
        bool CanSend(InternalSystemTarget target, int payloadBytes);

        /// <summary>
        /// Queues one message, copied, for the target's peers, and returns how many it was queued for: zero in single player
        /// or when the target selects nobody. It goes out at the frame's flush, and reaches every one of them unless their
        /// stream restarts first.
        /// </summary>
        /// <remarks>
        /// A host's message to <see cref="InternalSystemTargetKind.Everyone"/> or <see cref="InternalSystemTargetKind.AllExcept"/>
        /// goes to each selected player whose window has room. A player whose window is full is skipped and added to
        /// <paramref name="skipped"/>: it misses this message, so its owner starts it again from a baseline before sending
        /// it more. A message to one player, or a joined player's to its host, is refused whole with
        /// <see cref="ModErrorCode.RateLimited"/> when that window is full or a joined player is over its rate.
        /// </remarks>
        OperationResult<int> Send(ArraySegment<byte> payload, InternalSystemTarget target, ICollection<ParticipantId>? skipped = null);

        /// <summary>Takes the oldest received message. Taking it opens its sender's window again.</summary>
        bool TryReceive(out InternalSystemMessage message);

        /// <summary>
        /// Sends one datagram, copied, on the channel's realtime lane: unreliable and unordered, never held behind the
        /// stream's messages and never sent again. It may be lost, arrive more than once or overtake an older one, so the
        /// owner stamps what it needs to tell them apart. It carries the stream's epoch and join generation, so one sent on
        /// an earlier stream is dropped on arrival. It leaves at once and returns how many peers it went to. A host sends to
        /// joined players; a joined player sends to its host only, within <see cref="InternalSystemLimits.PlayerDatagramsPerSecond"/>,
        /// past which it is refused with <see cref="ModErrorCode.RateLimited"/>. The <see cref="InternalSystemChannelKind.Time"/>
        /// channel has no lane.
        /// </summary>
        OperationResult<int> SendDatagram(ArraySegment<byte> payload, InternalSystemTarget target);

        /// <summary>
        /// Takes the oldest datagram received on the realtime lane. Its payload lives until the next pump, so the owner reads
        /// it in its frame stage and copies what it keeps.
        /// </summary>
        bool TryReceiveDatagram(out InternalSystemDatagram datagram);

        InternalSystemChannelStatistics GetStatistics();
    }

    /// <summary>One system channel's counters.</summary>
    internal sealed class InternalSystemChannelStatistics
    {
        internal InternalSystemChannelStatistics(InternalSystemChannelKind kind, long sent, long received, long dropped, int pending, long refused)
            : this(kind, sent, received, dropped, pending, refused, 0, 0, 0)
        {
        }

        internal InternalSystemChannelStatistics(
            InternalSystemChannelKind kind,
            long sent,
            long received,
            long dropped,
            int pending,
            long refused,
            long datagramsSent,
            long datagramsReceived,
            long datagramsDropped)
        {
            Kind = kind;
            Sent = sent;
            Received = received;
            Dropped = dropped;
            Pending = pending;
            Refused = refused;
            DatagramsSent = datagramsSent;
            DatagramsReceived = datagramsReceived;
            DatagramsDropped = datagramsDropped;
        }

        internal InternalSystemChannelKind Kind { get; }

        /// <summary>Gets the messages queued for peers, one per recipient.</summary>
        internal long Sent { get; }

        /// <summary>Gets the messages taken in for this channel's owner.</summary>
        internal long Received { get; }

        /// <summary>
        /// Gets the messages dropped: one from a stream that restarted or a session already replaced, one waiting when the
        /// channel closed, one never sent because its link was lost, or one from a muted sender.
        /// </summary>
        internal long Dropped { get; }

        /// <summary>Gets the messages waiting for the owner.</summary>
        internal int Pending { get; }

        /// <summary>
        /// Gets the sends refused because a window was full or a joined player was over its rate, and the players a message
        /// to everyone skipped because their window was full, one per player.
        /// </summary>
        internal long Refused { get; }

        /// <summary>Gets the datagrams sent on the realtime lane, one per recipient.</summary>
        internal long DatagramsSent { get; }

        /// <summary>Gets the datagrams taken in for this channel's owner.</summary>
        internal long DatagramsReceived { get; }

        /// <summary>
        /// Gets the datagrams dropped: one from a stream already gone, one past the sender's rate, one the queue pushed out,
        /// or one waiting when the channel closed or its stream restarted.
        /// </summary>
        internal long DatagramsDropped { get; }
    }
}
