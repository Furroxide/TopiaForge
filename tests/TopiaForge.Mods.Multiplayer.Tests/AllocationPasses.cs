namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Measures what a loop allocates while the runtime may still be compiling it. Work the runtime does on the measuring
/// thread is not the measured code's own: compiling a method on its first call, or moving a long loop to optimized code
/// part way through, can add bytes to the count. That work happens once, while an allocation in the code under test
/// happens on every pass, so a pass runs once to warm and then up to three times, and the fewest bytes count.
/// </summary>
internal static class AllocationPasses
{
    /// <summary>Gets how many measured passes run at most.</summary>
    internal const int MeasuredPasses = 3;

    /// <summary>
    /// Runs <paramref name="pass"/> once unmeasured, then until a pass allocates nothing or three have run, and returns the
    /// fewest bytes a measured pass allocated. The pass is a method of its own, not inlined, that reads this thread's
    /// allocation count around its loop and returns the difference.
    /// </summary>
    internal static long Fewest(Func<long> pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        pass();
        var fewest = long.MaxValue;
        for (var attempt = 0; attempt < MeasuredPasses && fewest != 0; attempt++) fewest = Math.Min(fewest, pass());
        return fewest;
    }
}
