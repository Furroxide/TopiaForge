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
                default:
                    return false;
            }

            if (!read) counters.MalformedMessages++;
            return read;
        }

        /// <summary>A stream started or restarted: whatever was in flight is gone, so the world starts again from a baseline.</summary>
        private void StreamRestarted()
        {
            receiving = -1;
            expected = 0;
            received = 0;
            synchronized = false;
            requestPending = true;
            nextRequestMicros = 0;

            // A rejoin or a replacement takes the player out of Ready until the new snapshot; the hold applies from there.
            TakeHold();
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

        private bool ReadBegin(WorldWireReader reader)
        {
            if (!reader.TryReadInt32(int.MaxValue, out var number) || !reader.TryReadInt32(WorldMessages.MaximumBaselineEntries, out var announced) ||
                !reader.TryReadVarUInt((ulong)WorldMessages.MaximumTimeMicros, out _) || !reader.AtEnd)
            {
                return false;
            }

            receiving = number;
            expected = announced;
            received = 0;
            synchronized = false;

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
            receiving = -1;
            synchronized = true;
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
                if (!entities.TryGetValue(value, out var mirrored)) continue;
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
