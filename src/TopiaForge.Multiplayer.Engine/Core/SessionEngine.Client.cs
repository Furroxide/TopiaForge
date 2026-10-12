using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        private const int SoftFrameBytes = 64 * 1024;
        private readonly List<PendingSubmission> pending = new List<PendingSubmission>();
        private readonly Dictionary<WireKey, StagedState> unboundStates = new Dictionary<WireKey, StagedState>();
        private readonly Dictionary<string, ObjectWireMessage> unboundObjects = new Dictionary<string, ObjectWireMessage>(StringComparer.Ordinal);
        private readonly TombstoneSet tombstones = new TombstoneSet();
        private readonly ProtocolWriter clientFrame = new ProtocolWriter(1024);
        private readonly ProtocolWriter clientBody = new ProtocolWriter(256);
        private List<ParticipantEntry> clientParticipants = new List<ParticipantEntry>();
        private ClientChannel? channel;
        private FixedStepClock? hostClock;
        private ulong nextSequence;
        private ulong acknowledgedSequence;
        private ulong lastHostTick;
        private long lastHostTickAt;
        private bool joinedBefore;
        private bool reconcileRequested;

        private bool ClientHasPendingWork => channel != null && channel.HasOutbound;

        /// <summary>Joins a host over <paramref name="link"/>, replacing any previous connection.</summary>
        internal void Connect(IEngineLink link, string displayName, byte[]? content = null)
        {
            if (link == null) throw new ArgumentNullException(nameof(link));
            if (!ProtocolLimits.IsValidIdentity(displayName)) throw new ArgumentException("A bounded display name is required.", nameof(displayName));
            RequireOwnerThread();
            if (Role != EngineRole.Client) throw new InvalidOperationException("Only a client engine joins a host.");
            if (ended) throw new ObjectDisposedException(nameof(SessionEngine));
            if (scope.IsOpen) throw new InvalidOperationException("A client cannot reconnect from inside a transaction.");
            if (channel != null) LoseLink("The client began reconnect synchronization.");
            channel = new ClientChannel(link, displayName, content ?? Array.Empty<byte>());
            state = MultiplayerSessionState.Connecting;
            MarkSessionChanged();
            EndOperation();
        }

        internal void RequestCancellation(PendingSubmission submission)
        {
            if (IsOwnerThread && !scope.IsOpen && !settling)
            {
                CancelPending(submission, "The multiplayer submission was cancelled.");
                EndOperation();
                return;
            }

            inbox.Enqueue(() => CancelPending(submission, "The multiplayer submission was cancelled."));
        }

        private Task<MultiplayerCommandConfirmation<T>> SubmitPending<T>(
            PendingSubmission submission,
            Task<MultiplayerCommandConfirmation<T>> task,
            CancellationToken cancellationToken)
            where T : class
        {
            pending.Add(submission);
            channel!.Unsent.Add(submission);
            if (submission.Predicted) RunPrediction(submission);
            submission.RegisterCancellation(cancellationToken);
            EndOperation();
            return task;
        }

        private void PumpClient()
        {
            var current = channel;
            if (current == null) return;
            UpdateLinkHealth(current);
            while (ReferenceEquals(channel, current) && current.Link.TryReceive(out var channelKind, out var frame))
            {
                if (channelKind == EngineChannel.Session) ProcessClientFrame(current, frame);
            }

            if (ReferenceEquals(channel, current) && current.Link.State == EngineLinkState.Closed)
            {
                LoseLink("The connection to the host closed.");
            }

            UpdateClientTick();
            FlushClient();
        }

        private void UpdateLinkHealth(ClientChannel current)
        {
            if (current.Link.State == EngineLinkState.Stalled && state == MultiplayerSessionState.Ready)
            {
                state = MultiplayerSessionState.Reconnecting;
                MarkSessionChanged();
            }
            else if (current.Link.State == EngineLinkState.Open && state == MultiplayerSessionState.Reconnecting)
            {
                state = MultiplayerSessionState.Ready;
                MarkSessionChanged();
            }
        }

        private void ProcessClientFrame(ClientChannel current, byte[] frame)
        {
            try
            {
                if (frame.Length > ProtocolLimits.MaximumFrameBytes) throw new ProtocolException("A frame exceeded the protocol bound.");
                var reader = new ProtocolReader(frame);
                while (!reader.AtEnd && ReferenceEquals(channel, current))
                {
                    var type = reader.ReadVarUInt();
                    var body = reader.Slice(reader.ReadLength(ProtocolLimits.MaximumFrameBytes));
                    HandleClientMessage(current, type, body);
                }
            }
            catch (ProtocolException exception)
            {
                Report(new EngineDiagnostic(EngineDiagnosticKind.ProtocolViolation, "The host violated the session protocol: " + exception.Message));
                EndClient(MultiplayerSessionEndReason.ProtocolMismatch, "The host violated the session protocol.");
            }
        }

        private void HandleClientMessage(ClientChannel current, ulong type, ProtocolReader body)
        {
            if (current.Phase == ClientPhase.AwaitingJoin &&
                type != MessageType.JoinAccepted && type != MessageType.JoinRejected && type != MessageType.SessionEnded &&
                !MessageType.IsIgnorable(type))
            {
                throw new ProtocolException("The host sent session traffic before accepting the join.");
            }

            switch (type)
            {
                case MessageType.JoinAccepted:
                    if (current.Phase != ClientPhase.AwaitingJoin) throw new ProtocolException("The host accepted a join twice.");
                    ApplyJoinAccepted(current, JoinMessages.ReadJoinAccepted(body));
                    break;
                case MessageType.JoinRejected:
                    var rejected = JoinMessages.ReadJoinRejected(body);
                    EndClient(rejected.Reason, rejected.Message);
                    break;
                case MessageType.SessionEnded:
                    var reason = JoinMessages.ReadEndReason(body);
                    EndClient(reason, body.ReadString(ProtocolLimits.MessageCharacters));
                    break;
                case MessageType.SessionReplaced:
                    BeginReplacement(current, SessionMessages.ReadSessionReplaced(body));
                    break;
                case MessageType.SnapshotBegin:
                    if (body.ReadVarUInt() != epoch || current.Staging != null) throw new ProtocolException("A snapshot began out of order.");
                    current.Staging = new SnapshotStaging();
                    break;
                case MessageType.SnapshotEnd:
                    var staging = current.Staging ?? throw new ProtocolException("A snapshot ended before it began.");
                    current.Staging = null;
                    ApplySnapshot(staging);
                    current.SnapshotCompleted = true;
                    break;
                case MessageType.ParticipantsChanged:
                    clientParticipants = SessionMessages.ReadParticipants(body);
                    MarkSessionChanged(participantsChanged: true);
                    break;
                case MessageType.StateDelta:
                    ReplicationMessages.ReadStateDelta(body, current.InIntern, out var owner, out var stateId, out var version, out var bytes);
                    var key = new WireKey(owner, stateId);
                    if (current.Staging != null) current.Staging.States.Add(new StagedState(key, version, bytes));
                    else ApplyStateDelta(key, version, bytes);
                    break;
                case MessageType.ObjectSpawned:
                    var spawned = ReplicationMessages.ReadObjectSpawned(body, current.InIntern);
                    if (current.Staging != null) current.Staging.Objects.Add(spawned);
                    else ApplyObjectSpawned(spawned);
                    break;
                case MessageType.ObjectChanged:
                    ApplyObjectChanged(ReplicationMessages.ReadObjectChanged(body));
                    break;
                case MessageType.ObjectDespawned:
                    ApplyObjectDespawned(ReplicationMessages.ReadObjectDespawned(body));
                    break;
                case MessageType.CommandResult:
                case MessageType.ObjectInputResult:
                    RecordResult(ReplicationMessages.ReadResult(body));
                    break;
                case MessageType.PresentationEvent:
                    ReplicationMessages.ReadPresentation(body, current.InIntern, out var eventOwner, out var eventId, out var payload);
                    if (current.Phase == ClientPhase.Ready) presentationDeliveries.Enqueue(new PresentationDelivery(new WireKey(eventOwner, eventId), payload));
                    break;
                case MessageType.Commit:
                    ApplyCommit(current, body.ReadVarUInt(), body.ReadVarUInt());
                    break;
                case MessageType.ClockSync:
                    var hostMicros = body.ReadVarUInt();
                    ObserveHostTick(body.ReadVarUInt());
                    current.ClockReplies.Add(hostMicros);
                    break;
                default:
                    if (!MessageType.IsIgnorable(type)) throw new ProtocolException("The host sent an unknown critical message.");
                    break;
            }
        }

        private void ApplyJoinAccepted(ClientChannel current, JoinAcceptedMessage message)
        {
            var newSession = new MultiplayerSessionId(message.SessionId);
            if (joinedBefore && (!newSession.Equals(sessionId) || message.Epoch != epoch))
            {
                ResetLocalSession(newSession, new SessionSeed(message.Seed), message.Epoch, "The multiplayer session was replaced.");
            }

            sessionId = newSession;
            seed = new SessionSeed(message.Seed);
            epoch = message.Epoch;
            localParticipantId = new ParticipantId(message.ParticipantId);
            hostParticipantId = message.HostParticipantId == null ? (ParticipantId?)null : new ParticipantId(message.HostParticipantId);
            hostClock = new FixedStepClock(message.StepRateHz);
            if (message.NextSequence > 0 && message.NextSequence - 1 > nextSequence) nextSequence = message.NextSequence - 1;
            ObserveHostTick(message.Tick);
            joinedBefore = true;
            current.Phase = ClientPhase.Synchronizing;
            state = MultiplayerSessionState.Synchronizing;
            MarkSessionChanged(participantsChanged: true);
        }

        private void ApplyCommit(ClientChannel current, ulong hostTick, ulong acknowledged)
        {
            ObserveHostTick(hostTick);
            if (acknowledged > acknowledgedSequence) acknowledgedSequence = acknowledged;
            if (current.SnapshotCompleted)
            {
                current.SnapshotCompleted = false;
                current.Phase = ClientPhase.Ready;
                current.ReadyAckPending = true;
                state = MultiplayerSessionState.Ready;
                MarkSessionChanged();
            }

            for (var index = 0; index < pending.Count; index++)
            {
                var submission = pending[index];
                var result = submission.Result;
                if (result == null || !submission.TryFinish()) continue;
                pending.RemoveAt(index--);
                QueueCompletion(submission, new NetworkTick(result.ConfirmedAt), result.Outcome);
            }

            reconcileRequested = true;
            Settle();
        }

        private void RecordResult(SubmissionResultMessage result)
        {
            foreach (var submission in pending)
            {
                if (submission.Sequence != result.Sequence) continue;
                if (!submission.Finished) submission.Result = result;
                return;
            }
        }

        /// <summary>Resets current values to confirmed ones and replays predictions the host has not processed yet.</summary>
        private void Reconcile()
        {
            foreach (var record in stateList)
            {
                if (record.CurrentMatchesConfirmed) continue;
                record.CurrentBytes = record.ConfirmedBytes;
                record.CurrentVersion = record.ConfirmedVersion;
                MarkStateChanged(record);
            }

            foreach (var record in objectList) record.RevertToConfirmed();
            foreach (var submission in pending.ToArray())
            {
                if (!submission.Finished && submission.Predicted && submission.Result == null &&
                    submission.Sequence > acknowledgedSequence)
                {
                    RunPrediction(submission);
                }
            }
        }

        private void ApplyStateDelta(WireKey key, ulong version, byte[] bytes)
        {
            if (!states.TryGetValue(key, out var record) || !record.IsActive)
            {
                if (!unboundStates.TryGetValue(key, out var known) || version >= known.Version)
                {
                    unboundStates[key] = new StagedState(key, version, bytes);
                }

                return;
            }

            if (version < record.ConfirmedVersion ||
                (version == record.ConfirmedVersion && EngineCodec.BytesEqual(bytes, record.ConfirmedBytes)))
            {
                return;
            }

            if (!record.CanDecode(bytes))
            {
                FailContract("replicated state '" + key + "'");
                return;
            }

            record.ConfirmedBytes = bytes;
            record.ConfirmedVersion = version;
            reconcileRequested = true;
        }

        private void ApplySnapshot(SnapshotStaging staging)
        {
            var incoming = new HashSet<WireKey>();
            foreach (var staged in staging.States)
            {
                incoming.Add(staged.Key);
                if (states.TryGetValue(staged.Key, out var record) && record.IsActive)
                {
                    if (!record.CanDecode(staged.Bytes))
                    {
                        FailContract("replicated state '" + staged.Key + "'");
                        return;
                    }

                    record.ConfirmedBytes = staged.Bytes;
                    record.ConfirmedVersion = staged.Version;
                }
                else
                {
                    unboundStates[staged.Key] = staged;
                }
            }

            foreach (var key in new List<WireKey>(unboundStates.Keys))
            {
                if (!incoming.Contains(key)) unboundStates.Remove(key);
            }

            // The snapshot is an exact replacement: a state the host no longer has returns to its declared default.
            foreach (var record in stateList)
            {
                if (!record.IsActive || incoming.Contains(record.Key)) continue;
                record.ConfirmedBytes = record.InitialBytes;
                record.ConfirmedVersion = 0;
            }

            var objectIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in staging.Objects) objectIds.Add(item.ObjectId);
            foreach (var record in objectList.ToArray())
            {
                if (objectIds.Contains(record.Id.Value)) continue;
                record.Life = ObjectLife.Despawned;
                RemoveObject(record);
                CancelPendingInputs(record, "The replicated object left the session.");
                QueueObjectChange(record, ReplicatedObjectChangeKind.Despawned);
            }

            unboundObjects.Clear();
            tombstones.Clear();
            foreach (var item in staging.Objects) ApplyObjectSpawned(item);
            reconcileRequested = true;
        }

        private void ApplyObjectSpawned(ObjectWireMessage message)
        {
            if (tombstones.Contains(message.ObjectId)) return;
            if (objects.ContainsKey(message.ObjectId))
            {
                ApplyObjectChanged(message);
                return;
            }

            var key = new WireKey(message.Owner, message.TypeId);
            if (!objectTypes.TryGetValue(key, out var binding) || !binding.IsActive)
            {
                unboundObjects[message.ObjectId] = message;
                return;
            }

            CreateRemoteObject(binding, message);
        }

        private void CreateRemoteObject(ObjectTypeBinding binding, ObjectWireMessage message)
        {
            if (!binding.CanDecodeState(message.Bytes))
            {
                FailContract("replicated object '" + message.ObjectId + "'");
                return;
            }

            var owner = message.OwnerParticipant == null ? (ParticipantId?)null : new ParticipantId(message.OwnerParticipant);
            var record = binding.CreateRecord(new NetworkObjectId(message.ObjectId), owner, message.Bytes, message.Version);
            AddObject(record);
            QueueObjectChange(record, ReplicatedObjectChangeKind.Spawned);
            reconcileRequested = true;
        }

        private void ApplyObjectChanged(ObjectWireMessage message)
        {
            if (!objects.TryGetValue(message.ObjectId, out var record))
            {
                if (unboundObjects.TryGetValue(message.ObjectId, out var unbound) && message.Version >= unbound.Version)
                {
                    unbound.OwnerParticipant = message.OwnerParticipant;
                    unbound.Version = message.Version;
                    unbound.Bytes = message.Bytes;
                }

                return;
            }

            var owner = message.OwnerParticipant == null ? (ParticipantId?)null : new ParticipantId(message.OwnerParticipant);
            var ownerChanged = !Nullable.Equals(owner, record.ConfirmedOwner);
            if (message.Version < record.ConfirmedVersion || (message.Version == record.ConfirmedVersion && !ownerChanged)) return;
            if (!record.Type.CanDecodeState(message.Bytes))
            {
                FailContract("replicated object '" + message.ObjectId + "'");
                return;
            }

            record.ConfirmedBytes = message.Bytes;
            record.ConfirmedVersion = message.Version;
            record.ConfirmedOwner = owner;
            record.CurrentOwner = owner;
            QueueObjectChange(record, ReplicatedObjectChangeKind.Changed);
            reconcileRequested = true;
        }

        private void ApplyObjectDespawned(ObjectWireMessage message)
        {
            tombstones.Add(message.ObjectId);
            unboundObjects.Remove(message.ObjectId);
            if (!objects.TryGetValue(message.ObjectId, out var record)) return;
            if (!record.Type.CanDecodeState(message.Bytes))
            {
                FailContract("replicated object '" + message.ObjectId + "'");
                return;
            }

            record.ConfirmedBytes = message.Bytes;
            record.ConfirmedVersion = message.Version;

            record.RevertToConfirmed();
            record.Life = ObjectLife.Despawned;
            RemoveObject(record);
            CancelPendingInputs(record, "The replicated object was despawned.");
            QueueObjectChange(record, ReplicatedObjectChangeKind.Despawned);
            reconcileRequested = true;
        }

        private void AdoptUnboundState(StateRecord record)
        {
            if (!unboundStates.TryGetValue(record.Key, out var staged)) return;
            unboundStates.Remove(record.Key);
            if (!record.CanDecode(staged.Bytes))
            {
                ReportCodecFailure("replicated state '" + record.Key + "'", "The canonical value does not match the local codec.");
                return;
            }

            record.ResetTo(staged.Bytes, staged.Version);
        }

        private void AdoptUnboundObjects(ObjectTypeBinding binding)
        {
            if (unboundObjects.Count == 0) return;
            foreach (var message in new List<ObjectWireMessage>(unboundObjects.Values))
            {
                if (!new WireKey(message.Owner, message.TypeId).Equals(binding.Key)) continue;
                unboundObjects.Remove(message.ObjectId);
                CreateRemoteObject(binding, message);
            }
        }

        private void CancelPendingInputs(ObjectRecord record, string message)
        {
            for (var index = 0; index < pending.Count; index++)
            {
                var submission = pending[index];
                if (!ReferenceEquals(submission.Target, record) || !submission.TryFinish()) continue;
                pending.RemoveAt(index--);
                channel?.Unsent.Remove(submission);
                QueueCompletion(submission, CurrentTick, WireOutcome.Failure(ModErrorCode.Cancelled, message));
            }
        }

        private void CancelPending(PendingSubmission submission, string message)
        {
            if (!submission.TryFinish()) return;
            pending.Remove(submission);
            channel?.Unsent.Remove(submission);
            QueueCompletion(submission, CurrentTick, WireOutcome.Failure(ModErrorCode.Cancelled, message));
            if (submission.Predicted) reconcileRequested = true;
        }

        private void CancelAllPending(string message)
        {
            foreach (var submission in pending.ToArray())
            {
                if (submission.TryFinish()) QueueCompletion(submission, CurrentTick, WireOutcome.Failure(ModErrorCode.Cancelled, message));
            }

            pending.Clear();
            channel?.Unsent.Clear();
            reconcileRequested = true;
        }

        private void QueueCompletion(PendingSubmission submission, NetworkTick confirmedAt, WireOutcome outcome) =>
            completions.Enqueue(() => submission.Complete(confirmedAt, outcome));

        private void BeginReplacement(ClientChannel current, SessionReplacedMessage message)
        {
            ResetLocalSession(new MultiplayerSessionId(message.SessionId), new SessionSeed(message.Seed), message.Epoch, "The multiplayer session was replaced.");
            current.Phase = ClientPhase.Synchronizing;
            current.Staging = null;
            current.SnapshotCompleted = false;
            current.ReadyAckPending = false;
            state = MultiplayerSessionState.Synchronizing;
            MarkSessionChanged();
        }

        /// <summary>Retires the current session locally: new token, default values, no objects, no pending work.</summary>
        private void ResetLocalSession(MultiplayerSessionId newSession, SessionSeed newSeed, ulong newEpoch, string message)
        {
            var previous = sessionCancellation;
            sessionCancellation = new CancellationTokenSource();
            sessionId = newSession;
            seed = newSeed;
            epoch = newEpoch;
            // Tokens already handed out stay readable, so the retired source is cancelled but never disposed.
            RetireSessionToken(previous);
            CancelAllPending(message);
            foreach (var record in stateList)
            {
                record.ResetTo(record.InitialBytes, 0);
                MarkStateChanged(record);
            }

            DespawnAllLocally();
            unboundStates.Clear();
            unboundObjects.Clear();
            tombstones.Clear();
            presentationDeliveries.Clear();
            nextSequence = 0;
            acknowledgedSequence = 0;
            MarkSessionChanged();
        }

        private void LoseLink(string message)
        {
            var lost = channel;
            channel = null;
            if (lost != null && lost.Link.State != EngineLinkState.Closed) lost.Link.Close();
            CancelAllPending(message);
            localParticipantId = null;
            if (!ended) state = MultiplayerSessionState.Connecting;
            MarkSessionChanged(participantsChanged: true);
        }

        private void EndClient(MultiplayerSessionEndReason reason, string message)
        {
            var lost = channel;
            channel = null;
            lost?.Link.Close();
            CancelAllPending(string.IsNullOrEmpty(message) ? "The multiplayer session ended." : message);
            localParticipantId = null;
            EnterEnded(reason);
        }

        private void FailContract(string what)
        {
            ReportCodecFailure(what, "The host's canonical value does not decode with the local contract.");
            EndClient(MultiplayerSessionEndReason.ProtocolMismatch, "A replicated value did not match the local contract.");
        }

        private void ShutdownClient()
        {
            var current = channel;
            if (current != null && current.Link.State != EngineLinkState.Closed)
            {
                clientFrame.Reset();
                SessionMessages.WriteEmpty(clientFrame, clientBody, MessageType.Leave);
                current.Link.Send(EngineChannel.Session, clientFrame.AsSegment());
                current.Link.Close();
            }

            channel = null;
            CancelAllPending("The multiplayer session ended.");
        }

        private void ObserveHostTick(ulong hostTick)
        {
            lastHostTick = hostTick;
            lastHostTickAt = nowMicros;
            UpdateClientTick();
        }

        private void UpdateClientTick()
        {
            if (hostClock == null) return;
            var estimate = lastHostTick + hostClock.StepsIn(nowMicros - lastHostTickAt);
            if (estimate <= tick) return;
            tick = estimate;
            MarkSessionChanged();
        }

        private void FlushClient()
        {
            var current = channel;
            if (current == null || current.Link.State == EngineLinkState.Closed || !current.HasOutbound) return;
            clientFrame.Reset();
            if (current.JoinPending)
            {
                current.JoinPending = false;
                JoinMessages.WriteJoinRequest(
                    clientFrame,
                    clientBody,
                    new JoinRequestMessage(ProtocolVersion, current.DisplayName, RegisteredContracts(), current.Content));
            }

            if (current.ReadyAckPending)
            {
                current.ReadyAckPending = false;
                SessionMessages.WriteEpochMessage(clientFrame, clientBody, MessageType.ReadyAck, epoch);
            }

            foreach (var submission in current.Unsent)
            {
                submission.Write(clientFrame, clientBody, current.OutIntern);
                submission.Sent = true;
                if (clientFrame.Length >= SoftFrameBytes) SendClientFrame(current);
            }

            current.Unsent.Clear();
            foreach (var echoed in current.ClockReplies) SessionMessages.WriteClockSyncReply(clientFrame, clientBody, echoed);
            current.ClockReplies.Clear();
            SendClientFrame(current);
        }

        private void SendClientFrame(ClientChannel current)
        {
            if (clientFrame.Length == 0) return;
            current.Link.Send(EngineChannel.Session, clientFrame.AsSegment());
            clientFrame.Reset();
        }
    }
}
