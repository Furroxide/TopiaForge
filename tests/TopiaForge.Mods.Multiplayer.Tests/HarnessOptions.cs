namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Flags for the long runs. By default the harness runs the thirty-minute soak and bounded fuzz loops, which stay fast
/// enough for every pass. <c>--soak</c> adds eight more soak seeds and a four-hour soak, and <c>--fuzz</c> runs every fuzz
/// loop twenty times longer. Every run is seeded, so a failure found either way repeats exactly. <c>--staging</c> runs
/// only the live spike against the staging control plane (<see cref="StagingSpike"/>), which CI never runs.
/// </summary>
internal static class HarnessOptions
{
    internal const int LongFuzzScale = 20;

    internal static bool ExtendedSoak { get; private set; }

    internal static bool LongFuzz { get; private set; }

    /// <summary>Gets whether to run the live spike against the staging control plane instead of the suite.</summary>
    internal static bool Staging { get; private set; }

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
                case "--staging":
                    Staging = true;
                    break;
                default:
                    throw new ArgumentException("Unknown argument '" + argument + "'. The harness takes --soak, --fuzz and --staging.");
            }
        }
    }
}
