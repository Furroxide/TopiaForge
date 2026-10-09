using System;
using System.IO;
using System.Linq;
using TopiaForge.GameCompat.Extractor;
using Extractor = TopiaForge.GameCompat.Extractor;

namespace TopiaForge.ModManager.Tests
{
    internal static class GameVersionLabelReaderTests
    {
        internal static void Run()
        {
            // Created atomically under an unpredictable name; a GetTempPath-derived root is untrusted input to
            // CodeQL, and these fixtures pass it on to the production launcher-state reader.
            var root = Directory.CreateTempSubdirectory("TopiaForgeGameVersionTests-").FullName;

            try
            {
                ReadsLauncherBuildBesideMacApp(root);
                FallsBackToMacBundleVersion(root);
                ReadsLauncherBuildFromWindowsInstall(root);
                ReadsRelocatedLauncherBuild(root);
                StopsAtRejectedMarker(root);
                FindsRelocatedManagedDirectoryLast(root);
                ReadsPublicManagedReferenceCacheBuild(root);
                IgnoresAmbiguousChangelog(root);
                RejectsOversizedMetadata(root);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        private static void ReadsLauncherBuildBesideMacApp(string root)
        {
            var launcher = Path.Combine(root, "mac-build");
            var managed = MacManagedDir(launcher);
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(launcher, "installed-build.json"), "{\"id\":2309}");
            WriteInfoPlist(launcher, "0.1", "0");

            Assert(GameVersionLabelReader.Read(managed) == "build 2309",
                "macOS capture should read installed-build.json beside Robotopia.app");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed) == "0.0.2309",
                "macOS capture should expose the canonical build SemVer");
        }

        private static void FallsBackToMacBundleVersion(string root)
        {
            var launcher = Path.Combine(root, "mac-plist");
            var managed = MacManagedDir(launcher);
            Directory.CreateDirectory(managed);
            WriteInfoPlist(launcher, "0.7.2", "918");

            Assert(GameVersionLabelReader.Read(managed) == "0.7.2 (build 918)",
                "macOS capture should use bundle metadata when launcher build metadata is absent");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed) == "0.0.918",
                "numeric CFBundleVersion should take precedence for compatibility");

            File.WriteAllText(Path.Combine(launcher, "installed-build.json"), "{not-json");
            Assert(GameVersionLabelReader.Read(managed) == "0.7.2 (build 918)",
                "malformed launcher metadata should remain non-fatal and fall back to Info.plist");
        }

        private static void ReadsLauncherBuildFromWindowsInstall(string root)
        {
            var launcher = Path.Combine(root, "windows-build");
            var install = Path.Combine(launcher, "Robotopia");
            var managed = Path.Combine(install, "Robotopia_Data", "Managed");
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(launcher, "installed-build.json"), "{\"id\":\"310\"}");

            Assert(GameVersionLabelReader.Read(managed) == "build 310",
                "Windows/Proton capture should read launcher metadata beside the install root");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed) == "0.0.310",
                "Windows/Proton capture should expose the canonical build SemVer");

            File.WriteAllText(Path.Combine(install, "installed-build.json"), "{\"id\":\"311\"}");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed) == "0.0.311",
                "Windows/Proton capture should prefer metadata inside the install root");
        }

        private static void ReadsRelocatedLauncherBuild(string root)
        {
            var fixture = TomatoCakeLauncherFixture.Create(root, "relocated-build");
            var managed = Path.Combine(fixture.RelocatedGameRoot, "Robotopia_Data", "Managed");
            Directory.CreateDirectory(managed);
            fixture.WriteStateMarker(2545);
            fixture.WriteStaleStateManifest();

            Assert(GameVersionLabelReader.Read(managed, fixture.State) == string.Empty,
                "capture should not attribute the launcher marker without launcher-config.json");
            fixture.WriteGameDirectory(fixture.RelocatedParent);
            Assert(GameVersionLabelReader.Read(managed, fixture.State) == "build 2545",
                "capture should read the launcher marker for a game the official launcher moved");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed, fixture.State) == "0.0.2545",
                "capture should expose the relocated game's canonical build SemVer");
            Assert(GameVersionLabelReader.Read(managed, null) == string.Empty,
                "capture should not consult the launcher without a state directory");

            File.WriteAllText(Path.Combine(fixture.RelocatedParent, "installed-build.json"), "{\"id\":2546}");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed, fixture.State) == "0.0.2546",
                "metadata beside the game should outrank the launcher marker");
            File.Delete(Path.Combine(fixture.RelocatedParent, "installed-build.json"));

            var otherManaged = Path.Combine(fixture.Root, "other", "Robotopia", "Robotopia_Data", "Managed");
            Directory.CreateDirectory(otherManaged);
            Assert(GameVersionLabelReader.Read(otherManaged, fixture.State) == string.Empty,
                "capture should not lend the launcher marker to an install outside game_dir");

            var value = TomatoCakeLauncherFixture.Json(fixture.RelocatedParent);
            fixture.WriteConfig("{\"game_dir\":" + value + ",\"game_dir\":" + value + "}");
            Assert(GameVersionLabelReader.Read(managed, fixture.State) == string.Empty,
                "a malformed launcher-config.json should lend no marker");
        }

        private static void StopsAtRejectedMarker(string root)
        {
            var fixture = TomatoCakeLauncherFixture.Create(root, "rejected-marker");
            var managed = Path.Combine(fixture.RelocatedGameRoot, "Robotopia_Data", "Managed");
            Directory.CreateDirectory(managed);
            fixture.WriteStateMarker(2545);
            fixture.WriteGameDirectory(fixture.RelocatedParent);
            Assert(GameVersionLabelReader.Read(managed, fixture.State) == "build 2545",
                "the launcher marker should apply while no higher-priority marker exists");

            var besideGame = Path.Combine(fixture.RelocatedParent, "installed-build.json");
            File.WriteAllText(besideGame, "{not-json");
            Assert(GameVersionLabelReader.Read(managed, fixture.State) == string.Empty,
                "a malformed marker beside the game should not fall through to the launcher marker");

            File.WriteAllText(besideGame, "{\"id\":2546}");
            File.WriteAllText(Path.Combine(fixture.RelocatedGameRoot, "installed-build.json"), "{\"id\":0}");
            Assert(GameVersionLabelReader.Read(managed, fixture.State) == string.Empty,
                "a rejected marker in the game root should not fall through to the marker beside it");
        }

        private static void FindsRelocatedManagedDirectoryLast(string root)
        {
            var fixture = TomatoCakeLauncherFixture.Create(root, "relocated-managed");
            var managed = Path.Combine(fixture.RelocatedGameRoot, "Robotopia_Data", "Managed");
            Directory.CreateDirectory(managed);
            var explicitManaged = Path.Combine(root, "explicit", "Managed");

            var withoutRecord = Extractor.Program.ManagedDirCandidates(explicitManaged, fixture.State).ToList();
            Assert(withoutRecord[0] == explicitManaged && !withoutRecord.Contains(managed),
                "without launcher-config.json the extractor should keep its existing candidates only");

            fixture.WriteGameDirectory(fixture.RelocatedParent);
            var withRecord = Extractor.Program.ManagedDirCandidates(explicitManaged, fixture.State).ToList();
            Assert(withRecord[0] == explicitManaged && withRecord[withRecord.Count - 1] == managed,
                "the relocated Managed directory should be the extractor's last candidate");
            Assert(withRecord.Take(withRecord.Count - 1).SequenceEqual(withoutRecord),
                "the relocation should leave every existing candidate in place and in order");

            fixture.WriteConfig("{\"game_dir\":7}");
            Assert(!Extractor.Program.ManagedDirCandidates(explicitManaged, fixture.State).Contains(managed),
                "a malformed launcher-config.json should add no Managed directory");
        }

        private static void IgnoresAmbiguousChangelog(string root)
        {
            var install = Path.Combine(root, "misleading-changelog");
            var managed = Path.Combine(install, "Robotopia_Data", "Managed");
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(install, "changelog.txt"), "4 commits since v5.4.23.4\n");

            Assert(GameVersionLabelReader.Read(managed) == string.Empty,
                "a bundled dependency changelog must not be reported as the Robotopia game version");
        }

        private static void ReadsPublicManagedReferenceCacheBuild(string root)
        {
            var hash = new string('a', 64);
            var managed = Path.Combine(
                root,
                "robotopia-managed-refs",
                "public-2309-mac-" + hash,
                "Managed");
            Directory.CreateDirectory(managed);

            Assert(GameVersionLabelReader.Read(managed) == "build 2309",
                "public managed-reference cache should preserve its pinned build label");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed) == "0.0.2309",
                "public managed-reference cache should preserve the canonical build SemVer");

            var ambiguous = Path.Combine(
                root,
                "robotopia-managed-refs",
                "public-2309-linux-" + hash,
                "Managed");
            Directory.CreateDirectory(ambiguous);
            Assert(GameVersionLabelReader.Read(ambiguous) == string.Empty,
                "unknown cache platforms must not be interpreted as trusted build provenance");
        }

        private static void RejectsOversizedMetadata(string root)
        {
            var launcher = Path.Combine(root, "oversized");
            var managed = MacManagedDir(launcher);
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(launcher, "installed-build.json"), new string(' ', 64 * 1024 + 1));
            WriteInfoPlist(launcher, "0.8", "0");

            Assert(GameVersionLabelReader.Read(managed) == "0.8",
                "oversized launcher metadata should be rejected with a bounded bundle-version fallback");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed) == string.Empty,
                "a non-SemVer bundle label without a positive build id should remain compatibility-unknown");
        }

        private static string MacManagedDir(string launcher) => Path.Combine(
            launcher,
            "Robotopia.app",
            "Contents",
            "Resources",
            "Data",
            "Managed");

        private static void WriteInfoPlist(string launcher, string shortVersion, string buildVersion)
        {
            var path = Path.Combine(launcher, "Robotopia.app", "Contents", "Info.plist");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
                + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" "
                + "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
                + "<plist version=\"1.0\"><dict>"
                + "<key>CFBundleShortVersionString</key><string>" + shortVersion + "</string>"
                + "<key>CFBundleVersion</key><string>" + buildVersion + "</string>"
                + "</dict></plist>");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Game version label: " + message);
            }
        }
    }
}
