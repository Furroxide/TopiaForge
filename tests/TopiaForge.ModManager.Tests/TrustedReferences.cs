using System;
using System.IO;
using System.Linq;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The reference paths a Roslyn probe can actually open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TRUSTED_PLATFORM_ASSEMBLIES names every assembly the build resolved, which
    /// includes Unity modules that only ship in some of the game's platform builds.
    /// UnityEngine.NVIDIAModule is the clearest case: the Windows game carries it, the
    /// macOS game does not, so the reference resolves and lands in the deps file while
    /// no file is ever copied next to the test.
    /// </para>
    /// <para>
    /// MetadataReference.CreateFromFile throws FileNotFoundException on such an entry,
    /// which fails the probe for a module none of these tests reference. Dropping the
    /// entries that are not on disk keeps the probes building against the platform
    /// they run on, instead of against the platform the baseline was captured on.
    /// </para>
    /// </remarks>
    internal static class TrustedReferences
    {
        /// <summary>Trusted platform assembly paths that exist on this machine.</summary>
        internal static string[] Paths() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(path => path.Length != 0 && File.Exists(path))
                .ToArray();
    }
}
