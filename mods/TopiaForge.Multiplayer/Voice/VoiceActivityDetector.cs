using System;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>What voice activation decided for one captured frame.</summary>
    internal enum VoiceGate
    {
        /// <summary>The talker is quiet and no talk spurt is open: the frame is not sent.</summary>
        Silent = 0,

        /// <summary>The frame is speech, or within the hangover after it: it is sent.</summary>
        Transmit = 1,

        /// <summary>The last frame of the hangover: it is sent and ends the talk spurt.</summary>
        EndOfTalk = 2
    }

    /// <summary>
    /// Voice activation: a frame whose level reaches the threshold is speech, and the spurt stays open for a 300 ms
    /// hangover after the last one, so the ends of words and short pauses are not clipped. The hangover's last frame
    /// carries the end of talk. It also measures every frame's level, for the settings page's meter. One thread owns it.
    /// </summary>
    internal sealed class VoiceActivityDetector
    {
        /// <summary>The hangover: 15 frames, 300 ms.</summary>
        internal const int HangoverFrames = 15;

        /// <summary>The default threshold, -40 dBFS: speech at a normal distance clears it, room noise does not.</summary>
        internal const float DefaultThresholdDb = -40f;

        /// <summary>The level reported for digital silence, and the lowest threshold accepted.</summary>
        internal const float SilenceDb = -96f;

        private float thresholdDb = DefaultThresholdDb;
        private int hangover;

        /// <summary>Gets or sets the level, in dBFS, at which a frame counts as speech: -96 to 0.</summary>
        internal float ThresholdDb
        {
            get => thresholdDb;
            set => thresholdDb = RequireThreshold(value);
        }

        /// <summary>Gets the level of the last frame measured, in dBFS.</summary>
        internal float LastLevelDb { get; private set; } = SilenceDb;

        /// <summary>Gets whether a talk spurt is open: speech, or its hangover.</summary>
        internal bool IsTalking { get; private set; }

        /// <summary>Measures <paramref name="frame"/> and decides whether it is sent.</summary>
        internal VoiceGate Classify(ReadOnlySpan<short> frame)
        {
            LastLevelDb = LevelDb(frame);
            if (LastLevelDb >= thresholdDb)
            {
                IsTalking = true;
                hangover = HangoverFrames;
                return VoiceGate.Transmit;
            }

            if (!IsTalking) return VoiceGate.Silent;
            if (--hangover > 0) return VoiceGate.Transmit;
            IsTalking = false;
            return VoiceGate.EndOfTalk;
        }

        /// <summary>Measures a frame without deciding anything, as push-to-talk does for the meter.</summary>
        internal void Measure(ReadOnlySpan<short> frame) => LastLevelDb = LevelDb(frame);

        /// <summary>Closes any open spurt, as when capture stops or the mode changes.</summary>
        internal void Reset()
        {
            IsTalking = false;
            hangover = 0;
        }

        /// <summary>Gets the RMS level of <paramref name="frame"/> in dBFS, no lower than <see cref="SilenceDb"/>.</summary>
        internal static float LevelDb(ReadOnlySpan<short> frame)
        {
            if (frame.IsEmpty) return SilenceDb;
            double energy = 0;
            foreach (var sample in frame) energy += (double)sample * sample;
            var rms = Math.Sqrt(energy / frame.Length) / 32_768.0;
            return rms <= 0 ? SilenceDb : (float)Math.Max(SilenceDb, 20 * Math.Log10(rms));
        }

        internal static float RequireThreshold(float value)
        {
            if (float.IsNaN(value) || value < SilenceDb || value > 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The activation threshold is -96 to 0 dBFS.");
            }

            return value;
        }
    }
}
