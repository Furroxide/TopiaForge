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
        private static void TestInstallSuccess(string root)
        {
            var paths = NewPaths(root, "success");
            Assert(string.Equals(Path.GetFileName(paths.Root), "TopiaForge", StringComparison.Ordinal),
                "manager storage root must use the TopiaForge brand");
            Assert(string.Equals(
                    Path.GetFileName(paths.GetConfigPath("io.github.furroxide.topiaforge.ugc.livesync")),
                    "topiaforge.ugc.livesync.json",
                    StringComparison.Ordinal),
                "first-party config files must use the short TopiaForge carrier name");
            var state = new ManagerState();
            var package = Path.Combine(root, "ok.topiaforgemod");
            CreatePackage(package, "alpha.mod", "Alpha", "1.0.0", "Alpha.dll", "Alpha.Entry");

            var result = new PackageInstaller().Install(package, paths, state, restartRequired: false);
            Assert(result.Ok, "valid package should install");
            Assert(result.Manifest!.Id == "alpha.mod", "manifest name should map to mod id");
            Assert(result.Manifest.Name == "Alpha", "manifest displayName should map to display name");
            Assert(File.Exists(Path.Combine(paths.GetPackagePath("alpha.mod", "1.0.0"), "topiaforge.mod.json")), "manifest should be installed");
            Assert(state.Find("alpha.mod")?.Enabled == true, "installed mod should be enabled");
        }

        private static void TestUpdatePreservesDisabledState(string root)
        {
            var paths = NewPaths(root, "update");
            var state = new ManagerState();
            var firstPackage = Path.Combine(root, "alpha-1.0.0.topiaforgemod");
            var secondPackage = Path.Combine(root, "alpha-1.1.0.topiaforgemod");
            var installer = new PackageInstaller();
            CreatePackage(firstPackage, "alpha.mod", "Alpha", "1.0.0", "Alpha.dll", "Alpha.Entry");
            CreatePackage(secondPackage, "alpha.mod", "Alpha", "1.1.0", "Alpha.dll", "Alpha.Entry");

            Assert(installer.Install(firstPackage, paths, state, restartRequired: false).Ok, "initial package should install");
            var installed = state.Find("alpha.mod");
            Assert(installed != null, "installed state should exist");
            installed!.Enabled = false;

            var update = installer.Install(secondPackage, paths, state, restartRequired: true);

            Assert(update.Ok, "update package should install");
            Assert(File.Exists(Path.Combine(paths.GetPackagePath("alpha.mod", "1.1.0"), "topiaforge.mod.json")), "updated manifest should be installed");
            Assert(state.Find("alpha.mod")?.Version == "1.1.0", "updated version should be selected");
            Assert(state.Find("alpha.mod")?.Enabled == false, "disabled mod should stay disabled after update");
            Assert(state.Find("alpha.mod")?.RestartRequired == true, "update should mark restart required");
        }

        private static void TestDevToolInstallsDisabledAndUpdatePreservesState(string root)
        {
            var paths = NewPaths(root, "devtool-default");
            var state = new ManagerState();
            var firstPackage = Path.Combine(root, "creator-tools-1.0.0.topiaforgemod");
            var secondPackage = Path.Combine(root, "creator-tools-1.1.0.topiaforgemod");
            var installer = new PackageInstaller();
            CreatePackage(
                firstPackage,
                "creator.tools",
                "Creator Tools",
                "1.0.0",
                "CreatorTools.dll",
                "CreatorTools.Entry",
                category: "DevTool");
            CreatePackage(
                secondPackage,
                "creator.tools",
                "Creator Tools",
                "1.1.0",
                "CreatorTools.dll",
                "CreatorTools.Entry",
                category: "DevTool");

            Assert(installer.Install(firstPackage, paths, state, restartRequired: false).Ok,
                "DevTool package should install");
            Assert(state.Find("creator.tools")?.Enabled == false,
                "new DevTool packages must be disabled by default");

            state.Find("creator.tools")!.Enabled = true;
            Assert(installer.Install(secondPackage, paths, state, restartRequired: true).Ok,
                "DevTool update should install");
            Assert(state.Find("creator.tools")?.Enabled == true,
                "DevTool updates must preserve an explicit enabled state");
        }

        private static void TestLegacyPackageExtensionRejected(string root)
        {
            var paths = NewPaths(root, "legacy-extension");
            var package = Path.Combine(root, "legacy.zip");
            CreatePackage(package, "legacy.mod", "Legacy", "1.0.0", "Legacy.dll", "Legacy.Entry");

            var result = new PackageInstaller().Install(
                package,
                paths,
                new ManagerState(),
                restartRequired: false);

            Assert(!result.Ok && result.Errors.Any(error => error.Contains(".topiaforgemod", StringComparison.Ordinal)),
                "non-TopiaForge package extensions must be rejected without compatibility fallback");
        }

        private static void TestAppliedRestartRequirementsClear()
        {
            var state = new ManagerState();
            var appliedManifest = new ModManifest
            {
                SchemaVersion = 5,
                Id = "applied.mod",
                Name = "Applied",
                Version = "1.0.0",
                EntryAssembly = "Applied.dll",
                EntryType = "Applied.Entry"
            };
            var pendingManifest = new ModManifest
            {
                SchemaVersion = 5,
                Id = "pending.mod",
                Name = "Pending",
                Version = "1.0.0",
                EntryAssembly = "Pending.dll",
                EntryType = "Pending.Entry"
            };

            state.Upsert(appliedManifest, enabled: true, restartRequired: true);
            var pending = state.Upsert(pendingManifest, enabled: false, restartRequired: true);
            pending.UninstallPending = true;

            state.ClearAppliedRestartRequirements();

            Assert(state.Find("applied.mod")?.RestartRequired == false, "applied restart flag should clear");
            Assert(state.Find("pending.mod")?.RestartRequired == true, "uninstall pending restart flag should remain");
        }

        private static void TestMissingManifestRejected(string root)
        {
            var paths = NewPaths(root, "missing");
            var package = Path.Combine(root, "missing.topiaforgemod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("Something.dll");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("not a dll");
                }
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(e => e.Contains("topiaforge.mod.json")), "missing manifest should be rejected");
        }

        private static void TestZipTraversalRejected(string root)
        {
            var paths = NewPaths(root, "traversal");
            var package = Path.Combine(root, "traversal.topiaforgemod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                zip.CreateEntry("../escape.txt");
                WriteEntry(zip, "topiaforge.mod.json", JsonUtil.Serialize(new ModManifest
                {
                    SchemaVersion = 5,
                    Id = "bad.mod",
                    Name = "Bad",
                    Version = "1.0.0",
                    EntryAssembly = "Bad.dll",
                    EntryType = "Bad.Entry"
                }));
                WriteEntry(zip, "Bad.dll", "not a dll");
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(e => e.Contains("non-portable")), "zip traversal should be rejected");
        }

        private static void TestCaseChangedZipTraversalRejected(string root)
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                return; // Case-insensitive containment is correct on Windows.
            }

            var testRoot = Path.Combine(root, "case-changed-traversal");
            var destination = Path.Combine(testRoot, "case-root");
            var escapedPath = Path.Combine(testRoot, "CASE-ROOT", "escape.txt");
            var package = Path.Combine(root, "case-changed-traversal.topiaforgemod");
            Directory.CreateDirectory(destination);
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "../CASE-ROOT/escape.txt", "escaped");
            }

            var extraction = typeof(PackageInstaller).GetMethod(
                "ExtractToSafeDirectory",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert(extraction != null, "package extraction helper should exist");
            var rejected = false;
            try
            {
                extraction!.Invoke(null, new object[] { package, destination });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
            {
                rejected = true;
            }

            Assert(rejected, "case-changed sibling traversal should be rejected on case-sensitive platforms");
            Assert(!File.Exists(escapedPath), "case-changed traversal must not write outside the destination");
        }

        private static void TestArchiveManifestLimitRejected(string root)
        {
            var paths = NewPaths(root, "manifest-limit");
            var package = Path.Combine(root, "manifest-limit.topiaforgemod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "topiaforge.mod.json", new string(' ', (1024 * 1024) + 1));
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(error => error.Contains("topiaforge.mod.json") && error.Contains("limit")),
                "an oversized packed manifest should be rejected before loading it into memory");
        }

        private static void TestDuplicateArchivePathRejected(string root)
        {
            var paths = NewPaths(root, "duplicate-archive-path");
            var package = Path.Combine(root, "duplicate-archive-path.topiaforgemod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var manifest = JsonUtil.Serialize(TestManifest("duplicate.archive"));
                WriteEntry(zip, "topiaforge.mod.json", manifest);
                WriteEntry(zip, "TOPIAFORGE.MOD.JSON", manifest);
                WriteEntry(zip, "duplicate.archive.dll", "not a dll");
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(error => error.Contains("duplicate path")),
                "case-variant duplicate archive paths should be rejected consistently across platforms");
        }

        private static void TestUnicodeArchivePathPolicy(string root)
        {
            var collisions = new[]
            {
                ("assets/ligature-ff.txt", "assets/ligature-\uFB00.txt"),
                ("assets/fullwidth-A.txt", "assets/fullwidth-\uFF21.txt"),
                ("assets/sigma-\u03A3.txt", "assets/sigma-\u03C2.txt"),
                ("assets/strasse.txt", "assets/stra\u00DFe.txt")
            };
            for (var index = 0; index < collisions.Length; index++)
            {
                var paths = NewPaths(root, "unicode-collision-" + index);
                var package = Path.Combine(root, "unicode-collision-" + index + ".topiaforgemod");
                using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
                {
                    WriteEntry(zip, collisions[index].Item1, "first");
                    WriteEntry(zip, collisions[index].Item2, "second");
                }

                var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
                Assert(!result.Ok && result.Errors.Any(error => error.Contains("portable collision")),
                    "NFKC/invariant-case archive aliases must collide consistently across platforms");
            }

            var nonCanonicalPaths = NewPaths(root, "unicode-noncanonical");
            var nonCanonicalPackage = Path.Combine(root, "unicode-noncanonical.topiaforgemod");
            using (var zip = ZipFile.Open(nonCanonicalPackage, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "assets/cafe\u0301.txt", "decomposed");
            }

            var nonCanonical = new PackageInstaller().Install(
                nonCanonicalPackage,
                nonCanonicalPaths,
                new ManagerState(),
                restartRequired: false);
            Assert(!nonCanonical.Ok && nonCanonical.Errors.Any(error => error.Contains("Unicode NFC")),
                "archive paths must use canonical Unicode NFC so manifest references remain stable");
        }

        private static void TestArchivePathCollisionRejected(string root)
        {
            foreach (var childFirst in new[] { false, true })
            {
                var suffix = childFirst ? "child-first" : "file-first";
                var paths = NewPaths(root, "archive-collision-" + suffix);
                var package = Path.Combine(root, "archive-collision-" + suffix + ".topiaforgemod");
                using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
                {
                    if (childFirst)
                    {
                        WriteEntry(zip, "collision/child.txt", "child");
                        WriteEntry(zip, "collision", "file");
                    }
                    else
                    {
                        WriteEntry(zip, "collision", "file");
                        WriteEntry(zip, "collision/child.txt", "child");
                    }
                }

                var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
                Assert(!result.Ok && result.Errors.Any(error => error.Contains("file")),
                    "file/directory archive collisions should be rejected regardless of entry order");
            }
        }

        private static void TestArchiveLinkRejected(string root)
        {
            var paths = NewPaths(root, "archive-link");
            var package = Path.Combine(root, "archive-link.topiaforgemod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var link = zip.CreateEntry("linked-file");
                link.ExternalAttributes = unchecked((int)((0xA000u | 0x1FFu) << 16));
                using (var writer = new StreamWriter(link.Open()))
                {
                    writer.Write("../outside");
                }
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(error => error.Contains("symbolic link")),
                "symbolic-link archive entries should be rejected before extraction");
        }

        private static void TestArchiveEntryCountRejected(string root)
        {
            var paths = NewPaths(root, "archive-entry-count");
            var package = Path.Combine(root, "archive-entry-count.topiaforgemod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                for (var index = 0; index < 8193; index++)
                {
                    zip.CreateEntry("entries/" + index + ".txt");
                }
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(error => error.Contains("too many archive entries")),
                "archive entry counts should be capped before extraction");
        }

        private static void TestNonPortableArchivePathsRejected(string root)
        {
            var unsafePaths = new[]
            {
                "C:drive-relative.dll",
                "payload.dll:stream",
                "NUL.txt",
                "folder/trailing. /value.dll",
                "folder/./value.dll",
                "folder//value.dll"
            };
            for (var index = 0; index < unsafePaths.Length; index++)
            {
                var paths = NewPaths(root, "non-portable-path-" + index);
                var package = Path.Combine(root, "non-portable-path-" + index + ".topiaforgemod");
                using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
                {
                    WriteEntry(zip, unsafePaths[index], "bad");
                }

                var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
                Assert(!result.Ok && result.Errors.Any(error => error.Contains("non-portable")),
                    "unsafe portable archive path should be rejected: " + unsafePaths[index]);
            }
        }

        private static void TestReplacementRollbackPreservesInstalledPackage(string root)
        {
            var testRoot = Path.Combine(root, "replacement-rollback");
            var stagingRoot = Path.Combine(testRoot, "staging");
            var target = Path.Combine(testRoot, "packages", "rollback.mod", "1.0.0");
            var missingStaging = Path.Combine(stagingRoot, "missing-staging");
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(target);
            var marker = Path.Combine(target, "previous-package.txt");
            File.WriteAllText(marker, "previous");

            var commit = typeof(PackageInstaller).GetMethod(
                "CommitStagedDirectory",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert(commit != null, "package commit helper should exist");
            var failed = false;
            try
            {
                commit!.Invoke(null, new object[] { missingStaging, target, stagingRoot });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException || ex.InnerException is DirectoryNotFoundException)
            {
                failed = true;
            }

            Assert(failed, "a missing staged package should make the replacement commit fail");
            Assert(File.ReadAllText(marker) == "previous",
                "a failed replacement commit must restore the previously installed package");
            Assert(!Directory.GetDirectories(stagingRoot, "rollback-*", SearchOption.TopDirectoryOnly).Any(),
                "a successful rollback should not leave the previous package stranded in staging");
        }

        private static void TestSchemaV1Rejected(string root)
        {
            var paths = NewPaths(root, "schema-v1");
            var package = Path.Combine(root, "schema-v1.topiaforgemod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "topiaforge.mod.json", JsonUtil.Serialize(new ModManifest
                {
                    SchemaVersion = 1,
                    Id = "old.mod",
                    Name = "Old",
                    Author = new ModAuthor { Name = "TopiaForge" },
                    Version = "1.0.0",
                    EntryAssembly = "Old.dll",
                    EntryType = "Old.Entry"
                }));
                WriteEntry(zip, "Old.dll", "not a dll");
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(e => e.Contains("schemaVersion 5 is required")),
                "schema v1 should be rejected without being reinterpreted");
        }

        private static void TestInstallPreservesOtherVersions(string root)
        {
            var paths = NewPaths(root, "prune-install");
            var state = new ManagerState();
            var installer = new PackageInstaller();
            var firstPackage = Path.Combine(root, "prune-1.0.0.topiaforgemod");
            var secondPackage = Path.Combine(root, "prune-1.1.0.topiaforgemod");
            CreatePackage(firstPackage, "prune.mod", "Prune", "1.0.0", "Prune.dll", "Prune.Entry");
            CreatePackage(secondPackage, "prune.mod", "Prune", "1.1.0", "Prune.dll", "Prune.Entry");

            Assert(installer.Install(firstPackage, paths, state, restartRequired: false).Ok, "1.0.0 should install");
            Assert(installer.Install(secondPackage, paths, state, restartRequired: false).Ok, "1.1.0 should install");

            Assert(Directory.Exists(paths.GetPackagePath("prune.mod", "1.0.0")),
                "installing a new version should preserve the previous version for profile selection");
            Assert(Directory.Exists(paths.GetPackagePath("prune.mod", "1.1.0")), "installed 1.1.0 should remain");
        }

        private static void TestRetiredManifestAliasesRejected(string root)
        {
            var paths = NewPaths(root, "retired-manifest-aliases");
            var package = Path.Combine(root, "retired-manifest-aliases.topiaforgemod");
            const string manifest = "{\"schemaVersion\":5,\"name\":\"alias.mod\"," +
                "\"displayName\":\"Alias\",\"version\":\"1.0.0\"," +
                "\"author\":{\"name\":\"TopiaForge\"},\"entryAssembly\":\"Alias.dll\"," +
                "\"entryType\":\"Alias.Entry\",\"gameVersion\":\"2309\"," +
                "\"supportedGameVersionRange\":\"*\",\"supportedLoaderVersionRange\":\"*\"," +
                "\"supportedSdkVersionRange\":\"*\",\"vpmDependencies\":{},\"permissions\":[]}";
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "topiaforge.mod.json", manifest);
                WriteEntry(zip, "Alias.dll", "not a dll");
            }

            var result = new PackageInstaller().Install(
                package,
                paths,
                new ManagerState(),
                restartRequired: false);
            Assert(
                !result.Ok &&
                result.Errors.Any(error => error.Contains("gameVersion is not supported")) &&
                result.Errors.Any(error => error.Contains("vpmDependencies is not supported")) &&
                result.Errors.Any(error => error.Contains("permissions is not supported")),
                "retired manifest aliases must be rejected explicitly");
        }

    }
}
