using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class InstalledVersionCoexistenceTests
    {
        private const string ModId = "coexistence.mod";
        private const string FixtureAssembly = "TopiaForge.ValidTestMod.dll";
        private const string FixtureType = "TopiaForge.ValidTestMod.ValidMod";

        public static void Run(string root)
        {
            var paths = new ManagerPaths(Path.Combine(root, "installed-version-coexistence", "BepInEx"));
            paths.EnsureCreated();
            var state = InstallVersions(root, paths);

            TestVersionsSurviveSuccessiveStartupScans(paths, state);
            TestExactPinsCanSwitchWithoutDeletingAlternatives(paths, state);
            TestMissingExactPinFailsClosedWithoutDeletingAlternatives(paths, state);
            TestUnpinnedSelectionUsesHighestValidVersion(paths, state);
            TestInvalidDuplicateCandidateCannotHijackSelection(paths, state);
        }

        private static ManagerState InstallVersions(string root, ManagerPaths paths)
        {
            var state = new ManagerState();
            var installer = new PackageInstaller();
            foreach (var version in new[] { "1.0.0", "2.0.0" })
            {
                var archive = Path.Combine(root, ModId + "-" + version + ".topiaforgemod");
                WritePackageArchive(archive, version);
                var result = installer.Install(archive, paths, state, restartRequired: false);
                Assert(result.Ok, "version " + version + " should install: " + string.Join("; ", result.Errors));
            }

            AssertBothVersionsExist(paths, "installing a second version must preserve the first version");
            return state;
        }

        private static void TestVersionsSurviveSuccessiveStartupScans(ManagerPaths paths, ManagerState state)
        {
            var statePath = Path.Combine(paths.Root, "coexistence-state.json");
            JsonUtil.SaveFile(statePath, state);
            var firstStartupState = JsonUtil.LoadPersistentFile(statePath, new ManagerState());
            var firstSelection = new ModRegistry().Scan(paths, firstStartupState).Single();
            Assert(firstSelection.IsValid && firstSelection.Manifest!.Version == "2.0.0",
                "normal startup should select the highest valid installed version");
            AssertBothVersionsExist(paths, "the first startup scan must not prune alternatives");

            JsonUtil.SaveFile(statePath, firstStartupState);
            var secondStartupState = JsonUtil.LoadPersistentFile(statePath, new ManagerState());
            var secondSelection = new ModRegistry().Scan(paths, secondStartupState).Single();
            Assert(secondSelection.IsValid && secondSelection.Manifest!.Version == "2.0.0",
                "selection should remain authoritative and deterministic on the next startup");
            AssertBothVersionsExist(paths, "successive startup scans must preserve every valid installed version");
        }

        private static void TestExactPinsCanSwitchWithoutDeletingAlternatives(ManagerPaths paths, ManagerState state)
        {
            var selectedState = state.Find(ModId)!;
            selectedState.VersionPinned = true;
            selectedState.Version = "1.0.0";
            var first = new ModRegistry().Scan(paths, state).Single();
            Assert(first.IsValid && first.Manifest!.Version == "1.0.0",
                "an exact profile pin should select the retained older version");
            Assert(first.SelectionReason.Contains("exact profile pin", StringComparison.Ordinal),
                "an exact pin should be recorded in selection diagnostics");

            selectedState.Version = "2.0.0";
            var second = new ModRegistry().Scan(paths, state).Single();
            Assert(second.IsValid && second.Manifest!.Version == "2.0.0",
                "switching the exact profile pin should select the other retained version");
            AssertBothVersionsExist(paths, "switching exact pins must not delete either installed version");
        }

        private static void TestMissingExactPinFailsClosedWithoutDeletingAlternatives(
            ManagerPaths paths,
            ManagerState state)
        {
            var selectedState = state.Find(ModId)!;
            selectedState.VersionPinned = true;
            selectedState.Version = "9.9.9";

            var missing = new ModRegistry().Scan(paths, state).Single();

            Assert(!missing.IsValid &&
                   missing.Errors.Any(error => error.Contains("not installed; refusing to fall back", StringComparison.Ordinal)),
                "a missing exact pin should fail closed instead of selecting an available version");
            AssertBothVersionsExist(paths, "a failed exact pin must not delete any installed alternative");
        }

        private static void TestUnpinnedSelectionUsesHighestValidVersion(ManagerPaths paths, ManagerState state)
        {
            var selectedState = state.Find(ModId)!;
            selectedState.VersionPinned = false;
            selectedState.Version = "1.0.0";

            var packages = new ModRegistry().Scan(paths, state);

            Assert(packages.Count == 1,
                "multiple installed versions must materialize as one authoritative package candidate");
            Assert(packages[0].IsValid && packages[0].Manifest!.Version == "2.0.0",
                "an unpinned selection should use the highest valid Semantic Version");
            Assert(selectedState.Version == "2.0.0",
                "durable unpinned state should reconcile to the authoritative version");
            AssertBothVersionsExist(paths, "unpinned reconciliation must not delete the lower version");
        }

        private static void TestInvalidDuplicateCandidateCannotHijackSelection(
            ManagerPaths paths,
            ManagerState state)
        {
            var invalidPath = paths.GetPackagePath(ModId, "99.0.0");
            Directory.CreateDirectory(invalidPath);
            JsonUtil.SaveFile(Path.Combine(invalidPath, "topiaforge.mod.json"), Manifest("99.0.0"));
            File.WriteAllText(Path.Combine(invalidPath, FixtureAssembly), "not a managed PE image");
            var selectedState = state.Find(ModId)!;
            selectedState.VersionPinned = false;

            var packages = new ModRegistry().Scan(paths, state);

            Assert(packages.Count == 1 && packages[0].IsValid && packages[0].Manifest!.Version == "2.0.0",
                "an invalid higher duplicate candidate must not hijack authoritative selection");
            AssertBothVersionsExist(paths, "an invalid duplicate candidate must not cause valid versions to be deleted");
        }

        private static void WritePackageArchive(string archivePath, string version)
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }

            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "topiaforge.mod.json", JsonUtil.Serialize(Manifest(version)));
                var entry = archive.CreateEntry(FixtureAssembly);
                using (var output = entry.Open())
                using (var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, FixtureAssembly)))
                {
                    input.CopyTo(output);
                }
            }
        }

        private static ModManifest Manifest(string version)
        {
            return new ModManifest
            {
                SchemaVersion = ModManifest.CurrentSchemaVersion,
                Id = ModId,
                Name = "Coexistence",
                Author = new ModAuthor { Name = "TopiaForge" },
                Version = version,
                EntryAssembly = FixtureAssembly,
                EntryType = FixtureType,
                SupportedGameVersionRange = "*",
                SupportedLoaderVersionRange = ">=0.1.0-rc.1 <0.2.0",
                SupportedSdkVersionRange = ">=0.1.0-rc.1 <0.2.0"
            };
        }

        private static void WriteEntry(ZipArchive archive, string name, string value)
        {
            var entry = archive.CreateEntry(name);
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write(value);
            }
        }

        private static void AssertBothVersionsExist(ManagerPaths paths, string message)
        {
            Assert(Directory.Exists(paths.GetPackagePath(ModId, "1.0.0")) &&
                   Directory.Exists(paths.GetPackagePath(ModId, "2.0.0")), message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Installed version coexistence test failed: " + message);
            }
        }
    }
}
