using System;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>What one <see cref="VoiceJitterBuffer.Pull"/> produced.</summary>
    internal enum VoicePlayout
    {
        /// <summary>Silence: no talk spurt, the buffer is filling, or the talker paused on purpose.</summary>
        Silence = 0,

        /// <summary>The frame arrived in time and decoded.</summary>
        Decoded = 1,

        /// <summary>The frame was missing and was rebuilt from the next frame's forward error correction.</summary>
        Recovered = 2,

        /// <summary>The frame was missing; packet-loss concealment at full level.</summary>
        Concealed = 3,

        /// <summary>Frames have been missing for a while; concealment fading to silence.</summary>
        Faded = 4
    }

    /// <summary>
    /// Turns one speaker's voice frames, which arrive early, late or not at all, into exactly one frame of audio per
    /// <see cref="Pull"/>, which the playback clock calls once per frame. It belongs to one thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A talk spurt starts when a frame arrives while the buffer is idle. Playback waits until the buffer holds the target
    /// delay (<see cref="VoiceDelayEstimator"/>: 95th-percentile jitter plus one frame, 40 to 200 ms, and 60 ms until
    /// there is history to judge by), then plays one stream frame per pull. A missing frame is rebuilt from the next
    /// frame's FEC when that frame is already here, concealed otherwise, and after three concealed frames the next two
    /// fade to silence and the spurt ends. A gap the talker made on purpose plays as silence: the unbroken sequence
    /// numbers across it reveal it, and so does a silence (discontinuous) frame before it. A pause longer than the
    /// maximum delay ends the spurt, as an end-of-talk frame does, so the next one fills to the target again.
    /// </para>
    /// <para>
    /// A frame of another stream generation than the frames before it comes from a new stream, after a rejoin or a
    /// restart, and resets the buffer, its estimates and the decoder. The host stamps the generation, so this holds even
    /// when the new stream's first frames are lost. The voice channel is sequenced, so one speaker's frames arrive in
    /// order, each at most once, and a frame numbered at or below one already received starts a new stream too. A frame
    /// that arrives after its turn has passed is late and dropped.
    /// </para>
    /// <para>
    /// Clock drift and changing network delay change how early frames arrive. <see cref="VoiceSlackWindow"/> watches the
    /// 5th-percentile slack of recent arrivals: at zero frames a concealed frame is inserted, so playout waits longer;
    /// at three a frame is dropped (decoded and discarded, so the codec state stays continuous). Neither takes the delay
    /// outside 40 to 200 ms, each correction waits for fresh arrivals and half a second, and nothing is inserted while an
    /// end of talk is already buffered.
    /// </para>
    /// </remarks>
    internal sealed class VoiceJitterBuffer
    {
        internal const int Slots = 32;
        internal const int ConcealedBeforeFade = 3;
        internal const int FadeFrames = 2;
        internal const int DriftCooldownPulls = 25;

        /// <summary>The 5th-percentile slack, in frames, at or below which a concealed frame is inserted.</summary>
        internal const long InsertAtSlack = 0;

        /// <summary>The 5th-percentile slack, in frames, at or above which a frame is dropped.</summary>
        internal const long DropAtSlack = 3;

        internal const int TargetRefreshPulls = 50;

        /// <summary>The silent pulls after a silence frame that end the spurt: the maximum delay, 200 ms.</summary>
        internal const int PausePullsBeforeIdle = 10;

        private readonly IVoiceDecoder decoder;
        private readonly int frameSamples;
        private readonly int maximumPacketBytes;
        private readonly Slot[] slots = new Slot[Slots];
        private readonly short[] discard;
        private readonly VoiceDelayEstimator delay;
        private readonly VoiceSlackWindow slack = new VoiceSlackWindow();
        private readonly int minimumFrames;
        private readonly int maximumFrames;
        private Phase phase;
        private bool hasStream;
        private ulong stream;
        private ulong playout;
        private ulong newest;
        private ulong lastSequence;
        private ulong highestSequence;
        private ulong lastPlayed;
        private VoiceFrameFlags lastFlags;
        private VoiceFrameFlags newestFlags;
        private int bufferingPulls;
        private int concealedRun;
        private int pausedPulls;
        private int cooldown;
        private int sinceTargetRefresh;

        internal VoiceJitterBuffer(IVoiceCodec codec)
        {
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            decoder = codec.CreateDecoder();
            frameSamples = codec.FrameSamples;
            maximumPacketBytes = codec.MaximumPacketBytes;
            discard = new short[frameSamples];
            var frameMicros = (long)frameSamples * 1_000_000 / codec.SampleRate;
            delay = new VoiceDelayEstimator(frameMicros);
            minimumFrames = (int)(VoiceDelayEstimator.MinimumDelayMicros / frameMicros);
            maximumFrames = (int)(VoiceDelayEstimator.MaximumDelayMicros / frameMicros);
            for (var index = 0; index < Slots; index++) slots[index].Payload = new byte[maximumPacketBytes];
        }

        private enum Phase
        {
            Idle = 0,
            Buffering = 1,
            Playing = 2
        }

        internal VoiceJitterStatistics Statistics { get; } = new VoiceJitterStatistics();

        /// <summary>Gets the playout delay the buffer aims for.</summary>
        internal long TargetDelayMicros => delay.TargetDelayMicros;

        /// <summary>Gets the target delay in frames.</summary>
        internal int TargetFrames => delay.TargetFrames;

        /// <summary>Gets how many stream frames, from the next to play to the newest received, the buffer spans now.</summary>
        internal int Depth => phase == Phase.Idle || newest < playout ? 0 : (int)Math.Min(Slots, newest - playout + 1);

        /// <summary>Gets whether a talk spurt is buffering or playing.</summary>
        internal bool IsActive => phase != Phase.Idle;

        internal void Push(VoiceFrame frame) =>
            Push(frame.Stream, frame.Sequence, frame.Timestamp, frame.Flags, frame.Payload.AsSpan(), frame.ArrivalMicros);

        /// <summary>
        /// Accepts one received frame of stream generation <paramref name="generation"/>. The payload is copied, so the
        /// caller may reuse its buffer.
        /// </summary>
        internal void Push(ulong generation, ulong sequence, ulong timestamp, VoiceFrameFlags flags, ReadOnlySpan<byte> payload, long arrivalMicros)
        {
            Statistics.Received++;
            if (payload.Length < 1 || payload.Length > maximumPacketBytes || sequence == 0)
            {
                Statistics.Invalid++;
                return;
            }

            if (hasStream && (generation != stream || sequence <= highestSequence || timestamp + Slots < lastPlayed))
            {
                // The talker started a new stream, after a rejoin or a restart. The host says so with a new generation,
                // even when the new stream's first frames were lost. A sequenced channel delivers one speaker's frames in
                // order, so a number at or below one already seen can only come from a new stream too.
                Statistics.Restarts++;
                Reset();
            }

            stream = generation;
            delay.Observe(arrivalMicros, timestamp);
            if (phase == Phase.Idle)
            {
                if (hasStream && sequence <= lastSequence && timestamp <= lastPlayed)
                {
                    Statistics.Late++;
                    return;
                }

                StartSpurt(timestamp, sequence, clear: true);
            }
            else if (timestamp < playout)
            {
                if (phase != Phase.Buffering || playout - timestamp >= Slots)
                {
                    if (phase == Phase.Playing) slack.Observe((long)timestamp - (long)playout);
                    Statistics.Late++;
                    return;
                }

                // An earlier frame of a spurt that has not started playing: start from it instead.
                playout = timestamp;
                lastSequence = Math.Min(lastSequence, sequence - 1);
            }
            else if (timestamp - playout >= Slots)
            {
                // Far beyond what the buffer holds: the talker paused without saying so. Start over from this frame.
                Statistics.Resyncs++;
                StartSpurt(timestamp, sequence, clear: true);
            }

            ref var slot = ref slots[(int)(timestamp % Slots)];
            if (slot.Present && slot.Timestamp == timestamp)
            {
                Statistics.Duplicates++;
                return;
            }

            if (phase == Phase.Playing) slack.Observe((long)(timestamp - playout));
            slot.Present = true;
            slot.Timestamp = timestamp;
            slot.Sequence = sequence;
            slot.Flags = flags;
            slot.Length = payload.Length;
            payload.CopyTo(slot.Payload);
            if (timestamp >= newest)
            {
                newest = timestamp;
                newestFlags = flags;
            }

            if (sequence > highestSequence) highestSequence = sequence;
            hasStream = true;
        }

        /// <summary>Fills <paramref name="output"/> with the next frame of audio and says how it was produced.</summary>
        internal VoicePlayout Pull(Span<short> output)
        {
            if (output.Length != frameSamples) throw new ArgumentException("A pull fills exactly one frame.", nameof(output));
            if (phase == Phase.Buffering)
            {
                var depth = newest - playout + 1;
                if (depth < (ulong)delay.TargetFrames && bufferingPulls < delay.TargetFrames - 1)
                {
                    bufferingPulls++;
                    return Silent(output);
                }

                phase = Phase.Playing;
                cooldown = DriftCooldownPulls;
            }

            if (phase == Phase.Idle) return Silent(output);
            if (++sinceTargetRefresh >= TargetRefreshPulls)
            {
                sinceTargetRefresh = 0;
                delay.Update();
            }

            var correction = ControlDrift();
            if (correction == Drift.Insert)
            {
                decoder.Conceal(output);
                Statistics.DriftInserts++;
                return VoicePlayout.Concealed;
            }

            if (correction == Drift.Drop)
            {
                Statistics.DriftDrops++;
                if (TakeSlot(playout, out var dropped))
                {
                    decoder.Decode(dropped.Payload.AsSpan(0, dropped.Length), discard);
                    lastSequence = dropped.Sequence;
                }

                lastPlayed = playout++;
            }

            return PlayNext(output);
        }

        /// <summary>Forgets everything, as for a new talker, and resets the decoder.</summary>
        internal void Reset()
        {
            for (var index = 0; index < Slots; index++) slots[index].Present = false;
            phase = Phase.Idle;
            hasStream = false;
            playout = newest = lastSequence = highestSequence = lastPlayed = 0;
            lastFlags = newestFlags = VoiceFrameFlags.None;
            concealedRun = 0;
            slack.Clear();
            delay.Clear();
            decoder.Reset();
        }

        private enum Drift
        {
            None = 0,
            Drop = 1,
            Insert = 2
        }

        private Drift ControlDrift()
        {
            if (cooldown > 0)
            {
                cooldown--;
                return Drift.None;
            }

            if (slack.Count < VoiceSlackWindow.MinimumSamples) return Drift.None;
            var low = slack.LowPercentile();
            var depth = Depth;
            var correction = Drift.None;

            // A buffer draining towards an end of talk it already holds is not short of frames.
            if (low <= InsertAtSlack && depth < maximumFrames && concealedRun == 0 && (newestFlags & VoiceFrameFlags.EndOfTalk) == 0)
            {
                correction = Drift.Insert;
            }
            else if (low >= DropAtSlack && depth > minimumFrames && IsWaiting(playout))
            {
                correction = Drift.Drop;
            }

            if (correction == Drift.None) return correction;
            slack.Clear();
            cooldown = DriftCooldownPulls;
            return correction;
        }

        private VoicePlayout PlayNext(Span<short> output)
        {
            if (TakeSlot(playout, out var frame))
            {
                var decoded = decoder.Decode(frame.Payload.AsSpan(0, frame.Length), output);
                lastSequence = frame.Sequence;
                lastFlags = frame.Flags;
                lastPlayed = playout++;
                pausedPulls = 0;
                if ((frame.Flags & VoiceFrameFlags.EndOfTalk) != 0 && newest < playout) phase = Phase.Idle;
                if (!decoded) return Concealment(output, alreadyConcealed: true);
                concealedRun = 0;
                Statistics.Decoded++;
                return VoicePlayout.Decoded;
            }

            if (!TryFindNext(out var next))
            {
                // Nothing newer has arrived. After a silence frame the talker has paused; otherwise it is loss, or a
                // talker that stopped without an end-of-talk frame.
                if ((lastFlags & VoiceFrameFlags.Discontinuous) != 0 && concealedRun == 0)
                {
                    lastPlayed = playout++;
                    if (++pausedPulls >= PausePullsBeforeIdle) phase = Phase.Idle;
                    return Silent(output);
                }

                return Concealment(output, alreadyConcealed: false);
            }

            if (next.Sequence == lastSequence + 1)
            {
                // The talker skipped these frames on purpose (discontinuous transmission): play silence until it resumes.
                lastPlayed = playout++;
                concealedRun = 0;
                return Silent(output);
            }

            if (next.Timestamp == playout + 1)
            {
                var recovered = decoder.Recover(next.Payload.AsSpan(0, next.Length), output);
                lastSequence = next.Sequence - 1;
                lastFlags = VoiceFrameFlags.None;
                lastPlayed = playout++;
                pausedPulls = 0;
                if (!recovered) return Concealment(output, alreadyConcealed: true);
                concealedRun = 0;
                Statistics.Recovered++;
                return VoicePlayout.Recovered;
            }

            return Concealment(output, alreadyConcealed: false);
        }

        private VoicePlayout Concealment(Span<short> output, bool alreadyConcealed)
        {
            concealedRun++;
            if (concealedRun > ConcealedBeforeFade + FadeFrames)
            {
                // The spurt is over. A later frame that already arrived starts the next one after a fresh fill.
                if (TryFindNext(out var next)) StartSpurt(next.Timestamp, next.Sequence, clear: false);
                else phase = Phase.Idle;
                return Silent(output);
            }

            if (!alreadyConcealed)
            {
                decoder.Conceal(output);
                lastPlayed = playout++;
            }

            if (concealedRun <= ConcealedBeforeFade)
            {
                Statistics.Concealed++;
                return VoicePlayout.Concealed;
            }

            // Fade linearly across the remaining frames, so the last faded sample reaches zero.
            var step = concealedRun - ConcealedBeforeFade;
            var from = (FadeFrames - step + 1) * 32768 / FadeFrames;
            var to = (FadeFrames - step) * 32768 / FadeFrames;
            for (var index = 0; index < output.Length; index++)
            {
                var gain = from + ((to - from) * (index + 1) / output.Length);
                output[index] = (short)(output[index] * gain / 32768);
            }

            Statistics.Faded++;
            return VoicePlayout.Faded;
        }

        /// <summary>
        /// Starts a talk spurt at <paramref name="timestamp"/>, filling before it plays. With <paramref name="clear"/> the
        /// buffer starts empty; without it, frames already buffered from that timestamp on stay.
        /// </summary>
        private void StartSpurt(ulong timestamp, ulong sequence, bool clear)
        {
            if (clear)
            {
                for (var index = 0; index < Slots; index++) slots[index].Present = false;
                newest = timestamp;
            }

            phase = Phase.Buffering;
            playout = timestamp;
            lastSequence = sequence - 1;
            lastFlags = VoiceFrameFlags.None;
            bufferingPulls = 0;
            concealedRun = 0;
            pausedPulls = 0;
            slack.Clear();
            sinceTargetRefresh = 0;
            delay.Update();
            Statistics.TalkSpurts++;
        }

        private bool TakeSlot(ulong timestamp, out Slot frame)
        {
            ref var slot = ref slots[(int)(timestamp % Slots)];
            if (!slot.Present || slot.Timestamp != timestamp)
            {
                frame = default;
                return false;
            }

            slot.Present = false;
            frame = slot;
            return true;
        }

        private bool IsWaiting(ulong timestamp)
        {
            var slot = slots[(int)(timestamp % Slots)];
            return slot.Present && slot.Timestamp == timestamp;
        }

        private bool TryFindNext(out Slot frame)
        {
            for (var timestamp = playout + 1; timestamp <= newest && timestamp - playout < Slots; timestamp++)
            {
                var slot = slots[(int)(timestamp % Slots)];
                if (slot.Present && slot.Timestamp == timestamp)
                {
                    frame = slot;
                    return true;
                }
            }

            frame = default;
            return false;
        }

        private VoicePlayout Silent(Span<short> output)
        {
            output.Clear();
            Statistics.Silent++;
            return VoicePlayout.Silence;
        }

        private struct Slot
        {
            internal bool Present;
            internal ulong Timestamp;
            internal ulong Sequence;
            internal VoiceFrameFlags Flags;
            internal int Length;
            internal byte[] Payload;
        }
    }
}
