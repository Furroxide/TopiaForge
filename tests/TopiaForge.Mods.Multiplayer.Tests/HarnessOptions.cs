namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Flags for the long runs. By default the harness runs the thirty-minute soak and bounded fuzz loops, which stay fast
/// enough for every pass. <c>--soak</c> adds eight more soak seeds and a four-hour soak, and <c>--fuzz</c> runs every fuzz
/// loop twenty times longer. Every run is seeded, so a failure found either way repeats exactly.
/// </summary>
internal static class HarnessOptions
{
    internal const int LongFuzzScale = 20;

    internal static bool ExtendedSoak { get; private set; }

    internal static bool LongFuzz { get; private set; }

    /// <summary>Gets the factor every fuzz loop multiplies its iteration count by.</summary>
    internal static int FuzzScale => LongFuzz ? LongFuzzScale : 1;

    internal static void Parse(string[] args)
    {
        foreach (var argument in args)
        {
            switch (argument)
            {
                case "--soak":
                    ExtendedSoak = true;
                    break;
                case "--fuzz":
                    LongFuzz = true;
                    break;
                default:
                    throw new ArgumentException("Unknown argument '" + argument + "'. The harness takes --soak and --fuzz.");
            }
        }
    }
}
