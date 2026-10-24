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
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });
            Assert(!runtime.IsLoaded(fixture.Manifest.Id) && runtime.GetLoadFailure(fixture.Manifest.Id) == reason,
                "loader refuses before executing even a preconstructed valid package");
            AssertTrace(fixture.TracePath);
            Console.WriteLine("SDK loader refusal passed (discovery and pre-load checks; no mod callbacks).");
        }
    }
}
