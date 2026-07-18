using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class Program
    {
        private static void TestScanIgnoresSupersededBrokenVersions(string root)
        {
            var paths = NewPaths(root, "scan-superseded");
            var state = new ManagerState();
            var package = Path.Combine(root, "scan-superseded.topiaforgemod");
            CreatePackage(package, "delta.mod", "Delta", "1.0.0", "Delta.dll", "Delta.Entry");
            Assert(new PackageInstaller().Install(package, paths, state, restartRequired: false).Ok, "current version should install");

            // A stale version whose old-schema manifest no longer parses (the real-world source of the
            // per-launch warning wall).
            var staleDirectory = paths.GetPackagePath("delta.mod", "0.1.0");
            Directory.CreateDirectory(staleDirectory);
            File.WriteAllText(Path.Combine(staleDirectory, "topiaforge.mod.json"), "not json at all");

            var packages = new ModRegistry().Scan(paths, state);
            var delta = packages.Where(p => p.PackagePath.Contains("delta.mod")).ToList();

            Assert(delta.Count == 1, "stale broken version should fold into its mod's group");
            Assert(delta[0].IsValid && delta[0].Manifest!.Version == "1.0.0", "the valid current version should win the pick");
        }

        private static void TestScanStillReportsFullyBrokenPackage(string root)
        {
            var paths = NewPaths(root, "scan-broken");
            var brokenDirectory = paths.GetPackagePath("epsilon.mod", "0.1.0");
            Directory.CreateDirectory(brokenDirectory);
            File.WriteAllText(Path.Combine(brokenDirectory, "topiaforge.mod.json"), "not json at all");

            var packages = new ModRegistry().Scan(paths, new ManagerState());
            var epsilon = packages.Where(p => p.PackagePath.Contains("epsilon.mod")).ToList();

            Assert(epsilon.Count == 1, "a mod with no valid version should still surface");
            Assert(!epsilon[0].IsValid && epsilon[0].Errors.Count > 0, "the broken package should carry its error");
        }

        private static void TestScanSelectsDependencyCompatibleProviderVersion(string root)
        {
            var paths = NewPaths(root, "scan-compatible-provider");
            var state = new ManagerState();
            var installer = new PackageInstaller();
            var providerOne = Path.Combine(root, "selection-provider-1.topiaforgemod");
            var providerTwo = Path.Combine(root, "selection-provider-2.topiaforgemod");
            var consumer = Path.Combine(root, "selection-consumer.topiaforgemod");
            CreatePackageCandidate(
                providerOne,
                "selection.provider",
                "Selection provider",
                "1.0.0",
                "*",
                corruptEntryAssembly: false);
            CreatePackageCandidate(
                providerTwo,
                "selection.provider",
                "Selection provider",
                "2.0.0",
                "*",
                corruptEntryAssembly: false);
            CreatePackageCandidate(
                consumer,
                "selection.consumer",
                "Selection consumer",
                "1.0.0",
                "*",
                corruptEntryAssembly: false,
                dependencies: new Dictionary<string, string>
                {
                    ["selection.provider"] = ">=1.0.0 <2.0.0"
                });
            Assert(installer.Install(providerOne, paths, state, false).Ok, "provider 1 should install");
            Assert(installer.Install(providerTwo, paths, state, false).Ok, "provider 2 should install");
            Assert(installer.Install(consumer, paths, state, false).Ok, "consumer should install");

            var packages = new ModRegistry().Scan(paths, state);
            var selectedProvider = packages.Single(package => package.Manifest?.Id == "selection.provider");
            var resolution = new DependencyResolver().Resolve(packages);

            Assert(selectedProvider.Manifest!.Version == "1.0.0",
                "an unpinned provider should downgrade to the highest version compatible with its consumer");
            Assert(selectedProvider.SelectionReason.Contains("highest compatible version '1.0.0'", StringComparison.Ordinal),
                "dependency-compatible recovery should be recorded in diagnostics");
            Assert(resolution.Errors.Count == 0 && resolution.OrderedPackages.Count == 2,
                "a satisfiable retained provider version must keep the complete dependency graph loadable");
        }

        private static void TestScanBacktracksConsumerVersionForCompleteAssignment(string root)
        {
            var paths = NewPaths(root, "scan-compatible-assignment");
            var state = new ManagerState();
            var installer = new PackageInstaller();
            var providerOne = Path.Combine(root, "assignment-provider-1.topiaforgemod");
            var providerTwo = Path.Combine(root, "assignment-provider-2.topiaforgemod");
            var consumerOne = Path.Combine(root, "assignment-consumer-1.topiaforgemod");
            var consumerTwo = Path.Combine(root, "assignment-consumer-2.topiaforgemod");
            var guard = Path.Combine(root, "assignment-guard.topiaforgemod");
            CreatePackageCandidate(providerOne, "assignment.provider", "Provider", "1.0.0", "*", false);
            CreatePackageCandidate(providerTwo, "assignment.provider", "Provider", "2.0.0", "*", false);
            CreatePackageCandidate(
                consumerOne,
                "assignment.consumer",
                "Consumer",
                "1.0.0",
                "*",
                false,
                new Dictionary<string, string> { ["assignment.provider"] = "<2.0.0" });
            CreatePackageCandidate(
                consumerTwo,
                "assignment.consumer",
                "Consumer",
                "2.0.0",
                "*",
                false,
                new Dictionary<string, string> { ["assignment.provider"] = ">=2.0.0" });
            CreatePackageCandidate(
                guard,
                "assignment.guard",
                "Guard",
                "1.0.0",
                "*",
                false,
                new Dictionary<string, string> { ["assignment.provider"] = "<2.0.0" });
            foreach (var archive in new[] { providerOne, providerTwo, consumerOne, consumerTwo, guard })
            {
                Assert(installer.Install(archive, paths, state, false).Ok, "assignment fixture should install");
            }

            var packages = new ModRegistry().Scan(paths, state);
            var selectedConsumer = packages.Single(package => package.Manifest?.Id == "assignment.consumer");
            var selectedProvider = packages.Single(package => package.Manifest?.Id == "assignment.provider");
            var resolution = new DependencyResolver().Resolve(packages);

            Assert(selectedConsumer.Manifest!.Version == "1.0.0" && selectedProvider.Manifest!.Version == "1.0.0",
                "selection should backtrack a consumer as well as its provider to find the highest complete assignment");
            Assert(resolution.Errors.Count == 0 && resolution.OrderedPackages.Count == 3,
                "the selected assignment should load every compatible retained package");
        }

    }
}
