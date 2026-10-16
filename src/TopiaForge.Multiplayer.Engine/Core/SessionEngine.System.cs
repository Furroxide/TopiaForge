using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// The runtime's system channels: one owner per kind on each side, and one stream per peer for each kind. A stream has
    /// a window its sender keeps to, credits from the receiver that open it again, and a generation that says when it
    /// restarted. A joined player also keeps to a rate, which its host enforces. See
    /// docs/internal/MultiplayerRuntimeIntegration.md.
    /// </summary>
    internal sealed partial class SessionEngine
    {
        /// <summary>How many system messages a host takes from one player at once, over all channels, before the rate applies.</summary>
        internal const int SystemIngressBurst = 4 * InternalSystemLimits.PlayerMessageBurst;

        /// <summary>The steady rate a host allows one player: twice what an honest player keeps to, so arrival bunching never trips it.</summary>
        internal const int SystemIngressPerSecond = 2 * InternalSystemLimits.PlayerMessagesPerSecond;

        private readonly SystemChannelEndpoint?[] systemChannels = new SystemChannelEndpoint?[SystemStreamSet.Slots];
        private long systemMessagesUnclaimed;
        private long systemMessagesDiscarded;
        private ulong systemGeneration;
        private ulong nextJoinGeneration;
        private ulong clientStreamGeneration;

        /// <summary>Gets the system messages that arrived for a channel nobody had open, or of a kind this engine does not know.</summary>
        internal long SystemMessagesUnclaimed => systemMessagesUnclaimed;

        /// <summary>
        /// Gets the stale system messages a host discarded without counting them on a channel: those a player sent for a
        /// session or a connection already gone, past the allowance within which the host counts them.
        /// </summary>
        internal long SystemMessagesDiscarded => systemMessagesDiscarded;

        /// <summary>Gets the number that changes whenever a system stream starts, restarts or ends.</summary>
        internal ulong SystemGeneration => systemGeneration;

        /// <summary>Gets who a joined player hears its host's system messages from: the host's player, or the headless host.</summary>
        private ParticipantId SystemHostSender => hostParticipantId ?? InternalSystemMessage.HeadlessHostSender;

        /// <summary>Opens the system channel of <paramref name="kind"/>; one owner at a time.</summary>
        internal OperationResult<IInternalSystemChannel> OpenSystemChannel(InternalSystemChannelKind kind)
        {
            RequireOwnerThread();
            if (!SystemStreamSet.IsKind((byte)kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (ended) return OperationResult<IInternalSystemChannel>.Failure(ModErrorCode.InvalidState, "The multiplayer session has ended.");
            if (systemChannels[(int)kind] != null)
            {
                return OperationResult<IInternalSystemChannel>.Failure(ModErrorCode.Conflict, "The " + kind + " system channel is already open.");
            }

            var endpoint = new SystemChannelEndpoint(this, kind);
            systemChannels[(int)kind] = endpoint;
            return OperationResult<IInternalSystemChannel>.Success(endpoint);
        }

        /// <summary>Gets the open channel of <paramref name="kind"/>, or null.</summary>
        internal SystemChannelEndpoint? FindSystemChannel(InternalSystemChannelKind kind) =>
            SystemStreamSet.IsKind((byte)kind) ? systemChannels[(int)kind] : null;

        internal void CloseSystemChannel(SystemChannelEndpoint endpoint) => Post(() =>
        {
            var slot = (int)endpoint.Kind;
            if (ReferenceEquals(systemChannels[slot], endpoint)) systemChannels[slot] = null;
            endpoint.Close();
        });

        /// <summary>Gets the generation of this engine's stream with <paramref name="peer"/>; zero when there is none.</summary>
        internal ulong SystemGenerationOf(ParticipantId peer)
        {
            RequireOwnerThread();
            if (Role == EngineRole.Host)
            {
                var connection = FindParticipant(peer)?.Connection;
                return connection != null && connection.IsJoined ? connection.StreamGeneration : 0;
            }

            var current = channel;
            var joined = current != null && current.Phase != ClientPhase.AwaitingJoin;
            return joined && SystemHostSender.Equals(peer) ? clientStreamGeneration : 0;
        }

        /// <summary>Gets whether a message of <paramref name="payloadBytes"/> bytes for <paramref name="target"/> would reach every selected peer now.</summary>
        internal bool CanSendSystem(SystemChannelEndpoint endpoint, InternalSystemTarget target, int payloadBytes)
        {
            RequireOwnerThread();
            if (payloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            if (CheckSend(endpoint, target, payloadBytes) != null) return false;
            if (Role != EngineRole.Host) return true;
            var kind = (byte)endpoint.Kind;
            foreach (var connection in connections)
            {
                var participant = connection.Participant;
                if (!connection.IsJoined || participant == null || !target.Selects(participant.Id)) continue;
                if (!HasHostRoom(connection, kind, payloadBytes)) return false;
            }

            return true;
        }

        /// <summary>
        /// Queues one system message. A host queues a copy for every joined connection the target selects, players still
        /// synchronizing included, whose window has room, and names each player it skipped in <paramref name="skipped"/>; a
        /// message to one player is refused whole instead. A client queues it for its host. It leaves at the next flush.
        /// </summary>
        internal OperationResult<int> SendSystem(
            SystemChannelEndpoint endpoint,
            ArraySegment<byte> payload,
            InternalSystemTarget target,
            ICollection<ParticipantId>? skipped)
        {
            RequireOwnerThread();
            if (payload.Array == null) throw new ArgumentNullException(nameof(payload));
            var kind = (byte)endpoint.Kind;
            var refused = CheckSend(endpoint, target, payload.Count);
            if (refused != null)
            {
                if (refused.ErrorCode == ModErrorCode.RateLimited)
                {
                    endpoint.CountRefused();
                    if (Role == EngineRole.Host) FindParticipant(target.Participant)?.Connection?.Streams.Get(kind).MarkBlocked(nowMicros);
                }

                return refused;
            }

            var bytes = new byte[payload.Count];
            Buffer.BlockCopy(payload.Array, payload.Offset, bytes, 0, payload.Count);
            if (Role == EngineRole.Host)
            {
                var recipients = 0;
                foreach (var connection in connections)
                {
                    var participant = connection.Participant;
                    if (!connection.IsJoined || participant == null || !target.Selects(participant.Id)) continue;
                    var stream = connection.Streams.Get(kind);
                    if (!stream.HasRoom(bytes.Length, InternalSystemLimits.HostWindowMessages, InternalSystemLimits.HostWindowBytes))
                    {
                        // This player's owner has not taken what it holds; everyone else still hears the message.
                        stream.MarkBlocked(nowMicros);
                        endpoint.CountRefused();
                        skipped?.Add(participant.Id);
                        continue;
                    }

                    stream.CountSent(bytes.Length);
                    connection.Enqueue(new SystemItem(epoch, connection.JoinGeneration, kind, bytes));
                    recipients++;
                }

                endpoint.CountSent(recipients);
                return OperationResult<int>.Success(recipients);
            }

            var current = channel!;
            SendRate(current).TryTake(nowMicros);
            current.Streams.Get(kind).CountSent(bytes.Length);
            current.SystemOutbox.Add(new SystemWireMessage(epoch, current.JoinGeneration, kind, bytes));
            endpoint.CountSent(1);
            return OperationResult<int>.Success(1);
        }

        /// <summary>Returns why a send cannot go now, or null when it can; a host's message to several players never waits on one.</summary>
        private OperationResult<int>? CheckSend(SystemChannelEndpoint endpoint, InternalSystemTarget target, int payloadBytes)
        {
            if (!endpoint.IsOpen) return OperationResult<int>.Failure(ModErrorCode.InvalidState, "The system channel is closed.");
            if (payloadBytes > InternalSystemLimits.MaximumPayloadBytes)
            {
                return OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "A system message carries at most 32 KiB; send larger data in chunks.");
            }

            // A transaction can still abort, and nothing sent from inside it may outlive that.
            if (scope.IsOpen) return OperationResult<int>.Failure(ModErrorCode.InvalidState, "System messages cannot be sent inside a transaction.");
            if (ended) return OperationResult<int>.Failure(ModErrorCode.InvalidState, "The multiplayer session has ended.");
            var kind = (byte)endpoint.Kind;
            if (Role == EngineRole.Host)
            {
                if (target.Kind == InternalSystemTargetKind.Host)
                {
                    return OperationResult<int>.Failure(ModErrorCode.InvalidArgument, "The host does not send system messages to itself.");
                }

                if (target.Kind != InternalSystemTargetKind.Only) return null;
                var connection = FindParticipant(target.Participant)?.Connection;
                return connection != null && connection.IsJoined && !HasHostRoom(connection, kind, payloadBytes)
                    ? OperationResult<int>.Failure(ModErrorCode.RateLimited, "The player has not taken the earlier system messages yet; send again once there is room.")
                    : null;
            }

            if (target.Kind != InternalSystemTargetKind.Host)
            {
                return OperationResult<int>.Failure(ModErrorCode.NotAuthoritative, "A joined player sends system messages to the host only.");
            }

            var current = channel;
            if (current == null || current.Phase == ClientPhase.AwaitingJoin)
            {
                return OperationResult<int>.Failure(ModErrorCode.Unavailable, "This player is not joined to a host.");
            }

            if (!current.Streams.Get(kind).HasRoom(payloadBytes, InternalSystemLimits.PlayerWindowMessages, InternalSystemLimits.PlayerWindowBytes))
            {
                return OperationResult<int>.Failure(ModErrorCode.RateLimited, "The host has not taken the earlier system messages yet; send again once there is room.");
            }

            return SendRate(current).CanTake(nowMicros)
                ? null
                : OperationResult<int>.Failure(ModErrorCode.RateLimited, "This player sends system messages faster than its host allows; send again shortly.");
        }

        private static bool HasHostRoom(HostConnection connection, byte kind, int payloadBytes) =>
            connection.Streams.Get(kind).HasRoom(payloadBytes, InternalSystemLimits.HostWindowMessages, InternalSystemLimits.HostWindowBytes);

        private TokenBucket SendRate(ClientChannel current) =>
            current.SystemSendRate ??= new TokenBucket(InternalSystemLimits.PlayerMessageBurst, InternalSystemLimits.PlayerMessagesPerSecond, nowMicros);

        /// <summary>Takes a system message that arrived on <paramref name="stream"/> from <paramref name="sender"/>.</summary>
        private void DeliverSystem(SystemStream stream, ParticipantId sender, byte[] payload)
        {
            var endpoint = systemChannels[stream.Kind];
            if (endpoint == null)
            {
                // Nobody owns the channel here, so the message is gone at once and its sender's window opens again.
                systemMessagesUnclaimed++;
                stream.Consume(payload.Length);
                return;
            }

            endpoint.Deliver(sender, payload, stream);
        }

        /// <summary>
        /// Takes a message of a kind this build has no channel for, from a newer peer: it is unclaimed, and credited as taken
        /// at once, so a minor protocol version can add a channel without stalling an older peer's streams.
        /// </summary>
        private void TakeUnknownKind(SystemStreamSet streams, byte kind, int payloadBytes)
        {
            systemMessagesUnclaimed++;
            var stream = streams.GetUnknown(kind);
            stream.CountReceived(payloadBytes);
            stream.Consume(payloadBytes);
        }

        private void HandleSystemFromClient(HostConnection connection, ProtocolReader body)
        {
            SystemMessages.ReadHeader(body, out var sentEpoch, out var generation, out var kind);
            var participant = connection.Participant;
            if (!connection.IsJoined || participant == null)
            {
                SystemMessages.SkipPayload(body);
                CountDroppedSystem(kind);
                return;
            }

            // A message sent before the player saw a replacement, or before it rejoined, belongs to a stream already gone.
            // An honest player can have a window in flight on every stream each time and cannot know, so stale messages never
            // count against its rate; they have an allowance of their own, past which the host discards them uncounted.
            if (sentEpoch != epoch || generation != connection.JoinGeneration)
            {
                SystemMessages.SkipPayload(body);
                var stale = connection.StaleSystemIngress ??= new TokenBucket(SystemIngressBurst, SystemIngressPerSecond, nowMicros);
                if (stale.TryTake(nowMicros)) CountDroppedSystem(kind);
                else systemMessagesDiscarded++;
                return;
            }

            // The rate counts every message the player sends for its current streams, whatever becomes of it.
            var ingress = connection.SystemIngress ??= new TokenBucket(SystemIngressBurst, SystemIngressPerSecond, nowMicros);
            if (!ingress.TryTake(nowMicros)) throw new ProtocolException("A player sent system messages faster than the host allows.");
            if (!SystemStreamSet.IsKind(kind))
            {
                TakeUnknownKind(connection.Streams, kind, SystemMessages.SkipPayload(body));
                return;
            }

            var payload = SystemMessages.ReadPayload(body);
            var stream = connection.Streams.Get(kind);
            if (!stream.FitsWindow(payload.Length, InternalSystemLimits.PlayerWindowMessages, InternalSystemLimits.PlayerWindowBytes))
            {
                throw new ProtocolException("A player sent more system messages than its window allows.");
            }

            stream.CountReceived(payload.Length);
            DeliverSystem(stream, participant.Id, payload);
        }

        private void HandleSystemFromHost(ClientChannel current, ProtocolReader body)
        {
            SystemMessages.ReadHeader(body, out var sentEpoch, out var generation, out var kind);
            if (sentEpoch != epoch || generation != current.JoinGeneration)
            {
                SystemMessages.SkipPayload(body);
                CountDroppedSystem(kind);
                return;
            }

            if (!SystemStreamSet.IsKind(kind))
            {
                TakeUnknownKind(current.Streams, kind, SystemMessages.SkipPayload(body));
                return;
            }

            var payload = SystemMessages.ReadPayload(body);
            var stream = current.Streams.Get(kind);
            if (!stream.FitsWindow(payload.Length, InternalSystemLimits.HostWindowMessages, InternalSystemLimits.HostWindowBytes))
            {
                throw new ProtocolException("The host sent more system messages than its window allows.");
            }

            stream.CountReceived(payload.Length);

            // The host's world still arriving is progress for a readiness hold.
            if (current.ReadinessHeld) readinessProgressMicros = nowMicros;
            DeliverSystem(stream, SystemHostSender, payload);
        }

        private void HandleSystemCreditFromClient(HostConnection connection, ProtocolReader body)
        {
            var credit = SystemMessages.ReadCredit(body);
            if (!connection.IsJoined || credit.Epoch != epoch || credit.Generation != connection.JoinGeneration) return;
            ApplyCredit(connection.Streams, credit);
        }

        private void HandleSystemCreditFromHost(ClientChannel current, ProtocolReader body)
        {
            var credit = SystemMessages.ReadCredit(body);
            if (credit.Epoch != epoch || credit.Generation != current.JoinGeneration) return;
            ApplyCredit(current.Streams, credit);
        }

        private static void ApplyCredit(SystemStreamSet streams, SystemCreditMessage credit)
        {
            if (!SystemStreamSet.IsKind(credit.Kind))
            {
                if (credit.Consumed != 0 || credit.ConsumedBytes != 0) throw new ProtocolException("A peer credited a system channel it was never sent.");
                return;
            }

            streams.Get(credit.Kind).ApplyCredit(credit.Consumed, credit.ConsumedBytes);
        }

        private void CountDroppedSystem(byte kind)
        {
            var endpoint = SystemStreamSet.IsKind(kind) ? systemChannels[kind] : null;
            if (endpoint != null) endpoint.CountDropped();
            else systemMessagesUnclaimed++;
        }

        /// <summary>Drops what every channel holds: the session or the connection it arrived on is gone.</summary>
        private void DropPendingSystemMessages()
        {
            foreach (var endpoint in systemChannels) endpoint?.DropPending();
        }

        /// <summary>Drops what every channel holds from <paramref name="sender"/>, whose stream restarted when it rejoined.</summary>
        private void DropSystemMessagesFrom(ParticipantId sender)
        {
            foreach (var endpoint in systemChannels) endpoint?.DropFrom(sender);
        }

        /// <summary>Counts what a lost client link never sent: the owner was told it was queued, so it must not vanish unseen.</summary>
        private void CountUnsentSystem(ClientChannel lost)
        {
            foreach (var message in lost.SystemOutbox) CountDroppedSystem(message.Kind);
            lost.SystemOutbox.Clear();
        }

        /// <summary>Counts what a closed connection never sent.</summary>
        private void CountUnsentSystem(HostConnection closed)
        {
            foreach (var item in closed.Queue)
            {
                if (item is SystemItem system) CountDroppedSystem(system.Kind);
            }
        }

        private void WriteSystemOutbox(ClientChannel current)
        {
            if (current.SystemOutbox.Count > 0)
            {
                foreach (var message in current.SystemOutbox)
                {
                    SystemMessages.Write(clientFrame, clientBody, message.Epoch, message.Generation, message.Kind, message.Payload);
                    if (clientFrame.Length >= ProtocolLimits.SoftFrameBytes) SendClientFrame(current);
                }

                current.SystemOutbox.Clear();
            }

            if (current.Streams.CreditsDue) current.Streams.WriteCredits(clientFrame, clientBody, epoch, current.JoinGeneration);
        }
    }
}
