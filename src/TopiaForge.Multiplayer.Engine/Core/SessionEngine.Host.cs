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
        private readonly Queue<PresentationDelivery> localPresentation = new Queue<PresentationDelivery>();
        private readonly FlushContext flush = new FlushContext();
        private ulong nextParticipantNumber;
        private long nextDownstreamAt;

        private bool HostHasPendingWork
        {
            get
            {
                if (localPresentation.Count > 0) return true;
                foreach (var connection in connections)
                {
                    if (connection.HasOutbound || connection.HasPendingDeltas) return true;
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
            previous.Cancel();
            foreach (var record in stateList)
            {
                record.ResetTo(record.InitialBytes, 0);
                MarkStateChanged(record);
            }

            DespawnAllLocally();
            nextObjectNumber = 0;
            foreach (var binding in commands.Values) binding.ResetForNewSession();
            foreach (var record in participantRecords) record.LastSequence = 0;
            localPresentation.Clear();
            presentationDeliveries.Clear();
            var replaced = new SessionReplacedMessage(sessionId.Value, seed.Value, epoch);
            foreach (var connection in connections)
            {
                if (!connection.IsJoined) continue;
                connection.ClearPendingDeltas();
                connection.Enqueue(new SessionReplacedItem(replaced));
                connection.Enqueue(CaptureSnapshot());
                connection.Phase = HostConnectionPhase.Accepted;
                connection.AcknowledgedSequence = 0;
            }

            BroadcastParticipants();
            MarkSessionChanged(participantsChanged: true);
            EndOperation();
        }

        /// <summary>Ends one participant's connection with <paramref name="reason"/>.</summary>
        internal bool Kick(ParticipantId participant, MultiplayerSessionEndReason reason, string message)
        {
            RequireOwnerThread();
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

        private void PumpHost()
        {
            RunDueSteps();
            if (connections.Count > 0)
            {
                foreach (var connection in connections.ToArray()) ProcessConnection(connection);
            }

            while (localPresentation.Count > 0) presentationDeliveries.Enqueue(localPresentation.Dequeue());
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
                if (connection.IsJoined)
                {
                    BroadcastParticipants();
                    MarkSessionChanged(participantsChanged: true);
                }
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
            if (decision.Accepted && record == null)
            {
                var id = AllocateParticipantId(identity);
                if (!id.HasValue)
                {
                    decision = JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "The host could not assign a participant id.");
                }
                else
                {
                    record = new ParticipantRecord(id.Value, identity, request.DisplayName, isHost: false);
                    participantRecords.Add(record);
                }
            }

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
            BroadcastParticipants();
            MarkSessionChanged(participantsChanged: true);
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
            if (message.Epoch != epoch)
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

        private SnapshotItem CaptureSnapshot()
        {
            var capturedStates = new List<StagedState>(stateList.Count);
            foreach (var record in stateList)
            {
                if (record.IsActive) capturedStates.Add(new StagedState(record.Key, record.ConfirmedVersion, record.ConfirmedBytes));
            }

            var capturedObjects = new List<ObjectWireMessage>(objectList.Count);
            foreach (var record in objectList)
            {
                if (record.Life == ObjectLife.Live) capturedObjects.Add(ToWire(record));
            }

            return new SnapshotItem(epoch, capturedStates, capturedObjects);
        }

        private static ObjectWireMessage ToWire(ObjectRecord record) =>
            new ObjectWireMessage
            {
                Owner = record.Type.Key.Owner,
                TypeId = record.Type.Key.Id,
                ObjectId = record.Id.Value,
                OwnerParticipant = record.ConfirmedOwner?.Value,
                Version = record.ConfirmedVersion,
                Bytes = record.ConfirmedBytes
            };

        private void OnHostStateRegistered(StateRecord record)
        {
            foreach (var connection in connections)
            {
                if (connection.IsJoined) connection.PendingStates.Add(record);
            }
        }

        private void OnStateCommitted(StateRecord record)
        {
            if (Role != EngineRole.Host) return;
            foreach (var connection in connections)
            {
                if (connection.IsJoined) connection.PendingStates.Add(record);
            }
        }

        private void OnObjectChanged(ObjectRecord record)
        {
            if (Role != EngineRole.Host) return;
            foreach (var connection in connections)
            {
                if (connection.IsJoined) connection.PendingObjects.Add(record);
            }
        }

        private void OnObjectSpawned(ObjectRecord record)
        {
            if (Role != EngineRole.Host) return;
            ObjectWireMessage? message = null;
            foreach (var connection in connections)
            {
                if (!connection.IsJoined) continue;
                message ??= ToWire(record);
                connection.Enqueue(new SpawnItem(record, message));
            }
        }

        private void OnObjectDespawned(ObjectRecord record)
        {
            if (Role != EngineRole.Host) return;
            foreach (var connection in connections)
            {
                if (!connection.IsJoined) continue;
                connection.PendingObjects.Remove(record);
                connection.Enqueue(new DespawnItem(record));
            }
        }

        /// <summary>Routes a committed event to every ready recipient its target selects, and to the host's own player.</summary>
        private void PublishEvent(BufferedEvent item)
        {
            foreach (var connection in connections)
            {
                if (connection.Phase != HostConnectionPhase.Ready || connection.Participant == null) continue;
                if (item.Target.Selects(connection.Participant.Id, item.Sender)) connection.Enqueue(new PresentationItem(item.Key, item.Bytes));
            }

            if (ProcessKind == MultiplayerProcessKind.Interactive && localParticipantId.HasValue &&
                item.Target.Selects(localParticipantId.Value, item.Sender))
            {
                localPresentation.Enqueue(new PresentationDelivery(item.Key, item.Bytes));
            }
        }

        private void BroadcastParticipants()
        {
            List<ParticipantEntry>? entries = null;
            foreach (var connection in connections)
            {
                if (!connection.IsJoined) continue;
                entries ??= HostParticipantEntries();
                connection.Enqueue(new ParticipantsItem(entries));
            }
        }

        private void DropConnection(HostConnection connection)
        {
            if (connection.Phase == HostConnectionPhase.Closed) return;
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
            BroadcastParticipants();
            MarkSessionChanged(participantsChanged: true);
        }

        private void EndConnection(HostConnection connection, MultiplayerSessionEndReason reason, string message)
        {
            if (connection.Phase == HostConnectionPhase.Closed) return;
            connection.Enqueue(new SessionEndedItem(reason, message));
            FlushConnection(connection, downstreamDue: true);
            DropConnection(connection);
        }

        private void ReleaseOwnership(ParticipantId participant)
        {
            var owned = new List<ObjectRecord>();
            foreach (var record in objectList)
            {
                if (record.Life == ObjectLife.Live && record.CurrentOwner.HasValue && record.CurrentOwner.Value.Equals(participant)) owned.Add(record);
            }

            if (owned.Count == 0) return;
            RunTopLevel(() =>
            {
                foreach (var record in owned)
                {
                    scope.TouchObject(record);
                    record.CurrentOwner = null;
                    record.CurrentVersion++;
                }

                return OperationResult<bool>.Success(true);
            });
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

            if (!removed) return;
            BroadcastParticipants();
            MarkSessionChanged(participantsChanged: true);
        }

        private void ScheduleClockSync()
        {
            if (options.ClockSyncIntervalMicros <= 0) return;
            foreach (var connection in connections)
            {
                if (connection.Phase != HostConnectionPhase.Ready || nowMicros < connection.NextClockSyncAt) continue;
                connection.NextClockSyncAt = nowMicros + options.ClockSyncIntervalMicros;
                connection.Enqueue(new ClockSyncItem(nowMicros, tick, connection.Participant?.RttMicros ?? -1));
            }
        }

        private void ObserveRoundTrip(HostConnection connection, ulong echoedMicros)
        {
            var record = connection.Participant;
            if (record == null || echoedMicros > (ulong)nowMicros) return;
            var sample = nowMicros - (long)echoedMicros;
            var previousPing = record.PingMilliseconds;
            var previousQuality = record.Quality;
            record.RttMicros = record.RttMicros < 0 ? sample : record.RttMicros + ((sample - record.RttMicros) / 8);
            if (previousPing == record.PingMilliseconds && previousQuality == record.Quality) return;
            BroadcastParticipants();
            MarkSessionChanged(participantsChanged: true);
        }

        private void FlushHost()
        {
            if (connections.Count == 0) return;
            var downstreamDue = options.DownstreamRateHz == 0 || nowMicros >= nextDownstreamAt;
            if (downstreamDue && options.DownstreamRateHz > 0) nextDownstreamAt = nowMicros + (1_000_000 / options.DownstreamRateHz);
            foreach (var connection in connections.ToArray()) FlushConnection(connection, downstreamDue);
        }

        private void FlushConnection(HostConnection connection, bool downstreamDue)
        {
            if (connection.Phase == HostConnectionPhase.Closed) return;
            if (!connection.HasOutbound && !connection.HasPendingDeltas) return;
            var context = flush;
            context.Frame.Reset();
            context.SkippedSpawns.Clear();
            context.WroteMessage = false;
            context.SplitIfLarge = () => SplitHostFrame(connection);
            foreach (var item in connection.Queue)
            {
                item.Write(connection, context);
                context.WroteMessage = true;
                SplitHostFrame(connection);
            }

            connection.Queue.Clear();
            if (connection.IsJoined)
            {
                WritePendingStates(connection, downstreamDue);
                WritePendingObjects(connection, downstreamDue);
                if (context.WroteMessage || connection.CommitNeeded)
                {
                    SessionMessages.WriteCommit(context.Frame, context.Body, tick, connection.AcknowledgedSequence);
                }
            }

            SendHostFrame(connection);
            context.SplitIfLarge = null;
            connection.ForceFlush = false;
            connection.CommitNeeded = false;
            if (connection.CloseAfterFlush)
            {
                connection.CloseAfterFlush = false;
                DropConnection(connection);
            }
        }

        private void WritePendingStates(HostConnection connection, bool downstreamDue)
        {
            if (connection.PendingStates.Count == 0) return;
            var context = flush;
            connection.PendingStates.CopyTo(context.States);
            foreach (var record in context.States)
            {
                if (!record.IsActive)
                {
                    connection.PendingStates.Remove(record);
                    continue;
                }

                if (!IsDue(connection.ForceFlush, downstreamDue, record.Delivery, record.MaximumUpdatesPerSecond, connection.StateSentAt, record)) continue;
                ReplicationMessages.WriteStateDelta(
                    context.Frame,
                    context.Body,
                    connection.OutIntern,
                    record.Key.Owner,
                    record.Key.Id,
                    record.ConfirmedVersion,
                    record.ConfirmedBytes);
                context.WroteMessage = true;
                if (record.MaximumUpdatesPerSecond > 0) connection.StateSentAt[record] = nowMicros;
                connection.PendingStates.Remove(record);
                SplitHostFrame(connection);
            }
        }

        private void WritePendingObjects(HostConnection connection, bool downstreamDue)
        {
            if (connection.PendingObjects.Count == 0) return;
            var context = flush;
            connection.PendingObjects.CopyTo(context.Objects);
            foreach (var record in context.Objects)
            {
                if (record.Life != ObjectLife.Live)
                {
                    connection.PendingObjects.Remove(record);
                    continue;
                }

                if (!IsDue(connection.ForceFlush, downstreamDue, record.Type.Delivery, record.Type.MaximumUpdatesPerSecond, connection.ObjectSentAt, record)) continue;
                ReplicationMessages.WriteObjectChanged(context.Frame, context.Body, ToWire(record));
                context.WroteMessage = true;
                if (record.Type.MaximumUpdatesPerSecond > 0) connection.ObjectSentAt[record] = nowMicros;
                connection.PendingObjects.Remove(record);
                SplitHostFrame(connection);
            }
        }

        private bool IsDue<TRecord>(
            bool forced,
            bool downstreamDue,
            MultiplayerDelivery delivery,
            int maximumUpdatesPerSecond,
            Dictionary<TRecord, long> sentAt,
            TRecord record)
            where TRecord : class
        {
            if (forced) return true;
            if (delivery != MultiplayerDelivery.Immediate && !downstreamDue) return false;
            if (maximumUpdatesPerSecond <= 0 || !sentAt.TryGetValue(record, out var last)) return true;
            return nowMicros - last >= 1_000_000 / maximumUpdatesPerSecond;
        }

        private void SplitHostFrame(HostConnection connection)
        {
            if (flush.Frame.Length >= SoftFrameBytes) SendHostFrame(connection);
        }

        private void SendHostFrame(HostConnection connection)
        {
            if (flush.Frame.Length == 0) return;
            if (connection.Link.State != EngineLinkState.Closed) connection.Link.Send(EngineChannel.Session, flush.Frame.AsSegment());
            flush.Frame.Reset();
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
