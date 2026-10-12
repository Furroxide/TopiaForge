using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        private readonly List<HostConnection> connections = new List<HostConnection>();
        private readonly List<ParticipantRecord> participantRecords = new List<ParticipantRecord>();
        private ulong nextParticipantNumber;
        private bool participantsDirty;

        /// <summary>
        /// Gets how many participant records a host keeps, connected or within their grace period: room for every
        /// admitted player and as many recently departed ones, within the bound every snapshot and peer enforces.
        /// </summary>
        private int ParticipantRecordLimit => Math.Min(ProtocolLimits.MaximumParticipants, options.MaximumParticipants * 2);

        private bool HostHasPendingWork
        {
            get
            {
                foreach (var connection in connections)
                {
                    if (connection.HasOutbound || connection.HasPendingDeltas) return true;
                    if (participantsDirty && connection.IsJoined) return true;
                }

                return false;
            }
        }

        /// <summary>Accepts a link from a joining client. Its first frame must be a join request.</summary>
        internal void Accept(IEngineLink link)
        {
            if (link == null) throw new ArgumentNullException(nameof(link));
            RequireOwnerThread();
            if (Role != EngineRole.Host) throw new InvalidOperationException("Only a host accepts links.");
            if (ended)
            {
                link.Close();
                return;
            }

            connections.Add(new HostConnection(link) { NextClockSyncAt = nowMicros });
        }

        /// <summary>
        /// Replaces the session in place: registrations survive, values return to their declared defaults, objects
        /// despawn, and every connected client resynchronizes.
        /// </summary>
        internal void ReplaceSession(MultiplayerSessionId newSessionId, SessionSeed newSeed)
        {
            RequireOwnerThread();
            if (Role != EngineRole.Host) throw new InvalidOperationException("Only a host replaces the session.");
            if (scope.IsOpen) throw new InvalidOperationException("The session cannot be replaced inside a transaction.");
            if (ended) throw new ObjectDisposedException(nameof(SessionEngine));
            if (!newSessionId.IsValid || newSessionId.Equals(sessionId))
            {
                throw new ArgumentException("The replacement session id must differ from the current session id.", nameof(newSessionId));
            }

            var previous = sessionCancellation;
            sessionCancellation = new CancellationTokenSource();
            sessionId = newSessionId;
            seed = newSeed;
            epoch++;
            // Tokens already handed out stay readable, so the retired source is cancelled but never disposed.
            RetireSessionToken(previous);
            foreach (var record in stateList)
            {
                record.ResetTo(record.InitialBytes, 0);
                MarkStateChanged(record);
            }

            DespawnAllLocally();
            nextObjectNumber = 0;
            foreach (var binding in commands.Values) binding.ResetForNewSession();
            presentationDeliveries.Clear();
            // Submission sequences keep counting, so nothing sent in the old session can pass for the new one. Results
            // already queued still reach their clients ahead of the replacement.
            var replaced = new SessionReplacedMessage(sessionId.Value, seed.Value, epoch);
            SnapshotItem? snapshotItem = null;
            foreach (var connection in connections)
            {
                if (!connection.IsJoined) continue;
                connection.ResetForNewSession();
                connection.Enqueue(new SessionReplacedItem(replaced));
                connection.Enqueue(snapshotItem ??= CaptureSnapshot());
                connection.Phase = HostConnectionPhase.Accepted;
            }

            MarkParticipantsChanged();
            EndOperation();
        }

        /// <summary>Ends one participant's connection with <paramref name="reason"/>.</summary>
        internal bool Kick(ParticipantId participant, MultiplayerSessionEndReason reason, string message)
        {
            RequireOwnerThread();
            if (scope.IsOpen) throw new InvalidOperationException("A participant cannot be removed inside a transaction.");
            var connection = FindParticipant(participant)?.Connection;
            if (connection == null) return false;
            EndConnection(connection, reason, message);
            EndOperation();
            return true;
        }

        internal bool IsConnectedParticipant(ParticipantId id)
        {
            if (Role == EngineRole.Client) return FindClientParticipant(id)?.IsConnected ?? false;
            foreach (var record in participantRecords)
            {
                if (record.Id.Equals(id)) return record.IsConnected;
            }

            return false;
        }

        private ParticipantEntry? FindClientParticipant(ParticipantId id)
        {
            foreach (var entry in clientParticipants)
            {
                if (string.Equals(entry.Id, id.Value, StringComparison.Ordinal)) return entry;
            }

            return null;
        }

        private void InitializeHost(ParticipantId? hostParticipant, string hostDisplayName)
        {
            if (hostParticipant.HasValue)
            {
                if (ProcessKind != MultiplayerProcessKind.Interactive)
                {
                    throw new ArgumentException("A headless host has no local participant.", nameof(hostParticipant));
                }

                participantRecords.Add(new ParticipantRecord(hostParticipant.Value, null, hostDisplayName, isHost: true) { IsConnected = true });
                localParticipantId = hostParticipant;
                hostParticipantId = hostParticipant;
            }

            MarkSessionChanged(participantsChanged: true);
        }

        private List<ParticipantEntry> HostParticipantEntries()
        {
            var entries = new List<ParticipantEntry>(participantRecords.Count);
            foreach (var record in participantRecords) entries.Add(record.ToEntry());
            return entries;
        }

        /// <summary>
        /// Marks the participant list changed: the snapshot rebuilds, and each joined connection receives the new list
        /// once, at the next flush, however many changes happened before it.
        /// </summary>
        private void MarkParticipantsChanged()
        {
            participantsDirty = true;
            MarkSessionChanged(participantsChanged: true);
        }

        private void PumpHost()
        {
            RunDueSteps();
            if (connections.Count > 0)
            {
                foreach (var connection in connections.ToArray()) ProcessConnection(connection);
            }

            ExpireDepartedParticipants();
            ScheduleClockSync();
            FlushHost();
        }

        private void RunDueSteps()
        {
            var target = Clock.StepsIn(nowMicros - originMicros);
            if (target <= tick) return;
            var catchUp = (ulong)options.MaximumCatchUpSteps;
            if (target - tick > catchUp) tick = target - catchUp;
            while (tick < target)
            {
                tick++;
                MarkSessionChanged();
                RunStepHandlers();
                Settle();
            }
        }

        private void RunStepHandlers()
        {
            var handlers = tickSubscriberArray;
            if (handlers.Length == 0 || state != MultiplayerSessionState.Ready) return;
            var context = new MultiplayerTickContext(CurrentTick, Clock.StepSeconds);
            foreach (var handler in handlers)
            {
                if (!tickSubscribers.Contains(handler)) continue;
                OpenTransaction(TransactionKind.Canonical, localParticipantId ?? default);
                var committed = false;
                try
                {
                    handler(context);
                    CommitCanonical();
                    committed = true;
                }
                catch (Exception exception) when (options.CallbackFailures == EngineCallbackFailureMode.Report)
                {
                    Report(new EngineDiagnostic(EngineDiagnosticKind.CallbackFailed, "A tick handler threw; its changes were rolled back.", null, exception));
                }
                finally
                {
                    if (!committed) AbortCanonical();
                    CloseTransaction();
                }
            }
        }

        private void ProcessConnection(HostConnection connection)
        {
            var linkState = connection.Link.State;
            if (linkState != EngineLinkState.Closed && linkState != connection.ReportedLinkState)
            {
                connection.ReportedLinkState = linkState;
                if (connection.IsJoined) MarkParticipantsChanged();
            }

            while (connection.Phase != HostConnectionPhase.Closed && connection.Link.TryReceive(out var channelKind, out var frame))
            {
                if (channelKind != EngineChannel.Session) continue;
                ProcessHostFrame(connection, frame);
                Settle();
            }

            if (connection.Phase != HostConnectionPhase.Closed && connection.Link.State == EngineLinkState.Closed)
            {
                DropConnection(connection);
                Settle();
            }
        }

        private void ProcessHostFrame(HostConnection connection, byte[] frame)
        {
            try
            {
                if (frame.Length > ProtocolLimits.MaximumFrameBytes) throw new ProtocolException("A frame exceeded the protocol bound.");
                var reader = new ProtocolReader(frame);
                while (!reader.AtEnd && connection.Phase != HostConnectionPhase.Closed && connection.Phase != HostConnectionPhase.Rejected)
                {
                    var type = reader.ReadVarUInt();
                    var body = reader.Slice(reader.ReadLength(ProtocolLimits.MaximumFrameBytes));
                    HandleHostMessage(connection, type, body);
                }
            }
            catch (ProtocolException exception)
            {
                Report(new EngineDiagnostic(
                    EngineDiagnosticKind.ProtocolViolation,
                    "A client violated the session protocol: " + exception.Message,
                    connection.Participant?.Id));
                EndConnection(connection, MultiplayerSessionEndReason.ProtocolMismatch, "The client violated the session protocol.");
            }
        }

        private void HandleHostMessage(HostConnection connection, ulong type, ProtocolReader body)
        {
            if (connection.Phase == HostConnectionPhase.AwaitingJoin && type != MessageType.JoinRequest && !MessageType.IsIgnorable(type))
            {
                throw new ProtocolException("A client sent session traffic before joining.");
            }

            switch (type)
            {
                case MessageType.JoinRequest:
                    if (connection.Phase != HostConnectionPhase.AwaitingJoin) throw new ProtocolException("A client asked to join twice.");
                    HandleJoin(connection, JoinMessages.ReadJoinRequest(body));
                    break;
                case MessageType.ReadyAck:
                    if (body.ReadVarUInt() == epoch && connection.Phase == HostConnectionPhase.Accepted) connection.Phase = HostConnectionPhase.Ready;
                    break;
                case MessageType.Command:
                    HandleSubmission(connection, ReplicationMessages.ReadCommand(body, connection.InIntern), isCommand: true);
                    break;
                case MessageType.ObjectInput:
                    HandleSubmission(connection, ReplicationMessages.ReadObjectInput(body), isCommand: false);
                    break;
                case MessageType.Leave:
                    DropConnection(connection);
                    break;
                case MessageType.ClockSyncReply:
                    ObserveRoundTrip(connection, body.ReadVarUInt());
                    break;
                default:
                    if (!MessageType.IsIgnorable(type)) throw new ProtocolException("A client sent an unknown critical message.");
                    break;
            }
        }

        private void HandleJoin(HostConnection connection, JoinRequestMessage request)
        {
            var identity = connection.Link.PeerIdentity;
            ParticipantRecord? record = null;
            foreach (var candidate in participantRecords)
            {
                if (string.Equals(candidate.Identity, identity, StringComparison.Ordinal)) record = candidate;
            }

            if (record?.Connection != null && !ReferenceEquals(record.Connection, connection))
            {
                EndConnection(record.Connection, MultiplayerSessionEndReason.Replaced, "The same player joined from another connection.");
            }

            var connected = 0;
            foreach (var candidate in participantRecords)
            {
                if (candidate.IsConnected) connected++;
            }

            var decision = (options.JoinPolicy ?? DefaultJoinPolicy.Instance).Evaluate(
                new JoinRequestContext(identity, request, RegisteredContracts(), connected, options.MaximumParticipants));
            if (decision.Accepted && record == null) decision = AdmitNewParticipant(identity, request.DisplayName, out record) ?? decision;
            if (!decision.Accepted || record == null)
            {
                connection.Phase = HostConnectionPhase.Rejected;
                connection.Enqueue(new JoinRejectedItem(new JoinRejectedMessage(decision.Reason, decision.Message, decision.Report)));
                connection.CloseAfterFlush = true;
                return;
            }

            record.DisplayName = request.DisplayName;
            record.Connection = connection;
            record.IsConnected = true;
            record.DepartedAtMicros = 0;
            record.PlayerKey = connection.Link.PlayerKey.IsValid ? connection.Link.PlayerKey.Value : null;
            connection.Participant = record;
            connection.Phase = HostConnectionPhase.Accepted;
            connection.AcknowledgedSequence = record.LastSequence;
            connection.Enqueue(new JoinAcceptedItem(new JoinAcceptedMessage
            {
                SessionId = sessionId.Value,
                Seed = seed.Value,
                Epoch = epoch,
                ParticipantId = record.Id.Value,
                HostParticipantId = hostParticipantId?.Value,
                Tick = tick,
                StepRateHz = Clock.RateHz,
                NextSequence = record.LastSequence + 1,
                Report = decision.Report
            }));
            connection.Enqueue(CaptureSnapshot());
            connection.ClearPendingDeltas();
            MarkParticipantsChanged();
        }

        /// <summary>
        /// Creates the record for an identity the host has not seen within the grace period, or returns why it cannot.
        /// When the records are at their limit, the longest-departed participant makes room; if every record is
        /// connected, the join is refused.
        /// </summary>
        private JoinDecision? AdmitNewParticipant(string identity, string displayName, out ParticipantRecord? record)
        {
            record = null;
            var id = AllocateParticipantId(identity);
            if (!id.HasValue) return JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "The host could not assign a participant id.");
            if (participantRecords.Count >= ParticipantRecordLimit && !EvictLongestDepartedParticipant())
            {
                return JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "The session is full.");
            }

            record = new ParticipantRecord(id.Value, identity, displayName, isHost: false);
            participantRecords.Add(record);
            return null;
        }

        private bool EvictLongestDepartedParticipant()
        {
            ParticipantRecord? evicted = null;
            foreach (var candidate in participantRecords)
            {
                if (candidate.IsConnected || candidate.Identity == null) continue;
                if (evicted == null || candidate.DepartedAtMicros < evicted.DepartedAtMicros) evicted = candidate;
            }

            if (evicted == null) return false;
            participantRecords.Remove(evicted);
            MarkParticipantsChanged();
            return true;
        }

        private ParticipantId? AllocateParticipantId(string identity)
        {
            ParticipantId id;
            if (options.ParticipantIdForIdentity != null)
            {
                id = options.ParticipantIdForIdentity(identity);
            }
            else
            {
                do
                {
                    id = new ParticipantId("p" + (++nextParticipantNumber).ToString(CultureInfo.InvariantCulture));
                }
                while (FindParticipant(id) != null);
            }

            return id.IsValid && FindParticipant(id) == null ? id : (ParticipantId?)null;
        }

        private ParticipantRecord? FindParticipant(ParticipantId id)
        {
            foreach (var record in participantRecords)
            {
                if (record.Id.Equals(id)) return record;
            }

            return null;
        }

        private void HandleSubmission(HostConnection connection, SubmissionMessage message, bool isCommand)
        {
            var record = connection.Participant!;
            WireOutcome outcome;
            if (connection.Phase != HostConnectionPhase.Ready)
            {
                outcome = WireOutcome.Failure(
                    ModErrorCode.InvalidState,
                    "Submissions wait until the connection acknowledges the session snapshot.");
            }
            else if (message.Epoch != epoch)
            {
                outcome = WireOutcome.Failure(ModErrorCode.Conflict, "The submission belongs to an earlier session.");
            }
            else if (message.Sequence <= record.LastSequence)
            {
                outcome = WireOutcome.Failure(ModErrorCode.Conflict, "The server rejected a stale or duplicate submission sequence.");
            }
            else
            {
                record.LastSequence = message.Sequence;
                outcome = isCommand
                    ? ExecuteCommand(record.Id, new WireKey(message.Target, message.WireId), message.Sequence, message.Payload)
                    : ExecuteObjectInput(record.Id, message.Target, message.Sequence, message.Payload);
            }

            if (connection.Phase == HostConnectionPhase.Closed) return;
            connection.Enqueue(new ResultItem(
                isCommand ? MessageType.CommandResult : MessageType.ObjectInputResult,
                message.Sequence,
                tick,
                outcome));
            connection.AcknowledgedSequence = record.LastSequence;
            connection.ForceFlush = true;
            connection.CommitNeeded = true;
        }

        private ulong NextHostLocalSequence(ParticipantId sender) => ++FindParticipant(sender)!.LastSequence;

        private WireOutcome ExecuteCommand(ParticipantId sender, WireKey key, ulong sequence, byte[] payload)
        {
            if (!commands.TryGetValue(key, out var binding) || !binding.IsActive)
            {
                return WireOutcome.Failure(
                    ModErrorCode.NotFound,
                    "The canonical server has no matching registration for command '" + key.Id + "'.");
            }

            if (payload.Length > binding.MaximumPayloadBytes)
            {
                return WireOutcome.Failure(ModErrorCode.InvalidArgument, "The command payload exceeded its declared bound.");
            }

            if (!binding.TryConsumeRate(sender, nowMicros))
            {
                return WireOutcome.Failure(ModErrorCode.RateLimited, "The multiplayer command rate limit was exceeded.");
            }

            return RunCanonical(sender, () => binding.Invoke(sender, sequence, payload, scope));
        }

        private WireOutcome ExecuteObjectInput(ParticipantId sender, string objectId, ulong sequence, byte[] payload)
        {
            if (!objects.TryGetValue(objectId, out var record) || record.Life != ObjectLife.Live)
            {
                return WireOutcome.Failure(ModErrorCode.NotFound, "The canonical server no longer contains this replicated object.");
            }

            if (!record.Type.IsActive)
            {
                return WireOutcome.Failure(
                    ModErrorCode.NotFound,
                    "The canonical server has no active registration for replicated-object type '" + record.Type.Key.Id + "'.");
            }

            if (payload.Length > record.Type.MaximumPayloadBytes)
            {
                return WireOutcome.Failure(ModErrorCode.InvalidArgument, "The replicated-object input exceeded its declared bound.");
            }

            if (!record.TryConsumeRate(sender, nowMicros))
            {
                return WireOutcome.Failure(ModErrorCode.RateLimited, "The replicated-object input rate limit was exceeded.");
            }

            var owns = record.CurrentOwner.HasValue && record.CurrentOwner.Value.Equals(sender);
            if (record.CurrentOwner.HasValue && !owns)
            {
                return WireOutcome.Failure(ModErrorCode.NotAuthoritative, "The authenticated sender does not own this replicated object.");
            }

            return RunCanonical(sender, () => record.InvokeInput(sender, sequence, owns, payload, scope));
        }

        private void DropConnection(HostConnection connection)
        {
            if (connection.Phase == HostConnectionPhase.Closed) return;
            if (scope.IsOpen) throw new InvalidOperationException("A connection cannot close inside a transaction.");
            var wasJoined = connection.IsJoined;
            connection.Phase = HostConnectionPhase.Closed;
            connections.Remove(connection);
            if (connection.Link.State != EngineLinkState.Closed) connection.Link.Close();
            var record = connection.Participant;
            if (!wasJoined || record == null || !ReferenceEquals(record.Connection, connection)) return;
            record.Connection = null;
            record.IsConnected = false;
            record.DepartedAtMicros = nowMicros;
            ReleaseOwnership(record.Id);
            MarkParticipantsChanged();
        }

        private void EndConnection(HostConnection connection, MultiplayerSessionEndReason reason, string message)
        {
            if (connection.Phase == HostConnectionPhase.Closed) return;
            if (scope.IsOpen) throw new InvalidOperationException("A connection cannot close inside a transaction.");
            connection.Enqueue(new SessionEndedItem(reason, message));
            FlushConnection(connection, downstreamDue: true);
            DropConnection(connection);
        }

        /// <summary>
        /// Returns a departed participant's objects to server ownership as canonical changes. It commits without a
        /// transaction of its own and leaves the notifications to the settle point of the operation that called it,
        /// because it runs in the middle of joins, frames, kicks and shutdown.
        /// </summary>
        private void ReleaseOwnership(ParticipantId participant)
        {
            foreach (var record in objectList)
            {
                if (record.Life != ObjectLife.Live || !record.CurrentOwner.HasValue || !record.CurrentOwner.Value.Equals(participant)) continue;
                record.CurrentOwner = null;
                record.CurrentVersion++;
                record.Confirm();
                QueueObjectChange(record, ReplicatedObjectChangeKind.Changed);
                OnObjectChanged(record);
            }
        }

        private void ExpireDepartedParticipants()
        {
            var removed = false;
            for (var index = participantRecords.Count - 1; index >= 0; index--)
            {
                var record = participantRecords[index];
                if (record.IsConnected || record.Identity == null) continue;
                if (nowMicros - record.DepartedAtMicros < options.ParticipantGraceMicros) continue;
                participantRecords.RemoveAt(index);
                removed = true;
            }

            if (removed) MarkParticipantsChanged();
        }

        private void ShutdownHost(MultiplayerSessionEndReason reason)
        {
            foreach (var connection in connections.ToArray())
            {
                EndConnection(connection, reason, "The host ended the session.");
            }
        }
    }
}
