using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// The system channels' realtime lane: unreliable datagrams beside each channel's ordered stream, on
    /// <see cref="EngineChannel.Realtime"/>, for traffic that only the newest copy of matters, such as players' poses.
    /// Nothing waits for a window, nothing is sent again, and a datagram from an earlier stream is dropped by its epoch and
    /// join generation. A joined player keeps to its rate; its host takes twice that and drops the rest, so bunched
    /// arrivals never cost an honest player its link. Sending and taking allocate nothing beyond the link's own copy. See
    /// docs/internal/MultiplayerRuntimeIntegration.md, section 5.
    /// </summary>
    internal sealed partial class SessionEngine
    {
        /// <summary>How many datagrams a host takes from one player at once, over all channels, before the rate applies.</summary>
        internal const int DatagramIngressBurst = 2 * InternalSystemLimits.PlayerDatagramBurst;

        /// <summary>The steady rate a host takes from one player: twice what an honest player keeps to.</summary>
        internal const int DatagramIngressPerSecond = 2 * InternalSystemLimits.PlayerDatagramsPerSecond;

        // What a send answers, made once for the counts a session reaches: datagrams go many times a second, and a result
        // never changes, so sending allocates nothing.
        private static readonly OperationResult<int>[] SentResults = CreateSentResults(64);

        private readonly byte[] datagramScratch = new byte[SystemDatagrams.MaximumHeaderBytes + InternalSystemLimits.MaximumDatagramBytes];
        private long systemDatagramsUnclaimed;
        private long systemDatagramsMalformed;

        /// <summary>Gets the datagrams that arrived for a channel nobody had open, for the time channel, or of a kind this engine does not know.</summary>
        internal long SystemDatagramsUnclaimed => systemDatagramsUnclaimed;

        /// <summary>Gets the datagrams whose header did not parse, or whose payload passed the bound.</summary>
        internal long SystemDatagramsMalformed => systemDatagramsMalformed;

        /// <summary>
        /// Sends one datagram on <paramref name="endpoint"/>'s realtime lane, at once. A host sends a copy to every joined
        /// connection the target selects, players still synchronizing included; a client sends to its host, within its rate.
        /// </summary>
        internal OperationResult<int> SendSystemDatagram(SystemChannelEndpoint endpoint, ArraySegment<byte> payload, InternalSystemTarget target)
        {
            RequireOwnerThread();
            if (payload.Array == null) throw new ArgumentNullException(nameof(payload));
            var refused = CheckDatagram(endpoint, target, payload.Count);
            if (refused != null)
            {
                if (refused.ErrorCode == ModErrorCode.RateLimited) endpoint.CountRefused();
                return refused;
            }

            var kind = (byte)endpoint.Kind;
            if (Role == EngineRole.Host)
            {
                var recipients = 0;
                foreach (var connection in connections)
                {
                    var participant = connection.Participant;
                    if (!connection.IsJoined || participant == null || !target.Selects(participant.Id)) continue;
                    if (connection.Link.State == EngineLinkState.Closed) continue;
                    var length = SystemDatagrams.Write(datagramScratch, epoch, connection.JoinGeneration, kind, payload);
                    connection.Link.Send(EngineChannel.Realtime, new ArraySegment<byte>(datagramScratch, 0, length));
                    recipients++;
                }

                endpoint.CountDatagramsSent(recipients);
                return SentTo(recipients);
            }

            var current = channel!;
            DatagramRate(current).TryTake(nowMicros);
            var written = SystemDatagrams.Write(datagramScratch, epoch, current.JoinGeneration, kind, payload);
            current.Link.Send(EngineChannel.Realtime, new ArraySegment<byte>(datagramScratch, 0, written));
            endpoint.CountDatagramsSent(1);
            return SentTo(1);
        }

        private static OperationResult<int> SentTo(int recipients) =>
            recipients < SentResults.Length ? SentResults[recipients] : OperationResult<int>.Success(recipients);

        private static OperationResult<int>[] CreateSentResults(int largest)
        {
            var results = new OperationResult<int>[largest + 1];
            for (var count = 0; count <= largest; count++) results[count] = OperationResult<int>.Success(count);
            return results;
        }

        /// <summary>Returns why a datagram cannot go now, or null when it can.</summary>
        private OperationResult<int>? CheckDatagram(SystemChannelEndpoint endpoint, InternalSystemTarget target, int payloadBytes)
        {
            if (!endpoint.IsOpen) return OperationResult<int>.Failure(ModErrorCode.InvalidState, "The system channel is closed.");
            if (endpoint.Kind == InternalSystemChannelKind.Time)
            {
                return OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "The session's time has no datagram lane; the host sends it as messages.");
            }

            if (payloadBytes > InternalSystemLimits.MaximumDatagramBytes)
            {
                return OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "A datagram carries at most 1 KiB; send larger data as messages.");
            }

            // A transaction can still abort, and nothing sent from inside it may outlive that.
            if (scope.IsOpen) return OperationResult<int>.Failure(ModErrorCode.InvalidState, "Datagrams cannot be sent inside a transaction.");
            if (ended) return OperationResult<int>.Failure(ModErrorCode.InvalidState, "The multiplayer session has ended.");
            if (Role == EngineRole.Host)
            {
                return target.Kind == InternalSystemTargetKind.Host
                    ? OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "The host does not send datagrams to itself.")
                    : null;
            }

            if (target.Kind != InternalSystemTargetKind.Host)
            {
                return OperationResult<int>.Failure(ModErrorCode.NotAuthoritative, "A joined player sends datagrams to the host only.");
            }

            var current = channel;
            if (current == null || current.Phase == ClientPhase.AwaitingJoin || current.Link.State == EngineLinkState.Closed)
            {
                return OperationResult<int>.Failure(ModErrorCode.Unavailable, "This player is not joined to a host.");
            }

            return DatagramRate(current).CanTake(nowMicros)
                ? null
                : OperationResult<int>.Failure(ModErrorCode.RateLimited, "This player sends datagrams faster than its host takes them; send again shortly.");
        }

        private TokenBucket DatagramRate(ClientChannel current) =>
            current.DatagramSendRate ??= new TokenBucket(InternalSystemLimits.PlayerDatagramBurst, InternalSystemLimits.PlayerDatagramsPerSecond, nowMicros);

        /// <summary>
        /// Takes a datagram from a joined player: one for its current stream, within the host's rate, for a channel the host
        /// has open. Anything else is dropped and counted; a datagram never ends a connection, since it may be late or
        /// repeated by the path.
        /// </summary>
        private void HandleDatagramFromClient(HostConnection connection, byte[] frame)
        {
            if (!SystemDatagrams.TryReadHeader(frame, out var sentEpoch, out var generation, out var kind, out var offset))
            {
                systemDatagramsMalformed++;
                return;
            }

            var participant = connection.Participant;
            if (!connection.IsJoined || participant == null || sentEpoch != epoch || generation != connection.JoinGeneration)
            {
                CountDroppedDatagram(kind);
                return;
            }

            var ingress = connection.DatagramIngress ??= new TokenBucket(DatagramIngressBurst, DatagramIngressPerSecond, nowMicros);
            if (!ingress.TryTake(nowMicros))
            {
                CountDroppedDatagram(kind);
                return;
            }

            DeliverDatagram(kind, participant.Id, new ArraySegment<byte>(frame, offset, frame.Length - offset));
        }

        /// <summary>Takes a datagram from the host, once joined, for the current stream.</summary>
        private void HandleDatagramFromHost(ClientChannel current, byte[] frame)
        {
            if (!SystemDatagrams.TryReadHeader(frame, out var sentEpoch, out var generation, out var kind, out var offset))
            {
                systemDatagramsMalformed++;
                return;
            }

            if (current.Phase == ClientPhase.AwaitingJoin || sentEpoch != epoch || generation != current.JoinGeneration)
            {
                CountDroppedDatagram(kind);
                return;
            }

            DeliverDatagram(kind, SystemHostSender, new ArraySegment<byte>(frame, offset, frame.Length - offset));
        }

        private void DeliverDatagram(byte kind, ParticipantId sender, ArraySegment<byte> payload)
        {
            // The session's time travels as the host's messages alone; a datagram for it is nobody's.
            var endpoint = kind != TimeKind && SystemStreamSet.IsKind(kind) ? systemChannels[kind] : null;
            if (endpoint == null)
            {
                systemDatagramsUnclaimed++;
                return;
            }

            endpoint.DeliverDatagram(sender, payload);
        }

        private void CountDroppedDatagram(byte kind)
        {
            var endpoint = kind != TimeKind && SystemStreamSet.IsKind(kind) ? systemChannels[kind] : null;
            if (endpoint != null) endpoint.CountDatagramDropped();
            else systemDatagramsUnclaimed++;
        }
    }
}
