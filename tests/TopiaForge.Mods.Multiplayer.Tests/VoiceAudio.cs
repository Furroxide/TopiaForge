namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Measures what came out of a playback stream against what went into the microphone.</summary>
internal static class VoiceAudio
{
    /// <summary>
    /// Finds the delay, in samples, at which <paramref name="output"/> best matches <paramref name="reference"/> over the
    /// window that starts at <paramref name="from"/>: a coarse search on every fourth sample, then an exact one around
    /// the best coarse delay. Returns the delay and the normalized correlation there.
    /// </summary>
    internal static (int Delay, double Correlation) FindDelay(float[] reference, float[] output, int from, int length, int maximumDelay)
    {
        var best = (Delay: 0, Correlation: double.MinValue);
        for (var delay = 0; delay <= maximumDelay; delay += 4)
        {
            var correlation = Correlate(reference, output, from, length, delay, step: 4);
            if (correlation > best.Correlation) best = (delay, correlation);
        }

        var coarse = best.Delay;
        best = (0, double.MinValue);
        for (var delay = Math.Max(0, coarse - 8); delay <= coarse + 8; delay++)
        {
            var correlation = Correlate(reference, output, from, length, delay, step: 1);
            if (correlation > best.Correlation) best = (delay, correlation);
        }

        return best;
    }

    /// <summary>The signal-to-noise ratio, in decibels, of the output delayed by <paramref name="delay"/> against the reference.</summary>
    internal static double SnrDecibels(float[] reference, float[] output, int from, int length, int delay)
    {
        double signal = 0;
        double error = 0;
        for (var index = from; index < from + length; index++)
        {
            double expected = reference[index];
            var actual = index + delay < output.Length ? output[index + delay] : 0;
            signal += expected * expected;
            error += (expected - actual) * (expected - actual);
        }

        return 10 * Math.Log10(signal / Math.Max(error, 1e-12));
    }

    /// <summary>The RMS level, in dBFS, of a stretch of samples in [-1, 1].</summary>
    internal static double LevelDecibels(float[] samples, int from, int length)
    {
        double energy = 0;
        for (var index = from; index < from + length; index++) energy += (double)samples[index] * samples[index];
        var rms = Math.Sqrt(energy / Math.Max(1, length));
        return rms <= 0 ? -120 : 20 * Math.Log10(rms);
    }

    /// <summary>The frequency of a tone, from its rising zero crossings.</summary>
    internal static double ToneHertz(float[] samples, int from, int length, int sampleRate)
    {
        var crossings = 0;
        for (var index = from + 1; index < from + length; index++)
        {
            if (samples[index - 1] < 0 && samples[index] >= 0) crossings++;
        }

        return crossings * (double)sampleRate / length;
    }

    private static double Correlate(float[] reference, float[] output, int from, int length, int delay, int step)
    {
        double product = 0;
        double left = 0;
        double right = 0;
        for (var index = from; index < from + length && index + delay < output.Length; index += step)
        {
            double a = reference[index];
            double b = output[index + delay];
            product += a * b;
            left += a * a;
            right += b * b;
        }

        return left <= 0 || right <= 0 ? 0 : product / Math.Sqrt(left * right);
    }
}
