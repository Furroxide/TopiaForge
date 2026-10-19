using System;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// One speaker's decoded voice on its way to the audio thread: a single-producer, single-consumer ring of 48 kHz mono
    /// samples. The voice worker writes; the audio thread calls <see cref="Read"/>, which is wait-free. It takes no lock,
    /// never blocks, allocates nothing, and fills with silence whatever the ring cannot supply, counting the underrun.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each side owns its position and publishes it with a volatile write; the other side only reads it, so neither ever
    /// moves the other's. The writer never overwrites what the reader has not passed: a full ring drops the newest audio.
    /// </para>
    /// <para>
    /// The reader bounds latency itself. When more than <see cref="MaximumBufferedSamples"/> wait, as after the audio
    /// thread stalled or attached late, it skips to the newest <see cref="SkipToSamples"/>. The writer flushes the same
    /// way: it publishes a position, and the reader skips to it on its next read.
    /// </para>
    /// <para>
    /// It is the runtime's <see cref="IVoicePlaybackStream"/> as it is: the SPI hands out this stream, never a wrapper
    /// that could move to another stream, so a stream that ends stays ended and never plays anybody else.
    /// </para>
    /// </remarks>
    internal sealed class VoicePlaybackStream : IVoicePlaybackStream
    {
        /// <summary>The rate of every sample, in hertz.</summary>
        internal const int Rate = 48_000;

        /// <summary>The ring's size: 16,384 samples, about 341 ms.</summary>
        internal const int Capacity = 16_384;

        /// <summary>The most audio that may wait before a read skips ahead: 200 ms.</summary>
        internal const int MaximumBufferedSamples = 9_600;

        /// <summary>The audio a skip keeps: the newest 60 ms.</summary>
        internal const int SkipToSamples = 2_880;

        private const int Mask = Capacity - 1;
        private const float SampleScale = 1f / 32_768f;

        private readonly float[] ring = new float[Capacity];

        // Written by the producer only.
        private long written;
        private long flushTo;
        private long overflowSamples;
        private long lastVoiceMicros = long.MinValue;
        private int primed;
        private int ended;

        // Written by the consumer only.
        private long read;
        private long readCalls;
        private long samplesRead;
        private long underruns;
        private long underrunSamples;
        private long skippedSamples;
        private int largestRequest;

        internal VoicePlaybackStream(ParticipantId speaker)
        {
            Speaker = speaker;
        }

        /// <summary>Gets the participant whose voice this stream plays.</summary>
        public ParticipantId Speaker { get; }

        /// <summary>Gets the rate of every sample: 48,000 Hz.</summary>
        public int SampleRate => Rate;

        /// <summary>Gets the playback counters the voice worker publishes for this speaker.</summary>
        internal VoiceSpeakerCounters Counters { get; } = new VoiceSpeakerCounters();

        /// <summary>Gets how many times the reader asked for audio. A count that stops moving means nobody is listening.</summary>
        internal long ReadCalls => Volatile.Read(ref readCalls);

        /// <summary>Gets the most samples one read asked for, which sets how far ahead the writer must stay.</summary>
        internal int LargestRequest => Volatile.Read(ref largestRequest);

        /// <summary>Gets the reads that found too little audio and played silence for the rest.</summary>
        internal long Underruns => Volatile.Read(ref underruns);

        internal long UnderrunSamples => Volatile.Read(ref underrunSamples);

        /// <summary>Gets the samples the reader skipped to keep latency bounded, or because the writer flushed.</summary>
        internal long SkippedSamples => Volatile.Read(ref skippedSamples);

        /// <summary>Gets the samples the writer dropped because the ring was full.</summary>
        internal long OverflowSamples => Volatile.Read(ref overflowSamples);

        internal long SamplesRead => Volatile.Read(ref samplesRead);

        /// <summary>Gets when the speaker was last heard, on the voice clock, or <see cref="long.MinValue"/>.</summary>
        internal long LastVoiceMicros => Volatile.Read(ref lastVoiceMicros);

        /// <summary>Gets whether the speaker left; the stream then plays silence without counting underruns.</summary>
        public bool IsEnded => Volatile.Read(ref ended) != 0;

        /// <summary>Gets how many samples wait for the reader, as the writer sees it.</summary>
        internal int Buffered
        {
            get
            {
                var consumer = Math.Max(Volatile.Read(ref read), Volatile.Read(ref flushTo));
                return (int)Math.Max(0, written - consumer);
            }
        }

        /// <summary>
        /// Fills <paramref name="count"/> samples of <paramref name="buffer"/> from <paramref name="offset"/> with the
        /// speaker's voice, in [-1, 1], and silence where the ring runs short. Call it from one thread only, the audio
        /// thread; it returns <paramref name="count"/>.
        /// </summary>
        public int Read(float[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || count > buffer.Length - offset) throw new ArgumentOutOfRangeException(nameof(count));
            // The flush position is loaded before the end: the writer flushes to an end it has already published, and the
            // end only grows, so the end loaded second is never behind the flush loaded first. Loaded the other way round,
            // a write and a flush in between put the flush past the end, and the copy below got a negative length.
            var position = read;
            var flush = Volatile.Read(ref flushTo);
            var end = Volatile.Read(ref written);
            var skipped = 0L;
            if (flush > position)
            {
                var target = Math.Min(flush, end);
                skipped += target - position;
                position = target;
            }

            if (end - position > MaximumBufferedSamples)
            {
                skipped += end - SkipToSamples - position;
                position = end - SkipToSamples;
            }

            var copied = (int)Math.Min(end - position, count);
            var start = (int)(position & Mask);
            var first = Math.Min(copied, Capacity - start);
            Array.Copy(ring, start, buffer, offset, first);
            if (copied > first) Array.Copy(ring, 0, buffer, offset + first, copied - first);
            if (copied < count)
            {
                Array.Clear(buffer, offset + copied, count - copied);
                if (Volatile.Read(ref primed) != 0 && Volatile.Read(ref ended) == 0)
                {
                    Volatile.Write(ref underruns, underruns + 1);
                    Volatile.Write(ref underrunSamples, underrunSamples + (count - copied));
                }
            }

            if (skipped > 0) Volatile.Write(ref skippedSamples, skippedSamples + skipped);
            if (count > largestRequest) Volatile.Write(ref largestRequest, count);
            Volatile.Write(ref samplesRead, samplesRead + copied);
            Volatile.Write(ref read, position + copied);
            Volatile.Write(ref readCalls, readCalls + 1);
            return count;
        }

        /// <summary>Appends one frame of decoded audio. The writer's thread only; a full ring drops what does not fit.</summary>
        internal int Write(ReadOnlySpan<short> pcm)
        {
            var position = written;
            var free = Capacity - (int)(position - Volatile.Read(ref read));
            var count = Math.Min(free, pcm.Length);
            for (var index = 0; index < count; index++) ring[(int)((position + index) & Mask)] = pcm[index] * SampleScale;
            if (count < pcm.Length) Volatile.Write(ref overflowSamples, overflowSamples + (pcm.Length - count));
            Publish(position + count);
            return count;
        }

        /// <summary>Appends <paramref name="samples"/> of silence, as when the reader ran dry and needs a cushion.</summary>
        internal int WriteSilence(int samples)
        {
            var position = written;
            var free = Capacity - (int)(position - Volatile.Read(ref read));
            var count = Math.Max(0, Math.Min(free, samples));
            for (var index = 0; index < count; index++) ring[(int)((position + index) & Mask)] = 0f;
            Publish(position + count);
            return count;
        }

        /// <summary>Drops everything waiting: the reader skips past it on its next read.</summary>
        internal void Flush() => Volatile.Write(ref flushTo, written);

        /// <summary>Records that the speaker was heard at <paramref name="nowMicros"/>, for talking indicators.</summary>
        internal void MarkVoice(long nowMicros) => Volatile.Write(ref lastVoiceMicros, nowMicros);

        /// <summary>Marks the speaker gone: what waits is dropped, and later reads play silence without counting underruns.</summary>
        internal void End()
        {
            Volatile.Write(ref ended, 1);
            Flush();
        }

        private void Publish(long position)
        {
            Volatile.Write(ref written, position);
            if (primed == 0) Volatile.Write(ref primed, 1);
        }
    }
}
