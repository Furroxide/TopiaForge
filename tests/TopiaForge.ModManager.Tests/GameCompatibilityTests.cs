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
            TestRuntimeCompatibility();
            TestCompatibleVersionSelection(root);
            TestShuffledVersionSelection(root);
            TestRegistryAndInstallerThreadContext(root);
            TestInstalledBuildReader(root);
            Console.WriteLine("GameCompatibilityTests passed.");
        }

        private static void TestGameBuildNormalization()
        {
            Assert(GameBuildVersion.TryFromBuildId("2309", out var version) && version == "0.0.2309",
                "numeric game build should map to 0.0.N");
            Assert(GameBuildVersion.TryFromBuildLabel("build 2309", out version) && version == "0.0.2309",
                "human build label should map to 0.0.N");
            Assert(GameBuildVersion.TryNormalize("1.2.3-rc.1", out version) && version == "1.2.3-rc.1",
                "canonical product SemVer should remain unchanged");

            foreach (var invalid in new[] { null, "", "0", "02309", "+2309", "-1", "2309 ", "2147483648" })
            {
                Assert(!GameBuildVersion.TryFromBuildId(invalid, out _),
                    "invalid build id should be rejected: " + (invalid ?? "<null>"));
            }
        }

        private static void TestCompatibilityContext()
        {
            var manifest = ValidManifest("compat.context");
            manifest.SupportedGameVersionRange = "0.0.2309";
            manifest.SupportedLoaderVersionRange = ">=0.1.0-rc.1 <0.2.0";
            manifest.SupportedSdkVersionRange = ">=0.1.0-rc.1 <0.2.0";

            Assert(ManifestValidator.Validate(manifest).Count == 0,
                "context-free compatibility wrapper should syntax-check without requiring a game install");
            Assert(ManifestValidator.Validate(
                    manifest,
                    new ManifestValidationContext("0.0.2309", requireKnownGameVersion: true)).Count == 0,
                "matching production compatibility context should pass");
            Assert(ManifestValidator.Validate(
                    manifest,
                    new ManifestValidationContext("build 2309", requireKnownGameVersion: true)).Count == 0,
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
                new ManifestValidationContext("0.0.2309", loaderVersion: "2.0.0", requireKnownGameVersion: true));
            Assert(wrongLoader.Any(error => error.Contains("does not include loader 2.0.0", StringComparison.Ordinal)),
                "validation should use the supplied loader version rather than a global constant");
        }

        private static void TestRuntimeCompatibility()
        {
            Assert(ManifestValidationContext.NormalizePlatform(" Proton ") == "windows" &&
                   ManifestValidationContext.NormalizePlatform("darwin") == "macos",
                "host platform aliases should normalize to manifest platform ids");
            Assert(ManifestValidationContext.NormalizeArchitecture("AMD64") == "x64" &&
                   ManifestValidationContext.NormalizeArchitecture("aarch64") == "arm64",
                "host architecture aliases should normalize to manifest architecture ids");

            var manifest = ValidManifest("compat.runtime");
            manifest.Platforms.Add("windows");
            manifest.Architectures.Add("x64");
            manifest.ContentTargets.Add("code");
            var proton = RuntimeContext("Proton", "AMD64", "CODE");
            var matched = ManifestRuntimeCompatibility.Evaluate(manifest, proton);
            Assert(matched.Status == ManifestRuntimeCompatibility.MatchedStatus &&
                   ManifestValidator.Validate(manifest, proton).Count == 0,
                "a Proton-hosted Windows game should match Windows x64 code packages");

            var wrongPlatform = RuntimeContext("macOS", "x64", "code");
            Assert(ManifestValidator.Validate(manifest, wrongPlatform)
                    .Any(error => error.Contains("host platform macos", StringComparison.Ordinal)),
                "a platform mismatch should fail validation with the normalized host value");
            var wrongArchitecture = RuntimeContext("windows", "arm64", "code");
            Assert(ManifestValidator.Validate(manifest, wrongArchitecture)
                    .Any(error => error.Contains("host architecture arm64", StringComparison.Ordinal)),
                "an architecture mismatch should fail validation");
            var wrongContent = RuntimeContext("windows", "x64", "standalonewindows64");
            Assert(ManifestValidator.Validate(manifest, wrongContent)
                    .Any(error => error.Contains("host-supported target", StringComparison.Ordinal)),
                "a content-target mismatch should fail validation");

            var portable = ValidManifest("compat.portable");
            var unknownHost = RuntimeContext(string.Empty, string.Empty);
            var portableDecision = ManifestRuntimeCompatibility.Evaluate(portable, unknownHost);
            Assert(portableDecision.Status == ManifestRuntimeCompatibility.PortableStatus &&
                   ManifestValidator.Validate(portable, unknownHost).Count == 0,
                "empty runtime constraint lists should remain portable even when host details are unknown");
            Assert(ManifestValidator.Validate(manifest, unknownHost)
                    .Any(error => error.Contains("unknown", StringComparison.OrdinalIgnoreCase)),
                "a constrained package should fail closed when strict host details are unknown");

            var authoring = new ManifestValidationContext(
                platform: "macos",
                architecture: "arm64",
                contentTargets: new[] { "standaloneosx" });
            Assert(ManifestRuntimeCompatibility.Evaluate(manifest, authoring).Status ==
                   ManifestRuntimeCompatibility.NotEvaluatedStatus &&
                   ManifestValidator.Validate(manifest, authoring).Count == 0,
                "authoring validation should syntax-check cross-target packages without applying the local host");
        }

        private static void TestCompatibleVersionSelection(string root)
        {
            var testRoot = Path.Combine(root, "runtime-compatible-selection");
            var paths = new ManagerPaths(Path.Combine(testRoot, "BepInEx"));
            paths.EnsureCreated();
            const string id = "compat.selection";

            // Create candidates in deliberately non-SemVer order. Selection must be based on normalized id,
            // SemVer, compatibility, and path rather than filesystem enumeration order.
            WriteInstalledPackage(paths, id, "2.0.0", "macos", "x64", "standaloneosx");
            WriteInstalledPackage(paths, id, "1.0.0", null, null, null);
            WriteInstalledPackage(paths, id, "1.5.0", "windows", "x64", "code");
            var context = RuntimeContext("proton", "amd64", "code", "standalonewindows64");
            var state = new ManagerState
            {
                Mods = new[]
                {
                    new InstalledModState { Id = id, Version = "1.0.0", Enabled = true }
                }.ToList()
            };

            var selected = new ModRegistry().Scan(paths, state, context).Single();
            var selectedDecision = ManifestRuntimeCompatibility.Evaluate(selected.Manifest!, context);
            Assert(selected.IsValid && selected.Manifest!.Version == "1.5.0" &&
                   selectedDecision.Status == ManifestRuntimeCompatibility.MatchedStatus,
                "an unpinned package should select the highest compatible SemVer");
            Assert(selected.SelectionReason.Contains("recovered unpinned selection", StringComparison.Ordinal) &&
                   selected.SelectionReason.Contains("1.0.0", StringComparison.Ordinal) &&
                   selected.SelectionReason.Contains("1.5.0", StringComparison.Ordinal),
                "an automatic unpinned recovery must be explicit in diagnostics");

            var pinnedState = new ManagerState
            {
                Mods = new[]
                {
                    new InstalledModState
                    {
                        Id = id,
                        Version = "2.0.0",
                        VersionPinned = true,
                        Enabled = true
                    }
                }.ToList()
            };
            var pinned = new ModRegistry().Scan(paths, pinnedState, context).Single();
            var rejectedDecision = ManifestRuntimeCompatibility.Evaluate(pinned.Manifest!, context);
            Assert(pinned.Manifest!.Version == "2.0.0" && !pinned.IsValid &&
                   rejectedDecision.Status == ManifestRuntimeCompatibility.RejectedStatus &&
                   pinned.Errors.Any(error => error.Contains("host platform", StringComparison.Ordinal)),
                "an incompatible exact pin should fail closed instead of falling back");

            var report = new LastRunReport
            {
                Packages = new[]
                {
                    new LastRunPackage
                    {
                        Id = id,
                        Version = pinned.Manifest.Version,
                        Selection = pinned.SelectionReason,
                        Compatibility = rejectedDecision.Status,
                        CompatibilityReasons = rejectedDecision.Errors.ToList()
                    }
                }.ToList()
            };
            var restored = JsonUtil.Deserialize<LastRunReport>(JsonUtil.Serialize(report));
            Assert(restored.Packages.Single().Compatibility == ManifestRuntimeCompatibility.RejectedStatus &&
                   restored.Packages.Single().CompatibilityReasons.SequenceEqual(rejectedDecision.Errors) &&
                   restored.Packages.Single().Selection.Contains("exact profile pin", StringComparison.Ordinal),
                "last-run compatibility and deterministic selection decisions should round-trip");
        }

        private static void TestShuffledVersionSelection(string root)
        {
            var permutations = new[]
            {
                new[] { "1.0.0", "1.5.0", "2.0.0" },
                new[] { "1.0.0", "2.0.0", "1.5.0" },
                new[] { "1.5.0", "1.0.0", "2.0.0" },
                new[] { "1.5.0", "2.0.0", "1.0.0" },
                new[] { "2.0.0", "1.0.0", "1.5.0" },
                new[] { "2.0.0", "1.5.0", "1.0.0" }
            };
            var context = RuntimeContext("windows", "x64", "code");
            for (var index = 0; index < permutations.Length; index++)
            {
                var paths = new ManagerPaths(Path.Combine(root, "selection-permutation-" + index, "BepInEx"));
                paths.EnsureCreated();
                foreach (var version in permutations[index])
                {
                    WriteInstalledPackage(
                        paths,
                        "compat.shuffle",
                        version,
                        version == "2.0.0" ? "macos" : null,
                        null,
                        null);
                }

                var state = new ManagerState();
                var selected = new ModRegistry().Scan(paths, state, context).Single();
                Assert(selected.IsValid && selected.Manifest!.Version == "1.5.0",
                    "selection must be invariant across every candidate creation-order permutation");
            }
        }

        private static void TestRegistryAndInstallerThreadContext(string root)
        {
            var testRoot = Path.Combine(root, "compat-threading");
            var paths = new ManagerPaths(Path.Combine(testRoot, "BepInEx"));
            paths.EnsureCreated();
            var package = Path.Combine(testRoot, "constrained.topiaforgemod");
            Directory.CreateDirectory(testRoot);
            var manifest = ValidManifest("compat.threaded");
            manifest.SupportedGameVersionRange = "0.0.2309";
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "topiaforge.mod.json", JsonUtil.Serialize(manifest));
                var assemblyEntry = archive.CreateEntry(manifest.EntryAssembly);
                using var output = assemblyEntry.Open();
                using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, manifest.EntryAssembly));
                input.CopyTo(output);
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
                new ManifestValidationContext("0.0.2309", requireKnownGameVersion: true));
            Assert(accepted.Ok, "package installation should accept the supported game build");
            var scanned = new ModRegistry().Scan(paths, state, strictUnknown).Single();
            Assert(!scanned.IsValid && scanned.Errors.Any(error => error.Contains("unknown", StringComparison.OrdinalIgnoreCase)),
                "registry scanning should enforce the same production validation context");
        }

        private static void TestInstalledBuildReader(string root)
        {
            var windowsLauncher = Path.Combine(root, "version-reader", "windows");
            var windows = Path.Combine(windowsLauncher, "Robotopia");
            Directory.CreateDirectory(windows);
            File.WriteAllText(Path.Combine(windowsLauncher, "installed-build.json"), "{\"id\":2309}");
            Assert(InstalledGameVersionReader.TryRead(windows, out var version, out _) && version == "0.0.2309",
                "runtime should read the launcher marker beside a Windows/Proton game root");

            File.WriteAllText(Path.Combine(windows, "installed-build.json"), "{\"id\":2228}");
            Assert(InstalledGameVersionReader.TryRead(windows, out version, out _) && version == "0.0.2228",
                "runtime should prefer a marker inside the Windows/Proton game root");

            var launcher = Path.Combine(root, "version-reader", "mac");
            var macRoot = Path.Combine(launcher, "Robotopia.app", "Contents", "MacOS");
            Directory.CreateDirectory(macRoot);
            File.WriteAllText(Path.Combine(launcher, "installed-build.json"), "{\"id\":\"2309\"}");
            Assert(InstalledGameVersionReader.TryRead(macRoot, out version, out _) && version == "0.0.2309",
                "runtime should find the launcher marker beside a macOS app bundle");

            File.WriteAllText(Path.Combine(launcher, "installed-build.json"), "{\"id\":\"02309\"}");
            Assert(!InstalledGameVersionReader.TryRead(macRoot, out _, out var error) && error.Contains("rejected"),
                "runtime should fail closed on a noncanonical build id");
        }

        private static ModManifest ValidManifest(string id)
        {
            return new ModManifest
            {
                SchemaVersion = 5,
                Id = id,
                Name = id,
                Version = "1.0.0",
                Author = new ModAuthor { Name = "Test Author" },
                EntryAssembly = "TopiaForge.ValidTestMod.dll",
                EntryType = "TopiaForge.ValidTestMod.ValidMod"
            };
        }

        private static ManifestValidationContext RuntimeContext(
            string platform,
            string architecture,
            params string[] contentTargets)
        {
            return new ManifestValidationContext(
                gameVersion: "0.0.2309",
                loaderVersion: TopiaForgeVersions.LoaderVersion,
                sdkVersion: TopiaForgeVersions.SdkVersion,
                requireKnownGameVersion: true,
                platform: platform,
                architecture: architecture,
                contentTargets: contentTargets,
                enforceRuntimeCompatibility: true);
        }

        private static void WriteInstalledPackage(
            ManagerPaths paths,
            string id,
            string version,
            string? platform,
            string? architecture,
            string? contentTarget)
        {
            var manifest = ValidManifest(id);
            manifest.Version = version;
            if (platform != null) manifest.Platforms.Add(platform);
            if (architecture != null) manifest.Architectures.Add(architecture);
            if (contentTarget != null) manifest.ContentTargets.Add(contentTarget);
            var packagePath = paths.GetPackagePath(id, version);
            Directory.CreateDirectory(packagePath);
            JsonUtil.SaveFile(Path.Combine(packagePath, "topiaforge.mod.json"), manifest);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, manifest.EntryAssembly),
                Path.Combine(packagePath, manifest.EntryAssembly),
                overwrite: true);
            var sourcePath = Path.Combine(paths.Staging, id + "-" + version + ".topiaforgemod");
            File.WriteAllText(sourcePath, id + "@" + version);
            JsonUtil.SaveFile(
                Path.Combine(packagePath, PackageInstallReceipt.FileName),
                PackageInstallReceipt.Create(sourcePath, packagePath, manifest));
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
