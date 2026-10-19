using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// The remote speakers: one playback stream each, and the session they are in. Every speaker costs a 64 KiB ring, a
    /// jitter buffer and a decoder, so how many there are is bounded by the session, never by what the host names.
    /// </summary>
    internal sealed partial class VoiceService
    {
        /// <summary>
        /// How long a speaker the session's participants do not list may keep a stream: 2 s. Voice can come just before
        /// the participant list that names its speaker; a speaker still unlisted after this is nobody in the session.
        /// </summary>
        internal const long EarlyVoiceGraceMicros = 2_000_000;

        /// <summary>
        /// The span of one speaker's frames in one pump, in frames, past which they are a stall's backlog rather than
        /// jitter: the 200 ms the jitter buffer ever aims for.
        /// </summary>
        internal const ulong BacklogSpanFrames = (ulong)(VoiceDelayEstimator.MaximumDelayMicros / VoiceCaptureFramer.FrameMicros);

        private readonly Dictionary<string, VoicePlaybackStream> streams = new Dictionary<string, VoicePlaybackStream>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> unlisted = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, BacklogSpan> spans = new Dictionary<string, BacklogSpan>(StringComparer.Ordinal);
        private readonly HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> ending = new List<string>();
        private readonly int streamLimit;
        private IReadOnlyList<MultiplayerParticipant>? observedParticipants;
        private MultiplayerSessionId observedSession;
        private MultiplayerSessionState observedState;
        private ulong observedListGeneration;
        private bool observedAny;
        private int connectedOthers;
        private long framesReceived;
        private long inboundOverflow;
        private long unlistedFramesDropped;
        private long unlistedStreamsEnded;

        /// <summary>
        /// Gets the playback stream for <paramref name="speaker"/>, creating it the first time. Streams are bounded: at
        /// most the engine's <see cref="EngineOptions.MaximumParticipants"/> at once. A speaker the session's participants
        /// list always gets one, ahead of the rest. One they do not list yet gets one only while there is room, and keeps it
        /// for <see cref="EarlyVoiceGraceMicros"/> unless the list names them by then. Past the bound, an ended stream that
        /// plays silence comes back, and frames for that speaker are dropped and counted. A stream ends, playing silence,
        /// when its speaker leaves; a later call gives a new one.
        /// </summary>
        internal VoicePlaybackStream GetPlaybackStream(ParticipantId speaker)
        {
            if (!speaker.IsValid) throw new ArgumentException("A speaker is required.", nameof(speaker));
            engine.RequireOwnerThread();
            var stream = disposed ? null : EnsureStream(speaker, clock());
            if (stream != null) return stream;
            var refused = new VoicePlaybackStream(speaker);
            refused.End();
            return refused;
        }

        /// <summary>
        /// Gets <paramref name="speaker"/>'s playback stream without making one the session has no use for: the stream the
        /// speaker has, or a new one, made as <see cref="GetPlaybackStream"/> makes it, for a speaker the session's
        /// participants listed at the last pump while the bound has room. Otherwise it returns false, and makes and allocates
        /// nothing, so a caller whose stream ended may ask every frame. Asking <see cref="GetPlaybackStream"/> instead makes a
        /// stream with a decoder for 2 s for a speaker the participants do not list, and a new ring at every call the bound
        /// refuses.
        /// </summary>
        internal bool TryGetPlaybackStream(ParticipantId speaker, out VoicePlaybackStream? stream)
        {
            if (!speaker.IsValid) throw new ArgumentException("A speaker is required.", nameof(speaker));
            engine.RequireOwnerThread();
            stream = disposed ? null : EnsureStream(speaker, clock(), listedOnly: true);
            return stream != null;
        }

        /// <summary>Gets whether <paramref name="speaker"/> was heard in the last 250 ms, on the main thread: it refuses any other.</summary>
        internal bool IsSpeaking(ParticipantId speaker)
        {
            engine.RequireOwnerThread();
            if (!speaker.IsValid || !streams.TryGetValue(speaker.Value, out var stream)) return false;
            var heard = stream.LastVoiceMicros;
            return heard != long.MinValue && clock() - heard < SpeakingMicros;
        }

        private void ObserveSession()
        {
            var snapshot = engine.Snapshot;
            var participants = snapshot.Participants;
            if (observedAny && ReferenceEquals(participants, observedParticipants) && snapshot.Id.Equals(observedSession) &&
                snapshot.State == observedState && engine.ParticipantListGeneration == observedListGeneration)
            {
                return;
            }

            CatchUpWithTheList();
            var sessionChanged = !observedAny || !snapshot.Id.Equals(observedSession);
            var rejoined = observedAny && observedState != MultiplayerSessionState.Ready && snapshot.State == MultiplayerSessionState.Ready;
            observedAny = true;
            observedParticipants = participants;
            observedSession = snapshot.Id;
            observedState = snapshot.State;
            observedListGeneration = engine.ParticipantListGeneration;
            ready = snapshot.State == MultiplayerSessionState.Ready;
            lock (inbound.Gate)
            {
                // Each host process numbers its streams from one, so a buffer kept from another session could take a new
                // stream for an old one, and after a lost or stalled link what was buffered is out of date. Every jitter
                // buffer goes then, after the frames already queued and before any new one.
                if (sessionChanged || rejoined) inbound.Writable.AddControl(VoiceControlKind.ResetAll);
                present.Clear();
                connectedOthers = 0;
                for (var index = 0; index < participants.Count; index++)
                {
                    var participant = participants[index];
                    if (participant.IsLocal) continue;
                    var id = participant.Id.Value;
                    present.Add(id);
                    known.Add(id);
                    if (participant.IsConnected) connectedOthers++;
                }

                ending.Clear();
                foreach (var id in known)
                {
                    if (!present.Contains(id)) ending.Add(id);
                }

                foreach (var id in ending)
                {
                    known.Remove(id);
                    EndStream(id);
                }
            }

            AdoptListenerMutes(snapshot.State);
        }

        /// <summary>Ends the streams of speakers the participants still do not list once their grace is over.</summary>
        private void SweepUnlisted(long nowMicros)
        {
            if (unlisted.Count == 0) return;
            ending.Clear();
            foreach (var entry in unlisted)
            {
                if (present.Contains(entry.Key) || nowMicros - entry.Value >= EarlyVoiceGraceMicros) ending.Add(entry.Key);
            }

            lock (inbound.Gate)
            {
                foreach (var id in ending)
                {
                    unlisted.Remove(id);
                    if (present.Contains(id)) continue;
                    EndStream(id);
                    unlistedStreamsEnded++;
                }
            }
        }

        private bool ReceiveFrames(long nowMicros)
        {
            if (worker.Failed)
            {
                // Nobody will play them; keep the engine's queue drained all the same.
                while (engine.TryReceiveVoiceFrame(out _)) framesReceived++;
                return false;
            }

            var any = false;
            lock (inbound.Gate)
            {
                var batch = inbound.Writable;
                var first = batch.FrameCount;
                while (engine.TryReceiveVoiceFrame(out var frame))
                {
                    framesReceived++;
                    if (EnsureStream(frame.Speaker, nowMicros) == null)
                    {
                        unlistedFramesDropped++;
                        continue;
                    }

                    any = true;
                    if (!batch.TryAddFrame(frame)) inboundOverflow++;
                }

                if (batch.FrameCount - first > (int)BacklogSpanFrames) RestampBacklogs(batch, first);
            }

            return any;
        }

        /// <summary>
        /// Gets or creates <paramref name="speaker"/>'s stream, or returns null when the bound leaves no room. With
        /// <paramref name="listedOnly"/>, only a speaker the participants list gets a new one.
        /// </summary>
        private VoicePlaybackStream? EnsureStream(ParticipantId speaker, long nowMicros, bool listedOnly = false)
        {
            CatchUpWithTheList();
            var id = speaker.Value;
            if (streams.TryGetValue(id, out var stream)) return stream;
            var listed = present.Contains(id);
            if (listedOnly && !listed) return null;
            if (streams.Count >= streamLimit && !(listed && EndOldestUnlisted())) return null;
            stream = new VoicePlaybackStream(speaker);
            streams[id] = stream;
            if (!listed) unlisted[id] = nowMicros;
            lock (inbound.Gate) inbound.Writable.AddControl(VoiceControlKind.Attach, speaker, stream);
            worker.Signal();
            return stream;
        }

        /// <summary>Makes room for a listed speaker by ending the stream unlisted the longest.</summary>
        private bool EndOldestUnlisted()
        {
            string? oldest = null;
            var since = long.MaxValue;
            foreach (var entry in unlisted)
            {
                if (entry.Value < since)
                {
                    oldest = entry.Key;
                    since = entry.Value;
                }
            }

            if (oldest == null) return false;
            unlisted.Remove(oldest);
            lock (inbound.Gate) EndStream(oldest);
            unlistedStreamsEnded++;
            return true;
        }

        /// <summary>
        /// Ends every speaker's stream, listed or not, once the engine has let go of the list they were named from: at a join
        /// that withheld it or a role change, after which their ids may name other people. That holds even when the next
        /// host's list arrived in the same pump, so the session never listed nobody. The pump does this as it looks at the
        /// session, and so does a stream asked for sooner, as from a session callback during a join, so no caller is ever
        /// handed a stream named from the list of before.
        /// </summary>
        private void CatchUpWithTheList()
        {
            var listGeneration = engine.ParticipantListGeneration;
            if (!observedAny || listGeneration == observedListGeneration) return;
            observedListGeneration = listGeneration;
            lock (inbound.Gate)
            {
                present.Clear();
                ending.Clear();
                foreach (var id in streams.Keys) ending.Add(id);
                foreach (var id in ending) EndStream(id);
                known.Clear();
            }
        }

        /// <summary>Ends a speaker's stream and stops playing them. The caller holds the inbound gate.</summary>
        private void EndStream(string id)
        {
            unlisted.Remove(id);
            if (!streams.TryGetValue(id, out var stream)) return;
            streams.Remove(id);
            stream.End();
            inbound.Writable.AddControl(VoiceControlKind.Remove, stream.Speaker);
        }

        /// <summary>
        /// Gives each speaker's frames that came out of one pump spanning more than the most delay the jitter buffer aims
        /// for the arrival times they would have had in step with the newest of them. Such a burst is a stall's backlog,
        /// on this player's main thread or the host's. Stamped with the moment it was read, it would look like a second of
        /// jitter and push the speaker's delay target to its ceiling for the next 128 frames; the worker trims the backlog
        /// itself down to the target.
        /// </summary>
        private void RestampBacklogs(VoiceInboundBatch batch, int first)
        {
            spans.Clear();
            for (var index = first; index < batch.FrameCount; index++)
            {
                ref var frame = ref batch.Frame(index);
                var id = frame.Speaker.Value;
                if (spans.TryGetValue(id, out var span) && span.Stream == frame.Stream)
                {
                    if (frame.Timestamp < span.Oldest) span.Oldest = frame.Timestamp;
                    if (frame.Timestamp >= span.Newest)
                    {
                        span.Newest = frame.Timestamp;
                        span.Arrival = frame.ArrivalMicros;
                    }

                    spans[id] = span;
                }
                else
                {
                    // A speaker's first frame here, or the first of a new stream, which counts from there.
                    spans[id] = new BacklogSpan(frame.Stream, frame.Timestamp, frame.ArrivalMicros);
                }
            }

            for (var index = first; index < batch.FrameCount; index++)
            {
                ref var frame = ref batch.Frame(index);
                if (!spans.TryGetValue(frame.Speaker.Value, out var span) || span.Stream != frame.Stream) continue;
                if (span.Newest - span.Oldest <= BacklogSpanFrames) continue;
                frame.ArrivalMicros = span.Arrival - ((long)(span.Newest - frame.Timestamp) * VoiceCaptureFramer.FrameMicros);
            }
        }

        /// <summary>One speaker's frames in one pump, from the oldest to the newest of one stream.</summary>
        private struct BacklogSpan
        {
            internal BacklogSpan(ulong stream, ulong timestamp, long arrival)
            {
                Stream = stream;
                Oldest = timestamp;
                Newest = timestamp;
                Arrival = arrival;
            }

            internal ulong Stream;
            internal ulong Oldest;
            internal ulong Newest;
            internal long Arrival;
        }
    }
}
