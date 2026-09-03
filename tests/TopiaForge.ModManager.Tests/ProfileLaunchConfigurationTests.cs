using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ProfileLaunchConfigurationTests
    {
        public static void Run()
        {
            TestExactProfileDoesNotMutateDurableState();
            TestSafeModeIsTemporary();
            TestDartJsonContract();
            TestProfileWithoutWorldLaunchBootsNormally();
            TestMainMenuCommandCrossesTheWire();
            TestMainMenuCommandRejectsATarget();
            TestUnsafeWorldLaunchRejected();
            TestRetiredSchemaRejected();
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
            const string json = "{\"schemaVersion\":3,\"profileId\":\"dart\","
                + "\"safeMode\":false,\"inheritManagerModState\":false,"
                + "\"enabledMods\":[\"alpha.mod\"],"
                + "\"selectedVersions\":{\"alpha.mod\":\"1.2.3\"},"
                + "\"worldLaunch\":{\"command\":\"launch-target\","
                + "\"worldId\":\"a.b.world\",\"gamemodeId\":\"a.b.mode\","
                + "\"loadMode\":\"sceneReplacement\",\"allowAdditiveFallback\":false},"
                + "\"futureField\":{\"preservedByLauncher\":true}}";
            var profile = JsonUtil.Deserialize<ProfileLaunchConfiguration>(json);

            Assert(profile.Validate().Count == 0, "Dart profile JSON should validate in Core");
            Assert(profile.EnabledMods.Count == 1, "Dart enabledMods should deserialize");
            Assert(profile.SelectedVersions["alpha.mod"] == "1.2.3", "Dart selected version should deserialize");
            Assert(profile.WorldLaunch != null, "the launcher's gamemode choice must survive the crossing");
            Assert(profile.WorldLaunch!.GamemodeId == "a.b.mode" && profile.WorldLaunch.WorldId == "a.b.world",
                "the world and gamemode ids must deserialize");
            Assert(profile.WorldLaunch.PreferSceneReplacement && !profile.WorldLaunch.AllowAdditiveFallback,
                "the load-mode preferences must deserialize");
            Assert(!profile.WorldLaunch.IsMainMenu, "a launch-target command is not an ordinary boot");
        }

        /// <summary>
        /// The launcher says "play normally" out loud. It has to: the manager remembers a selection of
        /// its own, and cannot tell an absent instruction from a request for the ordinary menu.
        /// </summary>
        private static void TestMainMenuCommandCrossesTheWire()
        {
            const string json = "{\"schemaVersion\":3,\"profileId\":\"dart\","
                + "\"safeMode\":false,\"inheritManagerModState\":true,"
                + "\"enabledMods\":[],\"selectedVersions\":{},"
                + "\"worldLaunch\":{\"command\":\"main-menu\"}}";
            var profile = JsonUtil.Deserialize<ProfileLaunchConfiguration>(json);

            Assert(profile.Validate().Count == 0,
                "a main-menu command carries no target, so demanding one would reject every ordinary launch");
            Assert(profile.WorldLaunch != null && profile.WorldLaunch!.IsMainMenu,
                "the ordinary-boot command must survive the crossing");
        }

        /// <summary>A main-menu command must not smuggle a target past the empty-gamemode rule.</summary>
        private static void TestMainMenuCommandRejectsATarget()
        {
            var profile = new ProfileLaunchConfiguration
            {
                SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                ProfileId = "contradictory",
                InheritManagerModState = true,
                WorldLaunch = new WorldLaunchIntent
                {
                    Command = WorldLaunchIntent.MainMenuCommand,
                    GamemodeId = "a.b.mode"
                }
            };

            Assert(profile.Validate().Any(error => error.Contains("worldLaunch.gamemodeId")),
                "asking for the menu and for a gamemode at once is not a launch anyone meant");
        }

        /// <summary>
        /// A profile from a launcher that predates the command must stay valid: it expressed no
        /// instruction either way, and the manager falls back to its own remembered selection.
        /// </summary>
        private static void TestProfileWithoutWorldLaunchBootsNormally()
        {
            const string json = "{\"schemaVersion\":3,\"profileId\":\"dart\","
                + "\"safeMode\":false,\"inheritManagerModState\":true,"
                + "\"enabledMods\":[],\"selectedVersions\":{}}";
            var profile = JsonUtil.Deserialize<ProfileLaunchConfiguration>(json);

            Assert(profile.Validate().Count == 0, "a profile with no launch intent is valid");
            Assert(profile.WorldLaunch == null, "an older launcher issues no instruction at all");
        }

        /// <summary>An intent naming an unsafe id must be refused rather than reaching the world service.</summary>
        private static void TestUnsafeWorldLaunchRejected()
        {
            var profile = new ProfileLaunchConfiguration
            {
                SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                ProfileId = "bad-intent",
                InheritManagerModState = true,
                WorldLaunch = new WorldLaunchIntent { GamemodeId = "../../escape" }
            };

            Assert(profile.Validate().Any(error => error.Contains("worldLaunch.gamemodeId")),
                "an unsafe gamemode id must be rejected");
        }

        private static void TestRegistryHonorsSelectedVersion()
        {
            var root = Path.Combine(Path.GetTempPath(), "TopiaForgeProfileTest-" + Guid.NewGuid().ToString("N"));
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

        private static void TestRetiredSchemaRejected()
        {
            var profile = new ProfileLaunchConfiguration
            {
                SchemaVersion = 2,
                ProfileId = "retired-format"
            };

            Assert(
                profile.Validate().Any(error => error.Contains("schemaVersion must be 3")),
                "a retired profile schemaVersion must be rejected explicitly");
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
            JsonUtil.SaveFile(Path.Combine(packagePath, "topiaforge.mod.json"), new ModManifest
            {
                SchemaVersion = 5,
                Id = id,
                Name = "Alpha",
                Version = version,
                Author = new ModAuthor { Name = "TopiaForge" },
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
