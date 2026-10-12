namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Test audio that every platform computes bit for bit: only IEEE basic arithmetic, floor and round, never the
/// runtime's sine, whose last bit differs between C libraries. The frozen codec hashes depend on that.
/// </summary>
internal static class VoiceTestSignal
{
    internal const int SampleRate = 48_000;
    private const double Pi = 3.14159265358979323846;

    // Four formant resonances of a sustained vowel, as (centre hertz, half-width hertz).
    private static readonly double[] Formants = { 700, 130, 1220, 70, 2600, 160, 3300, 250 };

    /// <summary>
    /// A voiced, speech-like signal: harmonics of a fundamental gliding between 100 and 170 Hz every two seconds,
    /// shaped by the formants, under a 3.7 Hz syllable envelope, with a little white noise. It never falls silent.
    /// </summary>
    internal static short[] Voiced(int samples, uint seed = 1)
    {
        var output = new short[samples];
        double phase = 0;
        var noise = seed;
        for (var n = 0; n < samples; n++)
        {
            var cycle = (n % 96_000) / 96_000.0;
            var glide = cycle < 0.5 ? cycle * 2 : 2 - (cycle * 2);
            var fundamental = 100 + (70 * glide);
            phase = Wrap(phase + (2 * Pi * fundamental / SampleRate));
            double sample = 0;
            for (var harmonic = 1; harmonic * fundamental < 4_000; harmonic++)
            {
                sample += Formant(harmonic * fundamental) / harmonic * Sine(Wrap(harmonic * phase));
            }

            var envelope = 0.55 + (0.45 * Sine(Wrap(2 * Pi * 3.7 * n / SampleRate)));
            noise = (noise * 1_664_525u) + 1_013_904_223u;
            var white = ((noise >> 8) / 16_777_216.0) - 0.5;
            output[n] = ToSample(((sample * envelope) + (0.01 * white)) * 7_000);
        }

        return output;
    }

    /// <summary>Full-scale white noise: the hardest input for a speech codec's bitrate.</summary>
    internal static short[] Noise(int samples, uint seed = 7)
    {
        var output = new short[samples];
        var state = seed;
        for (var n = 0; n < samples; n++)
        {
            state = (state * 1_664_525u) + 1_013_904_223u;
            output[n] = (short)(state >> 16);
        }

        return output;
    }

    /// <summary>A pure tone at <paramref name="frequency"/> hertz with peak <paramref name="amplitude"/>, as floats.</summary>
    internal static float[] Tone(int samples, int sampleRate, double frequency, double amplitude)
    {
        var output = new float[samples];
        for (var n = 0; n < samples; n++) output[n] = (float)(amplitude * Sine(Wrap(2 * Pi * frequency * n / sampleRate)));
        return output;
    }

    /// <summary>The signal-to-noise ratio, in decibels, of <paramref name="actual"/> against a slice of the reference.</summary>
    internal static double SnrDecibels(short[] reference, int referenceOffset, ReadOnlySpan<short> actual)
    {
        double signal = 0;
        double error = 0;
        for (var index = 0; index < actual.Length; index++)
        {
            double expected = reference[referenceOffset + index];
            signal += expected * expected;
            error += (expected - actual[index]) * (expected - actual[index]);
        }

        return 10 * Math.Log10(signal / Math.Max(error, 1e-9));
    }

    private static short ToSample(double value) => (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Math.Round(value)));

    private static double Formant(double frequency)
    {
        double gain = 0;
        for (var index = 0; index < Formants.Length; index += 2)
        {
            var offset = (frequency - Formants[index]) / Formants[index + 1];
            gain += 1 / (1 + (offset * offset));
        }

        return gain;
    }

    private static double Wrap(double radians) => radians - (2 * Pi * Math.Floor((radians + Pi) / (2 * Pi)));

    // An odd Taylor polynomial after folding the angle into [-pi/2, pi/2]; its error stays below 1e-7.
    private static double Sine(double x)
    {
        if (x > Pi / 2) x = Pi - x;
        else if (x < -Pi / 2) x = -Pi - x;
        var x2 = x * x;
        return x * (1 - (x2 / 6 * (1 - (x2 / 20 * (1 - (x2 / 42 * (1 - (x2 / 72 * (1 - (x2 / 110 * (1 - (x2 / 156))))))))))));
    }
}
