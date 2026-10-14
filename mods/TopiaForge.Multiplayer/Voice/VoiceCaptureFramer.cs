using System;
using TopiaForge.Multiplayer.ThirdParty.Concentus.Common;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>Takes the frames a <see cref="VoiceCaptureFramer"/> completes.</summary>
    internal interface IVoiceFrameSink
    {
        /// <summary>
        /// Takes one 20 ms frame of 48 kHz audio: its place in the talker's stream, when its last sample was captured on
        /// the voice clock, and whether capture ended with it, padded with silence.
        /// </summary>
        void OnFrame(ReadOnlySpan<short> frame, ulong timestamp, long capturedMicros, bool endOfCapture);
    }

    /// <summary>
    /// Turns microphone capture at any rate into the codec's 20 ms frames of 48 kHz audio: it resamples with the
    /// vendored Speex resampler, cuts frames, and gives each its timestamp. One thread owns it.
    /// </summary>
    /// <remarks>
    /// Timestamps follow the voice clock, not just the samples: a frame's timestamp is its place in the stream, in frames
    /// since capture first began. While capture flows they advance one per frame. After a gap, such as a released
    /// push-to-talk key or a stall, the stream restarts at the frame the clock has reached, so listeners see the pause
    /// as a pause and their delay estimates stay true. Timestamps never go backwards.
    /// </remarks>
    internal sealed class VoiceCaptureFramer
    {
        internal const int FrameSamples = 960;
        internal const int SampleRate = 48_000;
        internal const long FrameMicros = 20_000;

        /// <summary>A gap this long between one capture's end and the next one's start breaks the stream.</summary>
        internal const long GapMicros = 40_000;

        private const int ResamplerQuality = 5;

        private readonly short[] frame = new short[FrameSamples];
        private readonly float[] resampled = new float[2_048];
        private SpeexResampler? resampler;
        private int resamplerRate;
        private int fill;
        private ulong nextTimestamp;
        private bool started;
        private long originMicros;
        private long lastEndMicros;

        /// <summary>Gets the timestamp the next completed frame will carry.</summary>
        internal ulong NextTimestamp => nextTimestamp;

        /// <summary>Gets the samples of the frame being filled.</summary>
        internal int PendingSamples => fill;

        /// <summary>Gets how many times the stream restarted after a gap.</summary>
        internal long Restarts { get; private set; }

        /// <summary>
        /// Appends one capture: <paramref name="samples"/> at <paramref name="sampleRate"/>, the last of them captured at
        /// <paramref name="endMicros"/>. Completed frames go to <paramref name="sink"/>.
        /// </summary>
        internal void Feed(Span<float> samples, int sampleRate, long endMicros, IVoiceFrameSink sink)
        {
            if (sampleRate < 8_000 || sampleRate > 192_000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (samples.IsEmpty) return;
            var startMicros = endMicros - (samples.Length * 1_000_000L / sampleRate);
            if (!started)
            {
                started = true;
                originMicros = startMicros;
            }
            else if (startMicros - lastEndMicros > GapMicros)
            {
                Restart(startMicros);
            }

            lastEndMicros = endMicros;
            if (sampleRate == SampleRate)
            {
                Append(samples, endMicros, 0, sink);
                return;
            }

            if (resampler == null || resamplerRate != sampleRate)
            {
                // Created only when the device rate changes, so steady capture allocates nothing here.
#pragma warning disable CS0618 // The upstream factory only adds native-library selection, which is not vendored.
                resampler = new SpeexResampler(1, sampleRate, SampleRate, ResamplerQuality);
#pragma warning restore CS0618
                resamplerRate = sampleRate;
            }

            // At most 10 ms of input per call, so each completed frame's capture time is within 10 ms of the truth.
            var step = Math.Max(1, sampleRate / 100);
            for (var start = 0; start < samples.Length;)
            {
                var consumed = Math.Min(step, samples.Length - start);
                var produced = resampled.Length;
                resampler.Process(0, samples.Slice(start, consumed), ref consumed, resampled, ref produced);
                start += consumed;
                var remainingMicros = (samples.Length - start) * 1_000_000L / sampleRate;
                Append(resampled.AsSpan(0, produced), endMicros, remainingMicros, sink);
                if (consumed == 0 && produced == 0) break;
            }
        }

        /// <summary>
        /// Ends capture at <paramref name="endMicros"/>: the frame being filled is padded with silence and sent with
        /// <c>endOfCapture</c>, or, when no samples wait, a frame of silence is, so the talk spurt always gets its last
        /// frame. When capture had stopped more than a gap before, as under a held key whose microphone yielded nothing,
        /// the stream restarts at the frame the clock has reached first, so that last frame is neither stale nor late.
        /// </summary>
        internal void EndCapture(IVoiceFrameSink sink, long endMicros = long.MinValue)
        {
            if (started && endMicros != long.MinValue && endMicros - lastEndMicros > GapMicros)
            {
                Restart(endMicros);
                lastEndMicros = endMicros;
            }

            Array.Clear(frame, fill, FrameSamples - fill);
            fill = 0;
            sink.OnFrame(frame, nextTimestamp++, lastEndMicros, endOfCapture: true);
        }

        private void Restart(long startMicros)
        {
            fill = 0;
            resampler?.ResetMem();
            var reached = startMicros > originMicros ? (ulong)((startMicros - originMicros) / FrameMicros) : 0UL;
            if (reached > nextTimestamp) nextTimestamp = reached;
            Restarts++;
        }

        private void Append(ReadOnlySpan<float> samples, long endMicros, long remainingMicros, IVoiceFrameSink sink)
        {
            for (var index = 0; index < samples.Length; index++)
            {
                var value = samples[index] * 32_767f;
                frame[fill++] = value >= 32_767f ? short.MaxValue : value <= -32_768f ? short.MinValue : (short)Math.Round(value);
                if (fill < FrameSamples) continue;
                fill = 0;
                var after = (samples.Length - index - 1) * 1_000_000L / SampleRate;
                sink.OnFrame(frame, nextTimestamp++, endMicros - remainingMicros - after, endOfCapture: false);
            }
        }
    }
}
