using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// What a joined player reads from its host: baselines, announcements, despawns, snapshots and removed scene objects,
    /// each checked as strictly as the runtime checks its own, and its request for a baseline whenever a stream starts.
    /// </summary>
    internal sealed partial class NetworkWorldClient
    {
        bool IWorldMessageHandler.Handle(byte type, ParticipantId sender, WorldWireReader reader)
        {
            if (!IsActive) return false;
            bool read;
            switch (type)
            {
                case WorldMessages.BaselineBegin:
                    read = ReadBegin(reader);
                    break;
                case WorldMessages.BaselineEntries:
                    read = ReadEntries(reader, true);
                    break;
                case WorldMessages.BaselineEnd:
                    read = ReadEnd(reader);
                    break;
                case WorldMessages.Spawn:
                    read = ReadEntries(reader, false);
                    break;
                case WorldMessages.Despawn:
                    read = ReadDespawns(reader);
                    break;
                case WorldMessages.Snapshot:
                    read = ReadSnapshot(reader);
                    break;
                case WorldMessages.SceneRemoved:
                    read = ReadRemovals(reader);
                    break;
                case AuthorityMessages.SpawnAdopted:
                    read = AuthorityWire.TryReadAdopted(reader, out var adopted, out var id);
                    if (read) Adopted(adopted, id);
                    break;
                case AuthorityMessages.SpawnRefused:
                    read = AuthorityWire.TryReadRefused(reader, out var refused, out var reason);
                    if (read) Refused(refused, reason);
                    break;
                default:
                    return false;
            }

            if (!read) counters.MalformedMessages++;
            return read;
        }

        /// <summary>A stream started or restarted: whatever was in flight is gone, so the world starts again from a baseline.</summary>
        private void StreamRestarted()
        {
            ResetEntityCommands();
            receiving = -1;
            expected = 0;
            received = 0;
            synchronized = false;
            requestPending = true;
            nextRequestMicros = 0;
            chunksRead = 0;
            progressPending = false;

            // A rejoin or a replacement takes the player out of Ready until the new snapshot; the hold applies from there.
            TakeHold();

            // Requests in flight on the old stream are lost: every spawn still waiting for its verdict is asked again (WS3.7).
            PredictionsRestarted();
        }

        private void SendRequest(long now)
        {
            request.Begin(WorldMessages.BaselineRequest);
            request.WriteByte(0);
            if (channel.TrySendToHost(request))
            {
                requestPending = false;
                counters.RequestsSent++;
                return;
            }

            nextRequestMicros = now + RequestRetryMicros;
        }

        /// <summary>Tells the host how many chunks of its baseline arrived, so it sends the next few; tried again next frame when refused.</summary>
        private void SendProgress()
        {
            progress.Begin(WorldMessages.BaselineProgress);
            progress.WriteVarUInt((ulong)receiving);
            progress.WriteVarUInt((ulong)chunksRead);
            if (channel.TrySendToHost(progress)) progressPending = false;
        }

        private bool ReadBegin(WorldWireReader reader)
        {
            if (!reader.TryReadInt32(int.MaxValue, out var number) || !reader.TryReadInt32(WorldMessages.MaximumBaselineEntries, out var announced) ||
                !reader.TryReadVarUInt((ulong)WorldMessages.MaximumTimeMicros, out _) || !reader.TryReadUInt64(out var instance) || instance == 0 || !reader.AtEnd)
            {
                return false;
            }

            // Another world: the host's process restarted under the same identity and this player rejoined it within the
            // grace. Nothing it mirrors belongs to that world, whose entities are numbered afresh, so it starts from nothing.
            if (worldInstance != 0 && instance != worldInstance) ReplaceWorld();
            worldInstance = instance;
            receiving = number;
            expected = announced;
            received = 0;
            synchronized = false;
            chunksRead = 0;
            progressPending = false;

            // The new baseline lists the poses of every object still unbound on the host, so older ones are superseded.
            pendingPoses.Clear();
            foreach (var mirrored in entities.Values) mirrored.Stale = true;
            counters.BaselinesReceived++;
            return true;
        }

        private bool ReadEnd(WorldWireReader reader)
        {
            if (!reader.TryReadInt32(int.MaxValue, out var number) || !reader.AtEnd) return false;
            if (number != receiving)
            {
                counters.StaleMessages++;
                return true;
            }

            // What the baseline did not list, the host no longer has.
            foreach (var mirrored in entities.Values)
            {
                if (mirrored.Stale) scratch.Add(mirrored);
            }

            foreach (var mirrored in scratch) Remove(mirrored, true);
            scratch.Clear();

            // An adoption whose entity the baseline did not list names nothing the host has any more (WS3.7).
            EndUnlistedAdoptions();
            receiving = -1;
            synchronized = true;
            synchronizedSince = clock();
            counters.BaselinesApplied++;
            return true;
        }

        private bool ReadEntries(WorldWireReader reader, bool baseline)
        {
            if (baseline)
            {
                if (!reader.TryReadInt32(int.MaxValue, out var number)) return false;
                if (number != receiving)
                {
                    counters.StaleMessages++;
                    return true;
                }

                // Read or not, the chunk is out of flight: the host may send the next.
                chunksRead++;
                progressPending = true;
            }

            if (!reader.TryReadInt32(WorldMessages.MaximumItems, out var count)) return false;
            for (var item = 0; item < count; item++)
            {
                if (!WorldEntryCodec.TryRead(reader, entry)) return false;
                Apply(entry, baseline);
                if (baseline) received++;
            }

            return reader.AtEnd;
        }

        private bool ReadDespawns(WorldWireReader reader)
        {
            if (!reader.TryReadInt32(WorldMessages.MaximumItems, out var count)) return false;
            for (var item = 0; item < count; item++)
            {
                if (!reader.TryReadUInt32(uint.MaxValue, out var value) || value == 0) return false;
                if (!entities.TryGetValue(value, out var mirrored))
                {
                    // An entity this player was adopted into ended before its entry arrived (WS3.7).
                    AdoptionEnded(value);
                    continue;
                }

                if (mirrored.Source == WorldEntrySource.SceneObject && mirrored.Slot != null)
                {
                    // The host's scene object is gone: this player's copy is hidden until it leaves.
                    removedKeys.Add(mirrored.Slot.Key);
                    Hide(mirrored.Slot);
                }

                Remove(mirrored, true);
            }

            return reader.AtEnd;
        }

        private bool ReadSnapshot(WorldWireReader reader)
        {
            if (!reader.TryReadVarUInt((ulong)WorldMessages.MaximumTimeMicros, out var hostTime) ||
                !reader.TryReadInt32(WorldMessages.MaximumItems, out var count))
            {
                return false;
            }

            var arrival = clock();
            for (var item = 0; item < count; item++)
            {
                if (!reader.TryReadUInt32(uint.MaxValue, out var value) || value == 0 || !reader.TryReadTransform(out var pose)) return false;
                if (!entities.TryGetValue(value, out var mirrored))
                {
                    counters.SamplesRefused++;
                    continue;
                }

                var result = driver.Offer(mirrored, (long)hostTime, pose, arrival);
                if (result == SnapshotSampleResult.Accepted || result == SnapshotSampleResult.Reset) counters.SamplesAccepted++;
                else counters.SamplesRefused++;
            }

            return reader.AtEnd;
        }

        private bool ReadRemovals(WorldWireReader reader)
        {
            if (!reader.TryReadInt32(WorldMessages.MaximumItems, out var count)) return false;
            for (var item = 0; item < count; item++)
            {
                if (!reader.TryReadUInt64(out var key)) return false;
                RemoveSceneObject(new SceneObjectKey(key));
            }

            return reader.AtEnd;
        }
    }
}
