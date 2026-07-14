using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class GameCompatibilityTests
    {
        internal static void Run(string root)
        {
            TestGameBuildNormalization();
            TestCompatibilityContext();
            TestRegistryAndInstallerThreadContext(root);
            TestInstalledBuildReader(root);
            Console.WriteLine("GameCompatibilityTests passed.");
        }

        private static void TestGameBuildNormalization()
        {
            Assert(GameBuildVersion.TryFromBuildId("2227", out var version) && version == "0.0.2227",
                "numeric game build should map to 0.0.N");
            Assert(GameBuildVersion.TryFromBuildLabel("build 2227", out version) && version == "0.0.2227",
                "human build label should map to 0.0.N");
            Assert(GameBuildVersion.TryNormalize("1.2.3-rc.1", out version) && version == "1.2.3-rc.1",
                "canonical product SemVer should remain unchanged");

            foreach (var invalid in new[] { null, "", "0", "02227", "+2227", "-1", "2227 ", "2147483648" })
            {
                Assert(!GameBuildVersion.TryFromBuildId(invalid, out _),
                    "invalid build id should be rejected: " + (invalid ?? "<null>"));
            }
        }

        private static void TestCompatibilityContext()
        {
            var manifest = ValidManifest("compat.context");
            manifest.SupportedGameVersionRange = "0.0.2227";
            manifest.SupportedLoaderVersionRange = ">=0.2.0 <0.3.0";
            manifest.SupportedSdkVersionRange = ">=0.1.0 <0.2.0";

            Assert(ManifestValidator.Validate(manifest).Count == 0,
                "context-free compatibility wrapper should syntax-check without requiring a game install");
            Assert(ManifestValidator.Validate(
                    manifest,
                    new ManifestValidationContext("0.0.2227", requireKnownGameVersion: true)).Count == 0,
                "matching production compatibility context should pass");
            Assert(ManifestValidator.Validate(
                    manifest,
                    new ManifestValidationContext("build 2227", requireKnownGameVersion: true)).Count == 0,
                "runtime build labels should normalize before range evaluation");

            var unknown = ManifestValidator.Validate(
                manifest,
                new ManifestValidationContext(requireKnownGameVersion: true));
            Assert(unknown.Any(error => error.Contains("unknown", StringComparison.OrdinalIgnoreCase)),
                "a constrained manifest should fail closed when production cannot identify the game");

            var wrongGame = ManifestValidator.Validate(
                manifest,
                new ManifestValidationContext("0.0.2226", requireKnownGameVersion: true));
            Assert(wrongGame.Any(error => error.Contains("does not include game 0.0.2226", StringComparison.Ordinal)),
                "a game-range mismatch should be actionable");

            var wrongLoader = ManifestValidator.Validate(
                manifest,
                new ManifestValidationContext("0.0.2227", loaderVersion: "0.3.0", requireKnownGameVersion: true));
            Assert(wrongLoader.Any(error => error.Contains("does not include loader 0.3.0", StringComparison.Ordinal)),
                "validation should use the supplied loader version rather than a global constant");
        }

        private static void TestRegistryAndInstallerThreadContext(string root)
        {
            var testRoot = Path.Combine(root, "compat-threading");
            var paths = new ManagerPaths(Path.Combine(testRoot, "BepInEx"));
            paths.EnsureCreated();
            var package = Path.Combine(testRoot, "constrained.topiaforgemod");
            Directory.CreateDirectory(testRoot);
            var manifest = ValidManifest("compat.threaded");
            manifest.SupportedGameVersionRange = "0.0.2227";
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "topiaforge.mod.json", JsonUtil.Serialize(manifest));
                WriteEntry(archive, manifest.EntryAssembly, "placeholder");
            }

            var strictUnknown = new ManifestValidationContext(requireKnownGameVersion: true);
            var rejected = new PackageInstaller().Install(package, paths, new ManagerState(), false, strictUnknown);
            Assert(!rejected.Ok && rejected.Errors.Any(error => error.Contains("unknown", StringComparison.OrdinalIgnoreCase)),
                "package installation should enforce its production validation context");

            var state = new ManagerState();
            var accepted = new PackageInstaller().Install(
                package,
                paths,
                state,
                false,
                new ManifestValidationContext("0.0.2227", requireKnownGameVersion: true));
            Assert(accepted.Ok, "package installation should accept the supported game build");
            var scanned = new ModRegistry().Scan(paths, state, strictUnknown).Single();
            Assert(!scanned.IsValid && scanned.Errors.Any(error => error.Contains("unknown", StringComparison.OrdinalIgnoreCase)),
                "registry scanning should enforce the same production validation context");
        }

        private static void TestInstalledBuildReader(string root)
        {
            var windows = Path.Combine(root, "version-reader", "windows");
            Directory.CreateDirectory(windows);
            File.WriteAllText(Path.Combine(windows, "installed-build.json"), "{\"id\":2227}");
            Assert(InstalledGameVersionReader.TryRead(windows, out var version, out _) && version == "0.0.2227",
                "runtime should read the launcher marker from a Windows/Proton game root");

            var launcher = Path.Combine(root, "version-reader", "mac");
            var macRoot = Path.Combine(launcher, "Robotopia.app", "Contents", "MacOS");
            Directory.CreateDirectory(macRoot);
            File.WriteAllText(Path.Combine(launcher, "installed-build.json"), "{\"id\":\"2227\"}");
            Assert(InstalledGameVersionReader.TryRead(macRoot, out version, out _) && version == "0.0.2227",
                "runtime should find the launcher marker beside a macOS app bundle");

            File.WriteAllText(Path.Combine(launcher, "installed-build.json"), "{\"id\":\"02227\"}");
            Assert(!InstalledGameVersionReader.TryRead(macRoot, out _, out var error) && error.Contains("rejected"),
                "runtime should fail closed on a noncanonical build id");
        }

        private static ModManifest ValidManifest(string id)
        {
            return new ModManifest
            {
                SchemaVersion = 3,
                Id = id,
                Name = id,
                Version = "1.0.0",
                Author = new ModAuthor { Name = "Test Author" },
                EntryAssembly = id + ".dll",
                EntryType = id + ".Entry"
            };
        }

        private static void WriteEntry(ZipArchive archive, string name, string value)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(value);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Manifest compatibility: " + message);
            }
        }
    }
}
