using System;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ReleaseVersionTests
    {
        internal static void Run()
        {
            var manifest = new ModManifest
            {
                SchemaVersion = ModManifest.CurrentSchemaVersion,
                Id = "example.release-contract",
                Name = "Release contract",
                Author = new ModAuthor { Name = "TopiaForge Tests" },
                Version = "1.0.0",
                EntryAssembly = "Example.ReleaseContract.dll",
                EntryType = "Example.ReleaseContract.Mod",
                SupportedGameVersionRange = "*",
                SupportedLoaderVersionRange = ">=0.2.0 <0.3.0",
                SupportedSdkVersionRange = ">=0.2.0 <0.3.0"
            };
            var currentIssues = ManifestValidator.Validate(manifest);
            Assert(currentIssues.Count == 0,
                "the bundled runtime must admit stable 0.2 requirements: " + string.Join("; ", currentIssues));
            Assert(TopiaForgeVersions.LoaderVersion == "0.2.0", "loader must be stable 0.2.0");
            Assert(TopiaForgeVersions.SdkVersion == "0.2.0", "SDK must be stable 0.2.0");
            Assert(TopiaForgeVersions.BepInExPluginVersion == "0.2.0", "BepInEx numeric version must match");
            foreach (var version in new[] { "0.1.0-rc.1", "0.2.0-rc.1", "0.3.0" })
            {
                var issues = ManifestValidator.Validate(manifest,
                    new ManifestValidationContext(loaderVersion: version, sdkVersion: version));
                Assert(version == "0.3.0"
                    ? issues.Count == 2 && issues.All(issue => issue.Contains("0.3.0", StringComparison.Ordinal))
                    : issues.Count == 1 && issues[0] == "This mod needs TopiaForge 0.2.0 or newer; update TopiaForge.",
                    "both bounds must refuse " + version + ": " + string.Join("; ", issues));
            }
            Console.WriteLine("ReleaseVersionTests passed (3 identities, current admission, 3 refusals).");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Release version: " + message);
        }
    }
}
