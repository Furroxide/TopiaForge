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
        private static void TestInboxInstallConsumesFiles(string root)
        {
            var paths = NewPaths(root, "inbox-consume");
            var state = new ManagerState();
            var alphaFile = Path.Combine(paths.PackageInbox, "alpha.topiaforgemod");
            var betaFile = Path.Combine(paths.PackageInbox, "beta.topiaforgemod");
            CreatePackage(alphaFile, "alpha.mod", "Alpha", "1.0.0", "Alpha.dll", "Alpha.Entry");
            CreatePackage(betaFile, "beta.mod", "Beta", "1.0.0", "Beta.dll", "Beta.Entry");

            var results = new PackageInstaller().InstallInbox(paths, state, restartRequired: false);

            Assert(results.Count == 2, "both inbox packages should be processed");
            Assert(results.All(r => r.Install!.Ok), "both inbox packages should install");
            Assert(results.All(r => r.Consumed), "both inbox files should be consumed");
            Assert(!File.Exists(alphaFile) && !File.Exists(betaFile), "consumed inbox files should be gone");
            Assert(state.Find("alpha.mod")?.RestartRequired == false, "startup-style install should not flag restart");
            Assert(state.Find("beta.mod")?.Version == "1.0.0", "state should track the installed version");
            var receipt = JsonUtil.LoadFile(
                Path.Combine(paths.GetPackagePath("alpha.mod", "1.0.0"), PackageInstallReceipt.FileName),
                new PackageInstallReceipt());
            Assert(receipt.Source == PackageInstallReceipt.InboxSource,
                "runtime inbox installs should retain inbox provenance without persisting the inbox path");
        }

        private static void TestStrictManifestExtensions()
        {
            var fixtures = Path.Combine(FindRepoRoot(), "tests", "fixtures", "manifests");
            var corpus = File.ReadAllLines(Path.Combine(fixtures, "corpus.txt"));
            foreach (var rawCase in corpus)
            {
                var testCase = rawCase.Trim();
                if (testCase.Length == 0 || testCase.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = testCase.IndexOf(' ');
                Assert(separator > 0, "manifest corpus entries must contain an expectation and fixture name");
                var expectedValid = string.Equals(
                    testCase.Substring(0, separator),
                    "valid",
                    StringComparison.Ordinal);
                var fixtureName = testCase.Substring(separator + 1).Trim();
                var actualValid = false;
                try
                {
                    var manifest = ModManifestJson.Deserialize(
                        File.ReadAllText(Path.Combine(fixtures, fixtureName)));
                    actualValid = ManifestValidator.Validate(manifest).Count == 0;
                }
                catch (InvalidDataException)
                {
                    actualValid = false;
                }
                catch (FormatException)
                {
                    actualValid = false;
                }

                Assert(
                    actualValid == expectedValid,
                    "C# manifest validator disagreed with corpus expectation for " + fixtureName);
            }
        }

        private static void TestInboxNewestVersionWins(string root)
        {
            var paths = NewPaths(root, "inbox-newest");
            var state = new ManagerState();
            var oldFile = Path.Combine(paths.PackageInbox, "gamma-1.0.0.topiaforgemod");
            var newFile = Path.Combine(paths.PackageInbox, "gamma-1.1.0.topiaforgemod");
            CreatePackage(oldFile, "gamma.mod", "Gamma", "1.0.0", "Gamma.dll", "Gamma.Entry");
            CreatePackage(newFile, "gamma.mod", "Gamma", "1.1.0", "Gamma.dll", "Gamma.Entry");

            var results = new PackageInstaller().InstallInbox(paths, state, restartRequired: false);

            Assert(results.Count == 2, "both inbox files should be reported");
            var winner = results.Single(r => !r.Superseded);
            var loser = results.Single(r => r.Superseded);
            Assert(winner.Install!.Ok && winner.Install.Manifest!.Version == "1.1.0", "highest version should install");
            Assert(loser.Install == null, "superseded file should not be installed");
            Assert(state.Find("gamma.mod")?.Version == "1.1.0", "state should select the highest version");
            Assert(!Directory.Exists(paths.GetPackagePath("gamma.mod", "1.0.0")), "old version should never hit disk");
            Assert(!File.Exists(oldFile) && !File.Exists(newFile), "both inbox files should be consumed");
        }

        private static void TestInboxPrereleasePrecedence(string root)
        {
            var paths = NewPaths(root, "inbox-prerelease");
            var state = new ManagerState();
            var lowerFile = Path.Combine(paths.PackageInbox, "delta-alpha-2.topiaforgemod");
            var higherFile = Path.Combine(paths.PackageInbox, "delta-alpha-10.topiaforgemod");
            CreatePackage(lowerFile, "delta.prerelease", "Delta", "1.0.0-alpha.2", "Delta.dll", "Delta.Entry");
            CreatePackage(higherFile, "delta.prerelease", "Delta", "1.0.0-alpha.10", "Delta.dll", "Delta.Entry");

            var results = new PackageInstaller().InstallInbox(paths, state, restartRequired: false);

            Assert(results.Count == 2, "both prerelease inbox files should be reported");
            var winner = results.Single(r => !r.Superseded);
            var loser = results.Single(r => r.Superseded);
            Assert(winner.Install!.Ok && winner.Install.Manifest!.Version == "1.0.0-alpha.10",
                "numeric prerelease identifiers should use SemVer precedence when selecting an inbox winner");
            Assert(loser.Install == null && loser.Consumed,
                "the lower prerelease should be superseded and consumed without installation");
            Assert(state.Find("delta.prerelease")?.Version == "1.0.0-alpha.10",
                "state should retain the SemVer-highest prerelease");
            Assert(!Directory.Exists(paths.GetPackagePath("delta.prerelease", "1.0.0-alpha.2")),
                "the lower prerelease should never reach the package store");
            Assert(!File.Exists(lowerFile) && !File.Exists(higherFile),
                "both prerelease inbox files should be consumed");
        }

        private static void TestInboxFallsBackFromIncompatibleHigherVersion(string root)
        {
            var paths = NewPaths(root, "inbox-incompatible-fallback");
            var state = new ManagerState();
            var lowerFile = Path.Combine(paths.PackageInbox, "compatible-1.0.0.topiaforgemod");
            var higherFile = Path.Combine(paths.PackageInbox, "incompatible-2.0.0.topiaforgemod");
            CreatePackageCandidate(
                lowerFile,
                "fallback.compatibility",
                "Compatibility fallback",
                "1.0.0",
                supportedGameVersionRange: "0.0.2309",
                corruptEntryAssembly: false);
            CreatePackageCandidate(
                higherFile,
                "fallback.compatibility",
                "Compatibility fallback",
                "2.0.0",
                supportedGameVersionRange: ">=0.0.3000",
                corruptEntryAssembly: false);
            var validationContext = new ManifestValidationContext(
                gameVersion: "0.0.2309",
                requireKnownGameVersion: true);

            var results = new PackageInstaller().InstallInbox(
                paths,
                state,
                restartRequired: false,
                validationContext);

            Assert(results.Count == 2, "both compatible and incompatible candidates should be reported");
            var installed = results.Single(result => result.Install?.Ok == true);
            var rejected = results.Single(result => string.Equals(
                result.FilePath,
                higherFile,
                StringComparison.Ordinal));
            var rejection = rejected.Install;
            Assert(installed.Install!.Manifest!.Version == "1.0.0" && installed.Consumed,
                "the highest compatible candidate should install even when a newer candidate is incompatible");
            Assert(!rejected.Superseded && rejection != null && !rejection.Ok,
                "the incompatible higher candidate should retain its rejected-preflight outcome");
            Assert(rejection!.Errors.Any(error => error.Contains(
                    "supportedGameVersionRange",
                    StringComparison.Ordinal)),
                "the incompatible candidate should report its actionable game-range error");
            Assert(!rejected.Consumed && File.Exists(higherFile),
                "the rejected incompatible candidate should remain in the inbox for inspection");
            Assert(state.Find("fallback.compatibility")?.Version == "1.0.0",
                "state should select the compatible lower version");
        }

        private static void TestInboxFallsBackFromCorruptHigherVersion(string root)
        {
            var paths = NewPaths(root, "inbox-corrupt-fallback");
            var state = new ManagerState();
            var lowerFile = Path.Combine(paths.PackageInbox, "valid-1.0.0.topiaforgemod");
            var higherFile = Path.Combine(paths.PackageInbox, "corrupt-2.0.0.topiaforgemod");
            CreatePackageCandidate(
                lowerFile,
                "fallback.integrity",
                "Integrity fallback",
                "1.0.0",
                supportedGameVersionRange: "*",
                corruptEntryAssembly: false);
            CreatePackageCandidate(
                higherFile,
                "fallback.integrity",
                "Integrity fallback",
                "2.0.0",
                supportedGameVersionRange: "*",
                corruptEntryAssembly: true);

            var results = new PackageInstaller().InstallInbox(paths, state, restartRequired: false);

            Assert(results.Count == 2, "both valid and corrupt candidates should be reported");
            var installed = results.Single(result => result.Install?.Ok == true);
            var rejected = results.Single(result => string.Equals(
                result.FilePath,
                higherFile,
                StringComparison.Ordinal));
            var rejection = rejected.Install;
            Assert(installed.Install!.Manifest!.Version == "1.0.0" && installed.Consumed,
                "the valid lower candidate should install when the newer assembly fails metadata preflight");
            Assert(!rejected.Superseded && rejection != null && !rejection.Ok,
                "the corrupt higher candidate should be reported as rejected rather than superseded");
            Assert(rejection!.Errors.Any(error =>
                    error.Contains("PE", StringComparison.OrdinalIgnoreCase) ||
                    error.Contains("managed", StringComparison.OrdinalIgnoreCase)),
                "the corrupt candidate should retain its actionable managed-assembly validation error");
            Assert(!rejected.Consumed && File.Exists(higherFile),
                "the rejected corrupt package should remain in the inbox for inspection");
            Assert(state.Find("fallback.integrity")?.Version == "1.0.0",
                "state should select the valid lower package");
        }

        private static void TestInboxEqualVersionUsesNormalizedPath(string root)
        {
            var paths = NewPaths(root, "inbox-path-tiebreak");
            var state = new ManagerState();
            var selectedFile = Path.Combine(paths.PackageInbox, "a-equal.topiaforgemod");
            var supersededFile = Path.Combine(paths.PackageInbox, "Z-equal.topiaforgemod");
            CreatePackage(selectedFile, "fallback.tie", "Tie", "1.0.0", "Tie.dll", "Tie.Entry");
            CreatePackage(supersededFile, "fallback.tie", "Tie", "1.0.0", "Tie.dll", "Tie.Entry");

            var results = new PackageInstaller().InstallInbox(paths, state, restartRequired: false);

            var selected = results.Single(result => result.Install?.Ok == true);
            var superseded = results.Single(result => result.Superseded);
            Assert(string.Equals(selected.FilePath, selectedFile, StringComparison.Ordinal),
                "equal SemVer candidates should use normalized path order as the deterministic tiebreaker");
            Assert(string.Equals(superseded.FilePath, supersededFile, StringComparison.Ordinal)
                && superseded.Consumed,
                "the path-later equal-version candidate should be consumed as superseded");
        }

        private static void TestInboxChangedAfterPreflightIsRetained(string root)
        {
            var paths = NewPaths(root, "inbox-preflight-race");
            var package = Path.Combine(paths.PackageInbox, "race.topiaforgemod");
            CreatePackage(package, "race.mod", "Race", "1.0.0", "Race.dll", "Race.Entry");
            var installer = new PackageInstaller
            {
                BeforeInboxInstallForTesting = selected =>
                {
                    File.Delete(selected);
                    CreatePackage(selected, "race.mod", "Race replacement", "2.0.0", "Race.dll", "Race.Entry");
                }
            };

            var results = installer.InstallInbox(paths, new ManagerState(), restartRequired: false);

            Assert(results.Count == 1 && results[0].Install != null && !results[0].Install!.Ok,
                "bytes replaced after inbox preflight must fail the install");
            Assert(results[0].Install!.Errors.Any(error => error.Contains("changed", StringComparison.OrdinalIgnoreCase)),
                "a changed inbox candidate should produce an actionable integrity error");
            Assert(!results[0].Consumed && File.Exists(package),
                "a replacement at the selected inbox path must be retained rather than deleted");
            Assert(!Directory.Exists(paths.GetPackagePath("race.mod", "1.0.0")) &&
                   !Directory.Exists(paths.GetPackagePath("race.mod", "2.0.0")),
                "neither preflighted nor replacement bytes may reach the package store");
        }

        private static void TestInboxChangedSupersededCandidateIsRetained(string root)
        {
            var paths = NewPaths(root, "inbox-superseded-race");
            var lower = Path.Combine(paths.PackageInbox, "race-1.topiaforgemod");
            var higher = Path.Combine(paths.PackageInbox, "race-2.topiaforgemod");
            CreatePackage(lower, "race.superseded", "Race", "1.0.0", "Race.dll", "Race.Entry");
            CreatePackage(higher, "race.superseded", "Race", "2.0.0", "Race.dll", "Race.Entry");
            var installer = new PackageInstaller
            {
                BeforeInboxInstallForTesting = _ => File.AppendAllText(lower, "replacement")
            };

            var results = installer.InstallInbox(paths, new ManagerState(), restartRequired: false);
            var winner = results.Single(result => !result.Superseded);
            var superseded = results.Single(result => result.Superseded);

            Assert(winner.Install?.Ok == true && winner.Consumed,
                "the unchanged selected candidate should still install and be consumed");
            Assert(!superseded.Consumed && File.Exists(lower) &&
                   superseded.ConsumeError?.Contains("changed", StringComparison.OrdinalIgnoreCase) == true,
                "changed bytes at a superseded path must be retained rather than deleting a replacement");
        }

        private static void TestInboxEnumerationLimitsFailClosed(string root)
        {
            var entryPaths = NewPaths(root, "inbox-entry-limit");
            for (var index = 0; index <= 1024; index++)
            {
                File.WriteAllText(Path.Combine(entryPaths.PackageInbox, "entry-" + index + ".txt"), string.Empty);
            }

            var entryResults = new PackageInstaller().InstallInbox(
                entryPaths,
                new ManagerState(),
                restartRequired: false);
            Assert(entryResults.Count == 1 && entryResults[0].Install?.Ok == false &&
                   entryResults[0].Install!.Errors.Any(error => error.Contains("1024 entry limit")),
                "an oversized inbox must fail closed before candidate processing");

            var packagePaths = NewPaths(root, "inbox-package-limit");
            for (var index = 0; index <= 256; index++)
            {
                File.WriteAllText(
                    Path.Combine(packagePaths.PackageInbox, "candidate-" + index + ".topiaforgemod"),
                    string.Empty);
            }

            var packageResults = new PackageInstaller().InstallInbox(
                packagePaths,
                new ManagerState(),
                restartRequired: false);
            Assert(packageResults.Count == 1 && packageResults[0].Install?.Ok == false &&
                   packageResults[0].Install!.Errors.Any(error => error.Contains("256 package limit")),
                "too many package candidates must fail closed before archive preflight");
        }

        private static void TestInboxRejectsNonRegularCandidate(string root)
        {
            var paths = NewPaths(root, "inbox-non-regular");
            var directoryCandidate = Path.Combine(paths.PackageInbox, "directory.topiaforgemod");
            Directory.CreateDirectory(directoryCandidate);

            var results = new PackageInstaller().InstallInbox(paths, new ManagerState(), restartRequired: false);

            Assert(results.Count == 1 && results[0].Install?.Ok == false,
                "a package-named directory should be reported as an invalid inbox candidate");
            Assert(results[0].Install!.Errors.Any(error => error.Contains("special file", StringComparison.Ordinal)) &&
                   !results[0].Consumed && Directory.Exists(directoryCandidate),
                "non-regular candidates must remain untouched for inspection");
        }

        private static void TestInboxFailureLeavesFile(string root)
        {
            var paths = NewPaths(root, "inbox-failure");
            var badFile = Path.Combine(paths.PackageInbox, "broken.topiaforgemod");
            using (var zip = ZipFile.Open(badFile, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "Something.dll", "not a dll");
            }

            var results = new PackageInstaller().InstallInbox(paths, new ManagerState(), restartRequired: false);

            Assert(results.Count == 1, "failing inbox package should be reported");
            Assert(!results[0].Install!.Ok, "install should fail without a manifest");
            Assert(!results[0].Consumed && File.Exists(badFile), "failed inbox file should be left for inspection");
        }

    }
}
