using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Robotopia.ModManager.Core;

namespace Robotopia.ModManager.Tests
{
    internal static class ProfileLaunchConfigurationTests
    {
        public static void Run()
        {
            TestExactProfileDoesNotMutateDurableState();
            TestSafeModeIsTemporary();
            TestDartJsonContract();
            TestRegistryHonorsSelectedVersion();
            TestUnsafeProfileIdRejected();
        }

        private static void TestExactProfileDoesNotMutateDurableState()
        {
            var durable = State(
                Mod("alpha.mod", "2.0.0", enabled: false),
                Mod("beta.mod", "1.0.0", enabled: true));
            var profile = new ProfileLaunchConfiguration
            {
                SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                ProfileId = "isolated",
                InheritManagerModState = false,
                EnabledMods = new List<string> { "alpha.mod" },
                SelectedVersions = new Dictionary<string, string>
                {
                    ["alpha.mod"] = "1.0.0"
                }
            };

            var effective = profile.CreateEffectiveState(durable);

            Assert(effective.Find("alpha.mod")!.Enabled, "profile should enable alpha");
            Assert(effective.Find("alpha.mod")!.Version == "1.0.0", "profile should select alpha 1.0.0");
            Assert(!effective.Find("beta.mod")!.Enabled, "profile should disable beta");
            Assert(!durable.Find("alpha.mod")!.Enabled, "durable alpha enablement must be unchanged");
            Assert(durable.Find("alpha.mod")!.Version == "2.0.0", "durable alpha version must be unchanged");
            Assert(durable.Find("beta.mod")!.Enabled, "durable beta enablement must be unchanged");
        }

        private static void TestSafeModeIsTemporary()
        {
            var durable = State(Mod("alpha.mod", "1.0.0", enabled: true));
            var profile = new ProfileLaunchConfiguration
            {
                SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                ProfileId = "safe",
                SafeMode = true,
                EnabledMods = new List<string> { "alpha.mod" }
            };

            var effective = profile.CreateEffectiveState(durable);

            Assert(!effective.Find("alpha.mod")!.Enabled, "safe mode should disable alpha for this run");
            Assert(durable.Find("alpha.mod")!.Enabled, "safe mode must not disable durable alpha state");
        }

        private static void TestDartJsonContract()
        {
            const string json = "{\"schemaVersion\":1,\"profileId\":\"dart\","
                + "\"safeMode\":false,\"inheritManagerModState\":false,"
                + "\"enabledMods\":[\"alpha.mod\"],"
                + "\"selectedVersions\":{\"alpha.mod\":\"1.2.3\"},"
                + "\"futureField\":{\"preservedByLauncher\":true}}";
            var profile = JsonUtil.Deserialize<ProfileLaunchConfiguration>(json);

            Assert(profile.Validate().Count == 0, "Dart profile JSON should validate in Core");
            Assert(profile.EnabledMods.Count == 1, "Dart enabledMods should deserialize");
            Assert(profile.SelectedVersions["alpha.mod"] == "1.2.3", "Dart selected version should deserialize");
        }

        private static void TestRegistryHonorsSelectedVersion()
        {
            var root = Path.Combine(Path.GetTempPath(), "RobotopiaProfileTest-" + Guid.NewGuid().ToString("N"));
            var paths = new ManagerPaths(Path.Combine(root, "BepInEx"));
            paths.EnsureCreated();
            try
            {
                WritePackageManifest(paths, "alpha.mod", "1.0.0");
                WritePackageManifest(paths, "alpha.mod", "2.0.0");
                var durable = State(Mod("alpha.mod", "2.0.0", enabled: true));
                var profile = new ProfileLaunchConfiguration
                {
                    SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                    ProfileId = "version-pin",
                    InheritManagerModState = true,
                    SelectedVersions = new Dictionary<string, string>
                    {
                        ["alpha.mod"] = "1.0.0"
                    }
                };

                var effective = profile.CreateEffectiveState(durable);
                var selected = new ModRegistry().Scan(paths, effective).Single();

                Assert(selected.Manifest!.Version == "1.0.0", "registry should select the profile-pinned version");
                Assert(selected.IsEnabled, "inherited enabled state should apply to the selected version");
                Assert(durable.Find("alpha.mod")!.Version == "2.0.0", "registry scan must not change durable selection");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        private static void TestUnsafeProfileIdRejected()
        {
            var profile = new ProfileLaunchConfiguration
            {
                SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                ProfileId = "forged\nlog-entry"
            };

            Assert(profile.Validate().Count != 0, "control characters in profile ids must be rejected");
        }

        private static void WritePackageManifest(ManagerPaths paths, string id, string version)
        {
            var packagePath = paths.GetPackagePath(id, version);
            Directory.CreateDirectory(packagePath);
            JsonUtil.SaveFile(Path.Combine(packagePath, "robotopia.mod.json"), new ModManifest
            {
                SchemaVersion = 2,
                Id = id,
                Name = "Alpha",
                Version = version,
                Author = new ModAuthor { Name = "Robotopia" },
                EntryAssembly = "Alpha.dll",
                EntryType = "Alpha.Entry"
            });
        }

        private static ManagerState State(params InstalledModState[] mods)
        {
            return new ManagerState { Mods = new List<InstalledModState>(mods) };
        }

        private static InstalledModState Mod(string id, string version, bool enabled)
        {
            return new InstalledModState { Id = id, Version = version, Enabled = enabled };
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Profile launch test failed: " + message);
            }
        }
    }
}
