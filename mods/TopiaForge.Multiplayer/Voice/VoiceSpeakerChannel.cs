using System;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// One remote speaker on the voice worker: their jitter buffer and decoder, and the playback stream the audio
    /// thread reads. Every 20 ms slot pulls one frame and writes it to the stream. One thread owns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The worker's clock and the audio device's clock drift apart, so the stream would slowly fill up or run dry. The
    /// channel watches the stream's lowest level over each second of slots, measured just before it writes, which is
    /// the cushion left after the reader's reads. Below <see cref="LowWaterSamples"/> it pulls one extra frame; above
    /// it by more than <see cref="HighWaterFrames"/> frames it skips one pull. The jitter buffer's own drift control
    /// then sees a slightly faster or slower listener, which it already handles. An underrun tops the stream up with
    /// silence at once, enough for the reader's largest read.
    /// </para>
    /// <para>
    /// When nobody has read the stream for <see cref="IdleSlots"/> slots, as before an avatar's emitter exists, the
    /// channel still pulls and decodes, so the jitter buffer keeps pace, but drops the audio instead of filling the ring.
    /// What the reader left in the ring when it stopped is flushed then, so a reader that comes back hears the speaker
    /// as they are, never what they said before the pause.
    /// </para>
    /// <para>
    /// A talker who rejoins or restarts sends a new stream, and the host stamps it with a new generation. The jitter
    /// buffer starts again at its first frame to arrive, even when that frame reaches this player before the session
    /// says the talker is back, so the channel itself never resets for a rejoin.
    /// </para>
    /// </remarks>
    internal sealed class VoiceSpeakerChannel
    {
        internal const int FrameSamples = 960;

        /// <summary>The cushion the reader should always find left after a read: 10 ms.</summary>
        internal const int LowWaterSamples = 480;

        /// <summary>How far above the cushion the level may sit before latency is trimmed: three frames.</summary>
        internal const int HighWaterFrames = 3;

        /// <summary>The slots per drift decision: one second.</summary>
        internal const int ControlSlots = 50;

        /// <summary>The slots without a read after which nobody counts as listening: 200 ms.</summary>
        internal const int IdleSlots = 10;

        /// <summary>
        /// The depth past which a jitter buffer holds a stall's backlog rather than jitter: a frame more than the 200 ms
        /// the buffer ever aims for.
        /// </summary>
        internal const int BacklogFrames = (int)(VoiceDelayEstimator.MaximumDelayMicros / VoiceCaptureFramer.FrameMicros) + 1;

        private readonly VoiceJitterBuffer buffer;
        private readonly short[] pcm = new short[FrameSamples];
        private long lastReadCalls;
        private long lastUnderruns;
        private int idleSlots = IdleSlots;
        private int windowMinimum = int.MaxValue;
        private int windowSlots;
        private int adjustment;

        internal VoiceSpeakerChannel(IVoiceCodec codec, VoicePlaybackStream stream)
        {
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));
            buffer = new VoiceJitterBuffer(codec ?? throw new ArgumentNullException(nameof(codec)));
            lastUnderruns = stream.Underruns;
            lastReadCalls = stream.ReadCalls;
        }

        internal VoicePlaybackStream Stream { get; }

        internal VoiceJitterBuffer Buffer => buffer;

        internal long Pulls { get; private set; }
        internal long Skips { get; private set; }
        internal long Extras { get; private set; }
        internal long TopUps { get; private set; }
        internal long Discarded { get; private set; }
        internal long Resets { get; private set; }

        /// <summary>Gets the frames <see cref="Trim"/> dropped from a stall's backlog.</summary>
        internal long Trimmed { get; private set; }

        /// <summary>Gets whether anyone has read the stream recently.</summary>
        internal bool IsListened => idleSlots < IdleSlots;

        /// <summary>
        /// Hands one received frame to the jitter buffer with its stream generation. A frame of a new generation, after
        /// the talker rejoined or restarted, starts the buffer and its decoder again at that frame, whatever its sequence.
        /// </summary>
        internal void Push(in VoiceInboundFrame frame, ReadOnlySpan<byte> payload) =>
            buffer.Push(frame.Stream, frame.Sequence, frame.Timestamp, frame.Flags, payload, frame.ArrivalMicros);

        /// <summary>Runs one 20 ms playout slot at <paramref name="nowMicros"/> on the voice clock.</summary>
        internal void Slot(long nowMicros)
        {
            var wasListened = IsListened;
            var reads = Stream.ReadCalls;
            if (reads != lastReadCalls)
            {
                lastReadCalls = reads;
                idleSlots = 0;
            }
            else if (idleSlots < IdleSlots && ++idleSlots == IdleSlots)
            {
                // The reader stopped: what it left in the ring is old by the time it comes back, so it goes now.
                Stream.Flush();
            }

            if (!IsListened)
            {
                Pull(nowMicros, keep: false);
                ResetControl();
                lastUnderruns = Stream.Underruns;
                Stream.Counters.Publish(this);
                return;
            }

            // A reader that just arrived, or one that ran dry, gets a cushion for its largest read at once.
            var level = Stream.Buffered;
            var underruns = Stream.Underruns;
            if (!wasListened || underruns != lastUnderruns)
            {
                lastUnderruns = underruns;
                var cushion = LowWaterSamples + Stream.LargestRequest;
                if (level < cushion)
                {
                    Stream.WriteSilence(cushion - level);
                    TopUps++;
                }

                ResetControl();
            }
            else
            {
                if (level < windowMinimum) windowMinimum = level;
                if (++windowSlots >= ControlSlots)
                {
                    if (windowMinimum < LowWaterSamples) adjustment = 1;
                    else if (windowMinimum > LowWaterSamples + (HighWaterFrames * FrameSamples)) adjustment = -1;
                    ResetControl();
                }
            }

            var pulls = 1;
            if (adjustment < 0)
            {
                pulls = 0;
                Skips++;
            }
            else if (adjustment > 0)
            {
                pulls = 2;
                Extras++;
            }

            adjustment = 0;
            for (var index = 0; index < pulls; index++) Pull(nowMicros, keep: true);
            Stream.Counters.Publish(this);
        }

        /// <summary>
        /// Drops what a stall left queued beyond the most delay the jitter buffer ever aims for: frames that arrived in a
        /// burst after this player's main thread, the host's, or the worker stalled. They are decoded, so the codec state
        /// stays continuous, and discarded, until the buffer holds its target and a frame. Left in place, they would play
        /// late until drift control caught up, a frame every half second or more.
        /// </summary>
        internal void Trim()
        {
            if (!buffer.IsActive || buffer.Depth <= BacklogFrames) return;
            for (var pulls = 0; pulls < VoiceJitterBuffer.Slots && buffer.IsActive && buffer.Depth > buffer.TargetFrames + 1; pulls++)
            {
                buffer.Pull(pcm);
                Trimmed++;
            }

            Stream.Counters.Publish(this);
        }

        /// <summary>
        /// Drops the jitter buffer's frames, timing and decoder state, and the audio waiting in the stream, as after a mute
        /// or a change of session.
        /// </summary>
        internal void Reset()
        {
            buffer.Reset();
            Stream.Flush();
            ResetControl();
            adjustment = 0;
            lastUnderruns = Stream.Underruns;
            Resets++;
            Stream.Counters.Publish(this);
        }

        private void Pull(long nowMicros, bool keep)
        {
            var playout = buffer.Pull(pcm);
            Pulls++;
            if (playout == VoicePlayout.Decoded || playout == VoicePlayout.Recovered || playout == VoicePlayout.Concealed)
            {
                Stream.MarkVoice(nowMicros);
            }

            if (keep) Stream.Write(pcm);
            else Discarded++;
        }

        private void ResetControl()
        {
            windowMinimum = int.MaxValue;
            windowSlots = 0;
        }
    }
}
