using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    internal sealed partial class SessionEngine
    {
        private readonly List<PendingSubmission> pending = new List<PendingSubmission>();
        private readonly Dictionary<WireKey, StagedState> unboundStates = new Dictionary<WireKey, StagedState>();
        private readonly Dictionary<string, ObjectWireMessage> unboundObjects = new Dictionary<string, ObjectWireMessage>(StringComparer.Ordinal);
        private readonly TombstoneSet tombstones = new TombstoneSet();
        private readonly ProtocolWriter clientFrame = new ProtocolWriter(1024);
        private readonly ProtocolWriter clientBody = new ProtocolWriter(256);
        private ClientChannel? channel;
        private FixedStepClock? hostClock;
        private ulong nextSequence;
        private ulong acknowledgedSequence;
        private ulong lastHostTick;
        private long lastHostTickAt;
        private bool joinedBefore;
        private bool reconcileRequested;

        private bool ClientHasPendingWork => channel != null && (channel.HasOutbound || channel.SnapshotReadinessPending);

        private bool ClientFlushOpen => channel != null && channel.FlushOpen;

        /// <summary>
        /// Gets whether the joined session is over: it ended, or the process went back to solo play. A value that did not
        /// decode ends it in the middle of applying a batch, and nothing more of that batch may land in what follows.
        /// </summary>
        private bool ClientSessionOver => ended || Role != EngineRole.Client;

        /// <summary>Gets how many submissions await the host, including cancelled ones still replaying.</summary>
        internal int PendingSubmissionCount => pending.Count;

        /// <summary>Joins a host over <paramref name="link"/>, replacing any previous connection.</summary>
        /// <param name="link">The link to the host.</param>
        /// <param name="displayName">The name to join as.</param>
        /// <param name="content">The opaque content the host's admission reads.</param>
        /// <param name="shownWithoutVoice">
        /// Whether the player was shown, before this join, that the session offers no voice chat, as its signed descriptor
        /// or its listing said. The join then carries no voice even if the host says otherwise in <c>JoinAccepted</c>.
        /// </param>
        internal void Connect(IEngineLink link, string displayName, byte[]? content = null, bool shownWithoutVoice = false)
        {
            if (link == null) throw new ArgumentNullException(nameof(link));
            if (!ProtocolLimits.IsValidIdentity(displayName)) throw new ArgumentException("A bounded display name is required.", nameof(displayName));
            RequireOwnerThread();
            if (Role != EngineRole.Client) throw new InvalidOperationException("Only a client engine joins a host.");
            if (ended) throw new ObjectDisposedException(nameof(SessionEngine));
            if (scope.IsOpen) throw new InvalidOperationException("A client cannot reconnect from inside a transaction.");
            if (channel != null) LoseLink("The client began reconnect synchronization.");

            // A new join starts the readiness holds' total again; a rejoin before the player was Ready keeps counting it.
            if (!joinedBefore) readinessAnchored = false;
            NoteVoiceShown(shownWithoutVoice);
            channel = new ClientChannel(link, displayName, content ?? Array.Empty<byte>());
            state = MultiplayerSessionState.Connecting;
            MarkSessionChanged();
            EndOperation();
        }

        /// <summary>
        /// Cancels a submission for its caller. It takes effect at the next settle point, so a token cancelled in the
        /// middle of an engine operation never interrupts it.
        /// </summary>
        internal void RequestCancellation(PendingSubmission submission) =>
            Defer(() => CancelPending(submission, "The multiplayer submission was cancelled."));

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
            // Give the runtime one frame to observe a new stream and take its world/map hold.
            // A small snapshot may otherwise acknowledge Ready in the same pump as replacement.
            if (current.SnapshotReadinessPending)
            {
                current.SnapshotReadinessPending = false;
                if (readinessHolds > 0) EnterReadinessHeld(current);
                else EnterClientReady(current);
            }
            UpdateLinkHealth(current);
            while (ReferenceEquals(channel, current) && current.Link.TryReceive(out var channelKind, out var frame))
            {
                if (channelKind == EngineChannel.Session) ProcessClientFrame(current, frame);
                else if (channelKind == EngineChannel.Voice) HandleRelayedVoice(current, frame);
                else if (channelKind == EngineChannel.Realtime) HandleDatagramFromHost(current, frame);
            }

            if (ReferenceEquals(channel, current) && current.Link.State == EngineLinkState.Closed)
            {
                LoseLink("The connection to the host closed.");
            }

            UpdateClientTick();
            FlushVoiceHold();
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

            // Every host flush ends with a Commit; until it arrives, the flush's changes are only partly applied. A credit
            // changes no session value, so it may travel without one.
            if (type != MessageType.Commit && type != MessageType.SystemCredit) current.FlushOpen = true;
            switch (type)
            {
                case MessageType.JoinAccepted:
                    if (current.Phase != ClientPhase.AwaitingJoin) throw new ProtocolException("The host accepted a join twice.");
                    ApplyJoinAccepted(current, JoinMessages.ReadJoinAccepted(body));
                    break;
                case MessageType.JoinRejected:
                    var rejected = JoinMessages.ReadJoinRejected(body);
                    EndClient(rejected.Reason, rejected.Message, rejected.Report);
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
                    ListParticipants(current, SessionMessages.ReadParticipants(body));
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
                case MessageType.VoiceRoster:
                    ApplyVoiceRoster(body);
                    break;
                case MessageType.SystemMessage:
                    HandleSystemFromHost(current, body);
                    break;
                case MessageType.SystemCredit:
                    HandleSystemCreditFromHost(current, body);
                    break;
                case MessageType.ClockSync:
                    var hostMicros = body.ReadVarUInt();
                    ObserveHostTick(body.ReadVarUInt());
                    current.PendingClockReply = hostMicros;
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

            // A list shows only beside the ids of the host that sent it, and listener mutes follow the people, not the
            // session id: see AdoptParticipantList and AdoptListenerMutes.
            AdoptParticipantList(current.Link.PeerIdentity, message);
            AdoptListenerMutes(current.Link.PeerIdentity, message.RecordKept);

            // The host's first answer says whether its session offers voice, so this player knows before it is ready to send.
            HearVoiceOffer(message.VoiceOffered);

            // A rejoin keeps the session and its epoch but restarts every system stream: what the old link carried is gone,
            // so what the channels still hold from it goes too, and the new generation tells their owners to start again.
            if (joinedBefore) DropPendingSystemMessages();
            current.JoinGeneration = message.Generation;
            clientStreamGeneration = ++systemGeneration;
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
            current.VoiceMutesPending = options.Voice != null && (localVoiceMuteAll || localVoiceMutes.Count > 0);
            state = MultiplayerSessionState.Synchronizing;
            MarkSessionChanged(participantsChanged: true);
        }

        private void ApplyCommit(ClientChannel current, ulong hostTick, ulong acknowledged)
        {
            current.FlushOpen = false;
            ObserveHostTick(hostTick);
            if (acknowledged > acknowledgedSequence) acknowledgedSequence = acknowledged;
            if (current.SnapshotCompleted)
            {
                current.SnapshotCompleted = false;

                // Holds already taken apply immediately; otherwise allow the runtime to observe the stream first.
                if (readinessHolds > 0) EnterReadinessHeld(current);
                else current.SnapshotReadinessPending = true;
            }

            // A result's effects are part of the confirmed values once its flush commits. A submission its caller
            // already cancelled leaves the list here too, without completing a second time.
            for (var index = 0; index < pending.Count; index++)
            {
                var submission = pending[index];
                var result = submission.Result;
                if (result == null) continue;
                pending.RemoveAt(index--);
                if (submission.TryFinish()) QueueCompletion(submission, new NetworkTick(result.ConfirmedAt), result.Outcome);
            }

            reconcileRequested = true;
            Settle();
        }

        private void RecordResult(SubmissionResultMessage result)
        {
            foreach (var submission in pending)
            {
                if (submission.Sequence != result.Sequence) continue;
                submission.Result ??= result;
                return;
            }
        }

        /// <summary>
        /// Resets current values to confirmed ones and replays predictions the host has not processed yet. It runs only
        /// between host flushes, where the acknowledged sequence matches the confirmed values exactly.
        /// </summary>
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
                // A cancelled submission that was already sent keeps replaying: the host still executes it.
                if (submission.Predicted && submission.Result == null && submission.Sequence > acknowledgedSequence)
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
            foreach (var item in staging.Objects)
            {
                ApplyObjectSpawned(item);
                if (ClientSessionOver) return;
            }

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
            // A diagnostic names the type this game registered, never the id the host chose.
            if (!binding.CanDecodeState(message.Bytes))
            {
                FailContract("a replicated object of type '" + binding.Key + "'");
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
                FailContract("a replicated object of type '" + record.Type.Key + "'");
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
                FailContract("a replicated object of type '" + record.Type.Key + "'");
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
                // The host's value cannot be shown, so the session ends, as it does for an object adopted the same way.
                FailContract("replicated state '" + record.Key + "'");
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
                if (ClientSessionOver) return;
            }
        }

        /// <summary>
        /// Cancels inputs for a despawned object that never left this client. Sent ones wait for the host, whose result
        /// is the truth: an input that despawned its own target still succeeded.
        /// </summary>
        private void CancelPendingInputs(ObjectRecord record, string message)
        {
            for (var index = 0; index < pending.Count; index++)
            {
                var submission = pending[index];
                if (!ReferenceEquals(submission.Target, record) || submission.Sent || !submission.TryFinish()) continue;
                pending.RemoveAt(index--);
                channel?.Unsent.Remove(submission);
                QueueCompletion(submission, CurrentTick, WireOutcome.Failure(ModErrorCode.Cancelled, message));
            }
        }

        /// <summary>
        /// Completes a submission as cancelled. One that was never sent is withdrawn and its prediction rolls back; one
        /// already on the wire still executes, so its prediction keeps replaying until the host's commit covers it.
        /// </summary>
        private void CancelPending(PendingSubmission submission, string message)
        {
            if (!submission.TryFinish()) return;
            QueueCompletion(submission, CurrentTick, WireOutcome.Failure(ModErrorCode.Cancelled, message));
            if (submission.Sent) return;
            pending.Remove(submission);
            channel?.Unsent.Remove(submission);
            if (submission.Predicted) reconcileRequested = true;
        }

        /// <summary>
        /// Cancels the pending submissions of an owner whose facade closed, as its caller would. Sent ones stay listed
        /// until the host's commit covers them, but their registrations are gone, so their predictions change nothing.
        /// </summary>
        private void CancelOwnerSubmissions(string owner, string message)
        {
            foreach (var submission in pending.ToArray())
            {
                if (string.Equals(submission.Owner, owner, StringComparison.Ordinal)) CancelPending(submission, message);
            }

            reconcileRequested = true;
        }

        /// <summary>
        /// Abandons every pending submission when the connection or the session goes away. A submission whose result
        /// already arrived completes with that result, because the host did execute it.
        /// </summary>
        private void CancelAllPending(string message)
        {
            foreach (var submission in pending.ToArray())
            {
                if (!submission.TryFinish()) continue;
                var result = submission.Result;
                if (result != null) QueueCompletion(submission, new NetworkTick(result.ConfirmedAt), result.Outcome);
                else QueueCompletion(submission, CurrentTick, WireOutcome.Failure(ModErrorCode.Cancelled, message));
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
            current.RestartStreams();
            clientStreamGeneration = ++systemGeneration;
            current.Phase = ClientPhase.Synchronizing;
            current.Staging = null;
            current.SnapshotCompleted = false;
            current.SnapshotReadinessPending = false;
            current.ReadinessHeld = false;
            current.ReadyAckPending = false;
            current.ReplacementReceivedPending = true;
            state = MultiplayerSessionState.Synchronizing;
            sessionReplacementsReceived++;
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
            DropPendingSystemMessages();
            // Sequences keep counting across sessions, so a late result from the old session can never match a
            // submission made in the new one.
            MarkSessionChanged();
        }

        private void LoseLink(string message)
        {
            var lost = channel;
            channel = null;
            if (lost != null)
            {
                if (lost.Link.State != EngineLinkState.Closed) lost.Link.Close();
                CountUnsentSystem(lost);
                systemGeneration++;
                NoteUnansweredJoin(lost);
            }

            ResetClientVoice();
            CancelAllPending(message);
            localParticipantId = null;
            if (!ended) state = MultiplayerSessionState.Connecting;
            MarkSessionChanged(participantsChanged: true);
        }

        private void EndClient(MultiplayerSessionEndReason reason, string message, byte[]? report = null)
        {
            var lost = channel;
            channel = null;
            if (lost != null)
            {
                lost.Link.Close();
                CountUnsentSystem(lost);
                systemGeneration++;
            }

            ResetClientVoice();

            // The host's own words go to the runtime alone: mods see this game's text, never what a host wrote.
            CancelAllPending("The multiplayer session ended.");
            localParticipantId = null;
            FinishClientSession(new ClientSessionEnd(reason, message, report));
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
                // Before the first flush the host has never seen a join. Sending Leave then would be
                // pre-admission traffic; closing the unused link is the entire cancellation.
                if (!current.JoinPending)
                {
                    clientFrame.Reset();
                    SessionMessages.WriteEmpty(clientFrame, clientBody, MessageType.Leave);
                    current.Link.Send(EngineChannel.Session, clientFrame.AsSegment());
                }
                current.Link.Close();
            }

            if (current != null)
            {
                CountUnsentSystem(current);
                systemGeneration++;
            }

            channel = null;
            ResetClientVoice();
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

            WriteReplacementReceived(current);
            if (current.ReadyAckPending)
            {
                current.ReadyAckPending = false;
                SessionMessages.WriteEpochMessage(clientFrame, clientBody, MessageType.ReadyAck, epoch);
            }

            foreach (var submission in current.Unsent)
            {
                submission.Write(clientFrame, clientBody, current.OutIntern);
                submission.Sent = true;
                if (clientFrame.Length >= ProtocolLimits.SoftFrameBytes) SendClientFrame(current);
            }

            current.Unsent.Clear();
            WriteSystemOutbox(current);
            if (current.PendingClockReply.HasValue)
            {
                SessionMessages.WriteClockSyncReply(clientFrame, clientBody, current.PendingClockReply.Value);
                current.PendingClockReply = null;
            }

            if (current.VoiceMutesPending && current.Phase != ClientPhase.AwaitingJoin)
            {
                VoiceMessages.WriteMutes(clientFrame, clientBody, localVoiceMuteAll, localVoiceMutes);
                current.VoiceMutesPending = false;
            }

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
