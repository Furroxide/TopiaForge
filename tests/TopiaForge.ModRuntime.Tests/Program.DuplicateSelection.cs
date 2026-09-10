using System;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestDuplicateSelectionKeepsHealthyRuntime(string root)
        {
            var fixture = NewFixture(root, "duplicate-selection-healthy", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            fixture.Manifest.SupportedLoaderVersionRange = "*";
            fixture.Manifest.SupportedSdkVersionRange = "*";
            var first = new ModManifest
            {
                Id = "tests.ambiguous",
                Name = "first",
                Version = "1.0.0",
                EntryAssembly = "must-not-be-probed.dll",
                EntryType = "MustNeverConstruct"
            };
            var second = JsonUtil.Clone(first); second.Name = "second";
            var selected = new[] { new ModPackage("not-a-real-package-a", first,
                new InstalledModState { Id = first.Id, Version = first.Version, Enabled = true }, Array.Empty<string>()),
                fixture.Package, new ModPackage("not-a-real-package-b", second,
                new InstalledModState { Id = second.Id, Version = second.Version, Enabled = true }, Array.Empty<string>()) };
            using var lease = fixture.CreateRuntime(); var runtime = (TopiaForge.ModManager.ModRuntime)lease;
            runtime.ConfigureSessionSelection(new EffectiveProfile("duplicate-production", 1, selected.Select(package =>
                new ResolvedPackage(package.Manifest!.Id, package.Manifest.Version, package.Manifest)).ToArray()));
            runtime.Load(selected);
            Assert(runtime.IsLoaded(fixture.Manifest.Id), "A healthy selected package must load despite unrelated duplicate owners.");
            Assert(!runtime.IsLoaded(first.Id), "No ambiguous package may construct an entry point.");
            Assert(runtime.GetLoadFailure(first.Id)?.Contains("ambiguous", StringComparison.OrdinalIgnoreCase) == true,
                "Duplicate owners retain an actionable load failure.");
            var snapshot = runtime.SessionBindings!.Capture();
            Assert(snapshot.Profile.Packages.Count == 3 && snapshot.Contexts.Count == 1
                && snapshot.Contexts.ContainsKey(fixture.Manifest.Id), "Exact selection and loaded context availability remain distinct.");
            Assert(snapshot.BindingFailures.Count(failure => failure.Code.ToString() == "AmbiguousPackage") == 2,
                "Each duplicate identity remains represented in production binding diagnostics.");
            Console.WriteLine("Duplicate selection production runtime test passed.");
        }
    }
}