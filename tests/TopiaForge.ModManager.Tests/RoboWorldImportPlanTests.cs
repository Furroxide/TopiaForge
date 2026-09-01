using System;
using System.Collections.Generic;
using System.IO;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Pins the folder confinement that stands between "load a local export" and "make the game read an
    /// arbitrary file". The import host takes a bare path, so these rules are the whole boundary.
    /// </summary>
    internal static class RoboWorldImportPlanTests
    {
        private static readonly string Folder =
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "topiaforge-roboworld-tests"));

        public static void Run()
        {
            TestAcceptsEveryDocumentedExtension();
            TestAcceptsAFileNameRelativeToTheFolder();
            TestRejectsPathsOutsideTheFolder();
            TestRejectsASiblingFolderWithTheSamePrefix();
            TestRejectsUnsupportedExtensions();
            TestRejectsMissingFilesAndEmptyInput();
            TestFolderNormalization();
            TestRootFolderStaysAbsolute();
        }

        private static void TestRootFolderStaysAbsolute()
        {
            // A root is a legal folder, and trimming its separator used to leave "" on Unix and a bare
            // "C:" on Windows. Path.Combine treats neither as absolute, so a bare file name resolved
            // against the process working directory instead of the configured folder.
            var root = Path.GetPathRoot(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "probe")));
            Assert(!string.IsNullOrEmpty(root), "the temp path should have a resolvable root");

            var normalized = RoboWorldImportPlan.TryNormalizeFolder(root);
            Assert(normalized != null, "a root folder should normalize rather than be refused");
            Assert(
                Path.IsPathRooted(Path.Combine(normalized!, "town.roboworld")),
                "combining against a normalized root must stay absolute");

            var expected = Path.GetFullPath(Path.Combine(root!, "town.roboworld"));
            Assert(
                RoboWorldImportPlan.TryPlan(root, "town.roboworld", Exists(expected), out var plan, out var error),
                "a bare file name in a root folder should resolve: " + error);
            Assert(
                plan!.FilePath == expected,
                "a root-folder request must resolve inside the root, not the working directory");
        }

        private static void TestAcceptsEveryDocumentedExtension()
        {
            // The game scans for .roboworld, .json, and .json.gz. A GetExtension-based check would read
            // ".json.gz" as ".gz" and refuse the compressed exports the Creator actually hands out.
            foreach (var name in new[] { "town.roboworld", "town.json", "town.json.gz", "TOWN.RoboWorld" })
            {
                var path = Path.Combine(Folder, name);
                Assert(
                    RoboWorldImportPlan.TryPlan(Folder, path, Exists(path), out var plan, out var error),
                    "'" + name + "' should be an importable export, refused with: " + error);
                Assert(plan!.FileName == name, "the plan should carry the export's file name");
                Assert(plan.FolderPath == Folder, "the plan should carry the normalized folder");
            }
        }

        private static void TestAcceptsAFileNameRelativeToTheFolder()
        {
            var absolute = Path.Combine(Folder, "town.roboworld");
            Assert(
                RoboWorldImportPlan.TryPlan(Folder, "town.roboworld", Exists(absolute), out var plan, out _),
                "a bare file name should resolve against the configured folder");
            Assert(plan!.FilePath == absolute, "a relative request should be resolved to an absolute path");
        }

        private static void TestRejectsPathsOutsideTheFolder()
        {
            foreach (var request in new[]
                     {
                         Path.Combine(Path.GetTempPath(), "elsewhere.roboworld"),
                         Path.Combine("..", "elsewhere.roboworld"),
                         Path.Combine("nested", "..", "..", "elsewhere.roboworld"),
                     })
            {
                Assert(
                    !RoboWorldImportPlan.TryPlan(Folder, request, _ => true, out var plan, out var error),
                    "'" + request + "' escapes the import folder and must be refused");
                Assert(plan == null, "a refused request must not produce a plan");
                Assert(error.Contains(Folder), "the refusal should name the folder a world must live in");
            }
        }

        private static void TestRejectsASiblingFolderWithTheSamePrefix()
        {
            // A plain StartsWith on the folder would accept "…\worlds-backup\x.roboworld" as a child of
            // "…\worlds". The separator-terminated prefix is what stops it.
            var sibling = Folder + "-backup" + Path.DirectorySeparatorChar + "town.roboworld";
            Assert(
                !RoboWorldImportPlan.TryPlan(Folder, sibling, _ => true, out _, out _),
                "a sibling folder sharing the configured folder's prefix must not pass as a child");
        }

        private static void TestRejectsUnsupportedExtensions()
        {
            foreach (var name in new[] { "town.gz", "town.txt", "town.roboworld.bak", "town" })
            {
                var path = Path.Combine(Folder, name);
                Assert(
                    !RoboWorldImportPlan.TryPlan(Folder, path, _ => true, out _, out var error),
                    "'" + name + "' is not an export the game imports and must be refused");
                Assert(error.Length > 0, "a refusal must carry a reason");
            }
        }

        private static void TestRejectsMissingFilesAndEmptyInput()
        {
            var path = Path.Combine(Folder, "town.roboworld");
            Assert(
                !RoboWorldImportPlan.TryPlan(Folder, path, _ => false, out _, out var missing),
                "an export that is not on disk must be refused");
            Assert(missing.Contains("town.roboworld"), "the refusal should name the missing export");

            Assert(
                !RoboWorldImportPlan.TryPlan(Folder, "   ", _ => true, out _, out _),
                "a blank request must be refused");
            Assert(
                !RoboWorldImportPlan.TryPlan("   ", path, _ => true, out _, out _),
                "an unconfigured folder must be refused");
        }

        private static void TestFolderNormalization()
        {
            Assert(RoboWorldImportPlan.TryNormalizeFolder(null) == null, "a null folder is not usable");
            Assert(RoboWorldImportPlan.TryNormalizeFolder("  ") == null, "a blank folder is not usable");

            var trailing = Folder + Path.DirectorySeparatorChar;
            Assert(
                RoboWorldImportPlan.TryNormalizeFolder(trailing) == Folder,
                "a trailing separator should normalize away so containment compares consistently");

            Assert(
                RoboWorldImportPlan.HasSupportedExtension("a.json.gz")
                && !RoboWorldImportPlan.HasSupportedExtension("a.gz")
                && !RoboWorldImportPlan.HasSupportedExtension(null),
                "the supported-extension check should recognize the compound .json.gz suffix only");

            var extensions = new List<string>(RoboWorldImportPlan.SupportedExtensions);
            Assert(
                extensions.Contains(".roboworld") && extensions.Contains(".json") && extensions.Contains(".json.gz"),
                "the supported set must match the three extensions build 2409 scans for");
        }

        private static Func<string, bool> Exists(string expected) =>
            candidate => string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase);

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
