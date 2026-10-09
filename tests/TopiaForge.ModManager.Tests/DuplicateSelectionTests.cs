using System;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class DuplicateSelectionTests
    {
        internal static void Run()
        {
            foreach (var sameVersion in new[] { false, true })
            {
                var first = Package("example.duplicate", "1.0.0", "first");
                var second = Package("EXAMPLE.DUPLICATE", sameVersion ? "1.0.0" : "2.0.0", "second");
                var healthy = Package("example.healthy", "1.0.0", "healthy");
                var incoming = new[] { first, second, healthy };
                var profile = new EffectiveProfile("duplicate-selection", 1, incoming.Select(package =>
                    new ResolvedPackage(package.Manifest!.Id, package.Manifest.Version, package.Manifest)).ToArray());
                var registry = new RuntimeBindingRegistry(profile);
                var frozen = registry.VerifyAndFreezeSelection(incoming.Reverse().ToArray());
                Assert(frozen.Count == 3 && registry.Capture().Profile.Packages.Count == 3,
                    "Duplicate selections must remain present without choosing one owner.");
                Assert(registry.Capture().BindingFailures.Count(failure => failure.Code.ToString() == "AmbiguousPackage") == 2,
                    "Each ambiguous package must retain its identity and structured failure.");
                Reject(() => registry.BeginPackageLoad(profile.Packages[0].Identity), "Ambiguous owners cannot acquire load attempts.");
                Assert(registry.BeginDiscovery(profile.Packages[0].Identity, default) == null,
                    "Ambiguous owners cannot construct discovery sources.");
                var attempt = registry.BeginPackageLoad(profile.Packages[2].Identity);
                Assert(registry.CommitFailed(attempt, "controlled healthy preflight"), "Healthy owners remain independently processable.");
                Assert(registry.Capture().BindingFailures.Count(failure => failure.Code.ToString() == "AmbiguousPackage") == 2,
                    "Healthy owner publication must not erase duplicate diagnostics.");
                first.Manifest!.Name = "changed after capture";
                Assert(frozen.Single(package => package.PackagePath == "first").Manifest!.Name == "first",
                    "Frozen duplicate metadata must remain immutable.");
                var changed = new RuntimeBindingRegistry(profile);
                Reject(() => changed.VerifyAndFreezeSelection(incoming), "Exact selected metadata changes must be rejected.");
                var missing = new RuntimeBindingRegistry(profile);
                Reject(() => missing.VerifyAndFreezeSelection(new[] { second, healthy }), "Missing duplicate members must be rejected.");
                first.Manifest.Name = "first";
                var substituted = new RuntimeBindingRegistry(profile);
                Reject(() => substituted.VerifyAndFreezeSelection(new[] { first, first, healthy }),
                    "Duplicating one manifest cannot replace the distinct selected manifest even when versions match.");
                registry.BeginShutdown(); registry.CompleteShutdown();
                Assert(registry.Capture().BindingFailures.Count(failure => failure.Code.ToString() == "AmbiguousPackage") == 2,
                    "Shutdown retains immutable ambiguity evidence.");
            }
            Console.WriteLine("Duplicate selection registry tests passed.");
        }
        private static ModPackage Package(string id, string version, string name) => new ModPackage(name,
            new ModManifest { Id = id, Version = version, Name = name },
            new InstalledModState { Id = id, Version = version, Enabled = true }, Array.Empty<string>());
        private static void Reject(Action operation, string message)
        {
            try { operation(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException(message);
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}