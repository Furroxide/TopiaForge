using System;
using System.IO;
using Robotopia.GameCompat.Extractor;

namespace Robotopia.ModManager.Tests
{
    internal static class GameVersionLabelReaderTests
    {
        internal static void Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "RobotopiaGameVersionTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                ReadsLauncherBuildBesideMacApp(root);
                FallsBackToMacBundleVersion(root);
                ReadsLauncherBuildFromWindowsInstall(root);
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
            File.WriteAllText(Path.Combine(launcher, "installed-build.json"), "{\"id\":2227}");
            WriteInfoPlist(launcher, "0.1", "0");

            Assert(GameVersionLabelReader.Read(managed) == "build 2227",
                "macOS capture should read installed-build.json beside Robotopia.app");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed) == "0.0.2227",
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
            var install = Path.Combine(root, "windows-build");
            var managed = Path.Combine(install, "Robotopia_Data", "Managed");
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(install, "installed-build.json"), "{\"id\":\"310\"}");

            Assert(GameVersionLabelReader.Read(managed) == "build 310",
                "Windows/Proton capture should read launcher metadata from the install root");
            Assert(GameVersionLabelReader.ReadCanonicalVersion(managed) == "0.0.310",
                "Windows/Proton capture should expose the canonical build SemVer");
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
