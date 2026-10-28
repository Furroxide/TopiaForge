using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>The host's outbound side: snapshots, committed changes, events, clock probes and per-connection flushes.</summary>
    internal sealed partial class SessionEngine
    {
        private readonly FlushContext flush = new FlushContext();
        private long nextDownstreamAt;

        /// <summary>Gets how many update-rate entries the host's connections hold, so a test can prove none outlive their value.</summary>
        internal int RateHistoryEntries
        {
            get
            {
                var count = 0;
                foreach (var connection in connections) count += connection.StateSentAt.Count + connection.ObjectSentAt.Count;
                return count;
            }
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
            if (Role != EngineRole.Host || record.PrivatePlayer.HasValue) return;
            foreach (var connection in connections)
            {
                if (connection.IsJoined) connection.PendingStates.Add(record);
            }
        }

        private void OnHostStateUnregistered(StateRecord record)
        {
            foreach (var connection in connections) connection.ForgetState(record);
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
                connection.ForgetObject(record);
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

            // The host's own player sees the event at the settle point after it commits, before any confirmation of
            // the same operation, exactly as a remote client sees it with the frame that carries it.
            if (ProcessKind == MultiplayerProcessKind.Interactive && localParticipantId.HasValue &&
                item.Target.Selects(localParticipantId.Value, item.Sender))
            {
                presentationDeliveries.Enqueue(new PresentationDelivery(item.Key, item.Bytes));
            }
        }

        private void ScheduleClockSync()
        {
            if (options.ClockSyncIntervalMicros <= 0) return;
            foreach (var connection in connections)
            {
                if (connection.Phase != HostConnectionPhase.Ready || nowMicros < connection.NextClockSyncAt) continue;
                connection.NextClockSyncAt = nowMicros + options.ClockSyncIntervalMicros;
                connection.ProbePending = true;
                connection.ProbeMicros = nowMicros;
                connection.Enqueue(new ClockSyncItem(nowMicros, tick, connection.Participant?.RttMicros ?? -1));
            }
        }

        /// <summary>
        /// Takes a round-trip sample from the reply to the latest clock probe. Any other reply is ignored, so a client
        /// can neither steer its reported ping nor make the host rebroadcast the participant list at will.
        /// </summary>
        private void ObserveRoundTrip(HostConnection connection, ulong echoedMicros)
        {
            if (!connection.ProbePending || echoedMicros != (ulong)Math.Max(0, connection.ProbeMicros)) return;
            connection.ProbePending = false;
            var record = connection.Participant;
            if (record == null) return;
            var sample = Math.Max(0, nowMicros - connection.ProbeMicros);
            var previousPing = record.PingMilliseconds;
            var previousQuality = record.Quality;
            record.RttMicros = record.RttMicros < 0 ? sample : record.RttMicros + ((sample - record.RttMicros) / 8);
            if (previousPing == record.PingMilliseconds && previousQuality == record.Quality) return;
            MarkParticipantsChanged();
        }

        private void FlushHost()
        {
            if (connections.Count == 0)
            {
                participantsDirty = false;
                voiceRosterDirty = false;
                return;
            }

            if (participantsDirty)
            {
                participantsDirty = false;
                List<ParticipantEntry>? entries = null;
                foreach (var connection in connections)
                {
                    if (!connection.IsJoined) continue;
                    entries ??= HostParticipantEntries();
                    connection.Enqueue(new ParticipantsItem(entries));
                }
            }

            FlushVoiceRoster();
            var downstreamDue = options.DownstreamRateHz == 0 || nowMicros >= nextDownstreamAt;
            if (downstreamDue && options.DownstreamRateHz > 0) nextDownstreamAt = nowMicros + (1_000_000 / options.DownstreamRateHz);
            foreach (var connection in connections.ToArray()) FlushConnection(connection, downstreamDue);
        }

        private void FlushConnection(HostConnection connection, bool downstreamDue)
        {
            if (connection.Phase == HostConnectionPhase.Closed) return;
            if (downstreamDue) FlushPrivateState(connection);
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
                // Credits tell the player what this host's owners took, which opens its windows again; they need no commit.
                if (connection.Streams.CreditsDue)
                {
                    connection.Streams.WriteCredits(context.Frame, context.Body, epoch, connection.JoinGeneration);
                    SplitHostFrame(connection);
                }

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
                    connection.ForgetObject(record);
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
            if (flush.Frame.Length >= ProtocolLimits.SoftFrameBytes) SendHostFrame(connection);
        }

        private void SendHostFrame(HostConnection connection)
        {
            if (flush.Frame.Length == 0) return;
            if (connection.Link.State != EngineLinkState.Closed) connection.Link.Send(EngineChannel.Session, flush.Frame.AsSegment());
            flush.Frame.Reset();
        }
    }
}
