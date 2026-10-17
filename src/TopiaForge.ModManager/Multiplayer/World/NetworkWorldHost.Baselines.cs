using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>A player the host baselines: the stream it asked on, and its baseline under way.</summary>
    internal sealed class HostWorldPeer
    {
        internal HostWorldPeer(ParticipantId id, ulong stream, long nowMicros)
        {
            Id = id;
            Stream = stream;
            Resyncs = new TokenBucket(NetworkWorldHost.ResyncBurstTokens, 1, nowMicros);
        }

        internal ParticipantId Id { get; }

        /// <summary>Gets the stream generation the player last asked on.</summary>
        internal ulong Stream { get; private set; }

        /// <summary>
        /// Gets whether the player needs a new baseline: it asked, or a broadcast skipped it. The baseline under way, if any,
        /// is superseded once the new one begins.
        /// </summary>
        internal bool Wanted { get; set; }

        /// <summary>Gets whether the player already asked on this stream, so a further request is a resynchronisation.</summary>
        internal bool AskedOnStream { get; set; }

        internal HostBaseline? Active { get; set; }

        /// <summary>Gets the number of the latest baseline started for this player; it never repeats.</summary>
        internal int Number { get; set; }

        /// <summary>Gets the bucket that bounds a player's resynchronisations within one stream.</summary>
        internal TokenBucket Resyncs { get; }

        /// <summary>Starts again on a new stream: whatever was under way belonged to the old one.</summary>
        internal void Restart(ulong stream)
        {
            Stream = stream;
            Wanted = false;
            AskedOnStream = false;
            Active = null;
        }
    }

    /// <summary>One baseline under way: what it lists, fixed when it started, and how far it has been sent.</summary>
    internal sealed class HostBaseline
    {
        internal HostBaseline(int number)
        {
            Number = number;
        }

        internal int Number { get; }

        internal List<uint> Ids { get; } = new List<uint>();

        internal List<SceneObjectKey> Removed { get; } = new List<SceneObjectKey>();

        /// <summary>Gets the unbound objects censused at the promotion, whose pose the baseline carries.</summary>
        internal List<SceneObjectSlot> Poses { get; } = new List<SceneObjectSlot>();

        internal int Cursor { get; set; }

        internal int RemovedCursor { get; set; }

        internal int PoseCursor { get; set; }

        internal int Expected => Ids.Count + Removed.Count + Poses.Count;

        internal int EntriesSent { get; set; }

        internal bool IsFullySent => Cursor >= Ids.Count && RemovedCursor >= Removed.Count && PoseCursor >= Poses.Count;
    }

    internal sealed partial class NetworkWorldHost
    {
        /// <summary>
        /// What a baseline message asks of a player's window before it goes: room for a whole system message, 32 KiB, though
        /// a baseline message carries at most <see cref="BaselineChunkBytes"/>. So a baseline always leaves the broadcasts
        /// behind it at least the difference, and pacing a baseline never makes a broadcast skip the player it is for. The
        /// channel answers <c>CanSend</c> for one message at most, so this is the most headroom the probe can ask for.
        /// </summary>
        internal const int BaselineProbeBytes = InternalSystemLimits.MaximumPayloadBytes;

        /// <summary>The most one baseline message carries; the largest entry, a 16 KiB description with its fields, fits.</summary>
        internal const int BaselineChunkBytes = 20 * 1024;

        /// <summary>The most baseline messages one player gets in one frame, so a large world spreads its cost.</summary>
        internal const int BaselineMessagesPerFrame = 8;

        /// <summary>The most players the host keeps baseline state for; the engine bounds its streams far below this.</summary>
        internal const int MaximumPeers = 256;

        /// <summary>A resynchronisation within one stream costs this many tokens of a bucket that refills one a second.</summary>
        internal const int ResyncCostTokens = 10;

        /// <summary>The bucket holds two resynchronisations; then one comes every ten seconds.</summary>
        internal const int ResyncBurstTokens = 2 * ResyncCostTokens;

        private readonly Dictionary<ParticipantId, HostWorldPeer> peers = new Dictionary<ParticipantId, HostWorldPeer>();
        private readonly List<HostWorldPeer> peerOrder = new List<HostWorldPeer>();
        private ulong lastGeneration;

        /// <summary>Gets how many players the host baselines.</summary>
        internal int PeerCount => peers.Count;

        /// <summary>Gets a player's baseline under way: how many entries it sent of how many; false when none runs.</summary>
        internal bool TryGetBaselineProgress(ParticipantId peer, out int sent, out int expected)
        {
            if (peers.TryGetValue(peer, out var state) && state.Active != null)
            {
                sent = state.Active.EntriesSent;
                expected = state.Active.Expected;
                return true;
            }

            sent = 0;
            expected = 0;
            return false;
        }

        private void ResetPeers()
        {
            peers.Clear();
            peerOrder.Clear();
            lastGeneration = channel.Generation;
        }

        /// <summary>
        /// A stream's generation changes when a player joins, rejoins, is moved to a replaced session or leaves. A player
        /// whose stream ended is forgotten; one whose stream restarted lost what was in flight, so its baseline starts
        /// again when it asks on the new stream, as it does once it sees the restart itself.
        /// </summary>
        private void CheckGenerations()
        {
            var generation = channel.Generation;
            if (generation == lastGeneration) return;
            lastGeneration = generation;
            for (var index = peerOrder.Count - 1; index >= 0; index--)
            {
                var peer = peerOrder[index];
                var stream = channel.GetGeneration(peer.Id);
                if (stream == 0)
                {
                    peers.Remove(peer.Id);
                    peerOrder.RemoveAt(index);
                    continue;
                }

                if (stream != peer.Stream)
                {
                    peer.Restart(stream);
                    counters.StreamRestarts++;
                }
            }
        }

        /// <summary>
        /// A player asks for a baseline. The first request on a stream is always answered; a further one is a
        /// resynchronisation, which a bucket bounds, so a player cannot make the host resend its world at will.
        /// </summary>
        private void Request(ParticipantId sender)
        {
            var stream = channel.GetGeneration(sender);
            if (stream == 0)
            {
                counters.RequestsRefused++;
                return;
            }

            var now = clock();
            if (!peers.TryGetValue(sender, out var peer))
            {
                if (peers.Count >= MaximumPeers)
                {
                    counters.RequestsRefused++;
                    return;
                }

                peer = new HostWorldPeer(sender, stream, now);
                peers.Add(sender, peer);
                peerOrder.Add(peer);
            }
            else if (peer.Stream != stream)
            {
                peer.Restart(stream);
            }

            if (peer.AskedOnStream && !peer.Resyncs.TryTake(now, ResyncCostTokens))
            {
                counters.RequestsRefused++;
                return;
            }

            // The baseline under way, if any, goes on until the new one begins and supersedes it.
            peer.AskedOnStream = true;
            peer.Wanted = true;
            counters.Requests++;
        }

        /// <summary>
        /// A broadcast skipped these players because their window was full: each missed a message, so each gets a new
        /// baseline once its window has room, which supersedes any baseline under way.
        /// </summary>
        private void Skipped(List<ParticipantId> players)
        {
            foreach (var player in players)
            {
                if (!peers.TryGetValue(player, out var peer) || !peer.AskedOnStream) continue;
                peer.Wanted = true;
                counters.Rebaselines++;
            }
        }

        private void PumpBaselines(long now)
        {
            var scanned = false;
            foreach (var peer in peerOrder)
            {
                if (peer.Wanted)
                {
                    // The list is fixed only when the baseline begins, so a broadcast skipped before that is covered by it,
                    // and a player whose window stays full costs nothing but this check.
                    if (!channel.CanSendTo(peer.Id, BaselineProbeBytes)) continue;
                    if (!scanned)
                    {
                        // Whatever moved since the last tick is bound before the baseline lists the world.
                        ScanFirstMotion(int.MaxValue);
                        scanned = true;
                    }

                    if (!Begin(peer, now)) continue;
                }

                if (peer.Active != null) Pump(peer);
            }
        }

        /// <summary>Fixes what a new baseline lists, as the world is now, and sends its first message.</summary>
        private bool Begin(HostWorldPeer peer, long now)
        {
            var baseline = new HostBaseline(peer.Number + 1);
            foreach (var hosted in ordered)
            {
                if (hosted.IsAlive) baseline.Ids.Add(hosted.Id.Value);
            }

            index.CollectRemovedKeys(baseline.Removed);
            index.CollectUnknownOrigins(baseline.Poses);
            message.Begin(WorldMessages.BaselineBegin);
            message.WriteVarUInt((ulong)baseline.Number);
            message.WriteVarUInt((ulong)baseline.Expected);
            message.WriteVarUInt((ulong)Math.Min(now, WorldMessages.MaximumTimeMicros));
            if (!channel.TrySendTo(peer.Id, message, BaselineProbeBytes)) return false;
            peer.Number = baseline.Number;
            peer.Active = baseline;
            peer.Wanted = false;
            counters.BaselinesStarted++;
            return true;
        }

        /// <summary>
        /// Sends what the window allows of one player's baseline, probing for <see cref="BaselineProbeBytes"/> first. Entries
        /// are written as they are now, and an entity that ended since the baseline began is left out, since the player
        /// hears of its end anyway. A chunk the window refuses is written again next frame.
        /// </summary>
        private void Pump(HostWorldPeer peer)
        {
            var baseline = peer.Active!;
            var messages = 1;
            while (!baseline.IsFullySent)
            {
                if (messages >= BaselineMessagesPerFrame) return;
                var cursor = baseline.Cursor;
                var removedCursor = baseline.RemovedCursor;
                var poseCursor = baseline.PoseCursor;
                var count = FillChunk(baseline);
                if (count == 0)
                {
                    // Only ended entities were left, or, which the bounds rule out, one entry larger than a chunk.
                    if (baseline.Cursor == cursor && baseline.RemovedCursor == removedCursor && baseline.PoseCursor == poseCursor) SkipOversizedEntry(baseline);
                    continue;
                }

                message.Begin(WorldMessages.BaselineEntries);
                message.WriteVarUInt((ulong)baseline.Number);
                message.WriteVarUInt((ulong)count);
                message.TryAppend(chunk);
                if (!channel.TrySendTo(peer.Id, message, BaselineProbeBytes))
                {
                    baseline.Cursor = cursor;
                    baseline.RemovedCursor = removedCursor;
                    baseline.PoseCursor = poseCursor;
                    return;
                }

                baseline.EntriesSent += count;
                messages++;
            }

            if (messages >= BaselineMessagesPerFrame) return;
            message.Begin(WorldMessages.BaselineEnd);
            message.WriteVarUInt((ulong)baseline.Number);
            if (!channel.TrySendTo(peer.Id, message, BaselineProbeBytes)) return;
            peer.Active = null;
            counters.BaselinesCompleted++;
        }

        private void SkipOversizedEntry(HostBaseline baseline)
        {
            if (baseline.Cursor < baseline.Ids.Count) baseline.Cursor++;
            else if (baseline.RemovedCursor < baseline.Removed.Count) baseline.RemovedCursor++;
            else baseline.PoseCursor++;
            counters.EntriesTooLarge++;
        }

        /// <summary>Writes the next entries into <c>chunk</c>, as many as fit; ended entities are passed over.</summary>
        private int FillChunk(HostBaseline baseline)
        {
            chunk.Reset();
            var count = 0;
            while (baseline.Cursor < baseline.Ids.Count && count < WorldMessages.MaximumItems)
            {
                if (!entities.TryGetValue(baseline.Ids[baseline.Cursor], out var hosted) || !hosted.IsAlive)
                {
                    baseline.Cursor++;
                    continue;
                }

                entry.Reset();
                WriteEntry(entry, hosted);
                if (!chunk.TryAppend(entry)) return count;
                baseline.Cursor++;
                count++;
            }

            while (baseline.Cursor >= baseline.Ids.Count && baseline.RemovedCursor < baseline.Removed.Count && count < WorldMessages.MaximumItems)
            {
                entry.Reset();
                WorldEntryCodec.WriteRemoved(entry, baseline.Removed[baseline.RemovedCursor]);
                if (!chunk.TryAppend(entry)) return count;
                baseline.RemovedCursor++;
                count++;
            }

            // An object bound since the baseline began is announced to everyone, and one gone since is said to be gone.
            while (baseline.Cursor >= baseline.Ids.Count && baseline.RemovedCursor >= baseline.Removed.Count &&
                   baseline.PoseCursor < baseline.Poses.Count && count < WorldMessages.MaximumItems)
            {
                var slot = baseline.Poses[baseline.PoseCursor];
                if (!slot.IsBindable || !slot.Object.TryReadPose(out var pose))
                {
                    baseline.PoseCursor++;
                    continue;
                }

                entry.Reset();
                WorldEntryCodec.WriteScenePose(entry, slot.Key, pose);
                if (!chunk.TryAppend(entry)) return count;
                baseline.PoseCursor++;
                count++;
            }

            return count;
        }
    }
}
