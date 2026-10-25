using System;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestLoaderVersionRefusalBeforeCode(string root)
        {
            var fixture = NewFixture(root, "newer-sdk", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            fixture.Manifest.SupportedLoaderVersionRange = ">=0.2.0 <0.3.0";
            const string reason = "This mod needs TopiaForge 0.2.0 or newer; update TopiaForge.";
            Assert(ManifestValidator.Validate(fixture.Manifest).Contains(reason), "discovery refuses the loader floor");
            // The TF1010 fix raises both floors; the player is told once what to do.
            fixture.Manifest.SupportedSdkVersionRange = ">=0.2.0 <0.3.0";
            var errors = ManifestValidator.Validate(fixture.Manifest);
            Assert(errors.Count(error => error == reason) == 1 && errors.All(error => !error.Contains("SDK", StringComparison.Ordinal)),
                "both raised floors give one update message: " + string.Join(" | ", errors));
            Assert(ManifestValidator.LoaderCompatibilityError(">0.2.0", "0.2.0") == "This mod needs a TopiaForge newer than 0.2.0; update TopiaForge."
                && ManifestValidator.LoaderCompatibilityError(">=0.1.0 <0.2.0", "0.3.0") == "supportedLoaderVersionRange does not include loader 0.3.0.",
                "an excluded floor and a ceiling keep their exact advice");
            foreach (var (loader, sdk, expected) in new[] {
                (">=0.2.0 <1.0.0", ">=0.3.0 <1.0.0", "This mod needs TopiaForge 0.3.0 or newer; update TopiaForge."),
                (">=0.3.0 <1.0.0", ">=0.2.0 <1.0.0", "This mod needs TopiaForge 0.3.0 or newer; update TopiaForge."),
                (">=0.2.0 <1.0.0", ">0.2.0 <1.0.0", "This mod needs a TopiaForge newer than 0.2.0; update TopiaForge.") })
            {
                fixture.Manifest.SupportedLoaderVersionRange = loader;
                fixture.Manifest.SupportedSdkVersionRange = sdk;
                var mixed = ManifestValidator.Validate(fixture.Manifest).Where(error => error.Contains("update TopiaForge", StringComparison.Ordinal)).ToArray();
                Assert(mixed.Length == 1 && mixed[0] == expected,
                    "different floors give one update to the stricter requirement: " + string.Join(" | ", mixed));
            }
            fixture.Manifest.SupportedLoaderVersionRange = ">=0.2.0 <0.3.0";
            fixture.Manifest.SupportedSdkVersionRange = ">=0.2.0 <0.3.0";
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });
            Assert(!runtime.IsLoaded(fixture.Manifest.Id) && runtime.GetLoadFailure(fixture.Manifest.Id) == reason,
                "loader refuses before executing even a preconstructed valid package");
            AssertTrace(fixture.TracePath);
            Console.WriteLine("SDK loader refusal passed (discovery and pre-load checks; one update message; no mod callbacks).");
        }
    }
}
