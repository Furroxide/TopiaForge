using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Robotopia.ModManager.Core;

namespace Robotopia.ModManager.Tests
{
    internal static class Program
    {
        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "RobotopiaModManagerTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                TestInstallSuccess(root);
                TestUpdatePreservesDisabledState(root);
                TestAppliedRestartRequirementsClear();
                TestMissingManifestRejected(root);
                TestZipTraversalRejected(root);
                TestSchemaV1Rejected(root);
                TestInstallPrunesOldVersions(root);
                TestInboxInstallConsumesFiles(root);
                TestInboxNewestVersionWins(root);
                TestInboxFailureLeavesFile(root);
                TestScanIgnoresSupersededBrokenVersions(root);
                TestScanStillReportsFullyBrokenPackage(root);
                TestPruneSupersededVersionsRespectsStatePin(root);
                TestRequiredDependenciesHelper();
                TestDependencyOrder(root);
                TestFrameworkDependencyOrder(root);
                TestUgcExportSchemaContract();
                UgcLiveSyncTests.Run();
                SdkSurfaceTests.Run();
                PromptRegistryTests.Run();
                OverrideTests.Run();
                ConversationTests.Run();
                ConversationDirectorTests.Run();
                ObjectiveRunnerTests.Run();
                RobotTargetFactsTests.Run();
                SandboxProgramDirectorTests.Run();
                ChronosTests.Run();
                GameCompatTests.Run();
                UiKitCoreTests.Run();
                UiKitSourceConventionTests.Run();
                Console.WriteLine("All QuantumWorks tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void TestInstallSuccess(string root)
        {
            var paths = NewPaths(root, "success");
            var state = new ManagerState();
            var package = Path.Combine(root, "ok.robotopiamod");
            CreatePackage(package, "alpha.mod", "Alpha", "1.0.0", "Alpha.dll", "Alpha.Entry");

            var result = new PackageInstaller().Install(package, paths, state, restartRequired: false);
            Assert(result.Ok, "valid package should install");
            Assert(result.Manifest!.Id == "alpha.mod", "manifest name should map to mod id");
            Assert(result.Manifest.Name == "Alpha", "manifest displayName should map to display name");
            Assert(File.Exists(Path.Combine(paths.GetPackagePath("alpha.mod", "1.0.0"), "robotopia.mod.json")), "manifest should be installed");
            Assert(state.Find("alpha.mod")?.Enabled == true, "installed mod should be enabled");
        }

        private static void TestUpdatePreservesDisabledState(string root)
        {
            var paths = NewPaths(root, "update");
            var state = new ManagerState();
            var firstPackage = Path.Combine(root, "alpha-1.0.0.robotopiamod");
            var secondPackage = Path.Combine(root, "alpha-1.1.0.robotopiamod");
            var installer = new PackageInstaller();
            CreatePackage(firstPackage, "alpha.mod", "Alpha", "1.0.0", "Alpha.dll", "Alpha.Entry");
            CreatePackage(secondPackage, "alpha.mod", "Alpha", "1.1.0", "Alpha.dll", "Alpha.Entry");

            Assert(installer.Install(firstPackage, paths, state, restartRequired: false).Ok, "initial package should install");
            var installed = state.Find("alpha.mod");
            Assert(installed != null, "installed state should exist");
            installed!.Enabled = false;

            var update = installer.Install(secondPackage, paths, state, restartRequired: true);

            Assert(update.Ok, "update package should install");
            Assert(File.Exists(Path.Combine(paths.GetPackagePath("alpha.mod", "1.1.0"), "robotopia.mod.json")), "updated manifest should be installed");
            Assert(state.Find("alpha.mod")?.Version == "1.1.0", "updated version should be selected");
            Assert(state.Find("alpha.mod")?.Enabled == false, "disabled mod should stay disabled after update");
            Assert(state.Find("alpha.mod")?.RestartRequired == true, "update should mark restart required");
        }

        private static void TestAppliedRestartRequirementsClear()
        {
            var state = new ManagerState();
            var appliedManifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = "applied.mod",
                Name = "Applied",
                Version = "1.0.0",
                EntryAssembly = "Applied.dll",
                EntryType = "Applied.Entry"
            };
            var pendingManifest = new ModManifest
            {
                SchemaVersion = 2,
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
            var package = Path.Combine(root, "missing.robotopiamod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("Something.dll");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    writer.Write("not a dll");
                }
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(e => e.Contains("robotopia.mod.json")), "missing manifest should be rejected");
        }

        private static void TestZipTraversalRejected(string root)
        {
            var paths = NewPaths(root, "traversal");
            var package = Path.Combine(root, "traversal.robotopiamod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                zip.CreateEntry("../escape.txt");
                WriteEntry(zip, "robotopia.mod.json", JsonUtil.Serialize(new ModManifest
                {
                    SchemaVersion = 2,
                    Id = "bad.mod",
                    Name = "Bad",
                    Version = "1.0.0",
                    EntryAssembly = "Bad.dll",
                    EntryType = "Bad.Entry"
                }));
                WriteEntry(zip, "Bad.dll", "not a dll");
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(e => e.Contains("outside")), "zip traversal should be rejected");
        }

        private static void TestSchemaV1Rejected(string root)
        {
            var paths = NewPaths(root, "schema-v1");
            var package = Path.Combine(root, "schema-v1.robotopiamod");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "robotopia.mod.json", JsonUtil.Serialize(new ModManifest
                {
                    SchemaVersion = 1,
                    Id = "old.mod",
                    Name = "Old",
                    Author = new ModAuthor { Name = "QuantumWorks" },
                    Version = "1.0.0",
                    EntryAssembly = "Old.dll",
                    EntryType = "Old.Entry"
                }));
                WriteEntry(zip, "Old.dll", "not a dll");
            }

            var result = new PackageInstaller().Install(package, paths, new ManagerState(), restartRequired: false);
            Assert(!result.Ok && result.Errors.Any(e => e.Contains("schemaVersion must be 2")), "schema v1 should be rejected");
        }

        private static void TestInstallPrunesOldVersions(string root)
        {
            var paths = NewPaths(root, "prune-install");
            var state = new ManagerState();
            var installer = new PackageInstaller();
            var firstPackage = Path.Combine(root, "prune-1.0.0.robotopiamod");
            var secondPackage = Path.Combine(root, "prune-1.1.0.robotopiamod");
            CreatePackage(firstPackage, "prune.mod", "Prune", "1.0.0", "Prune.dll", "Prune.Entry");
            CreatePackage(secondPackage, "prune.mod", "Prune", "1.1.0", "Prune.dll", "Prune.Entry");

            Assert(installer.Install(firstPackage, paths, state, restartRequired: false).Ok, "1.0.0 should install");
            Assert(installer.Install(secondPackage, paths, state, restartRequired: false).Ok, "1.1.0 should install");

            Assert(!Directory.Exists(paths.GetPackagePath("prune.mod", "1.0.0")), "superseded 1.0.0 should be pruned");
            Assert(Directory.Exists(paths.GetPackagePath("prune.mod", "1.1.0")), "installed 1.1.0 should remain");
        }

        private static void TestInboxInstallConsumesFiles(string root)
        {
            var paths = NewPaths(root, "inbox-consume");
            var state = new ManagerState();
            var alphaFile = Path.Combine(paths.PackageInbox, "alpha.robotopiamod");
            var betaFile = Path.Combine(paths.PackageInbox, "beta.robotopiamod");
            CreatePackage(alphaFile, "alpha.mod", "Alpha", "1.0.0", "Alpha.dll", "Alpha.Entry");
            CreatePackage(betaFile, "beta.mod", "Beta", "1.0.0", "Beta.dll", "Beta.Entry");

            var results = new PackageInstaller().InstallInbox(paths, state, restartRequired: false);

            Assert(results.Count == 2, "both inbox packages should be processed");
            Assert(results.All(r => r.Install!.Ok), "both inbox packages should install");
            Assert(results.All(r => r.Consumed), "both inbox files should be consumed");
            Assert(!File.Exists(alphaFile) && !File.Exists(betaFile), "consumed inbox files should be gone");
            Assert(state.Find("alpha.mod")?.RestartRequired == false, "startup-style install should not flag restart");
            Assert(state.Find("beta.mod")?.Version == "1.0.0", "state should track the installed version");
        }

        private static void TestInboxNewestVersionWins(string root)
        {
            var paths = NewPaths(root, "inbox-newest");
            var state = new ManagerState();
            var oldFile = Path.Combine(paths.PackageInbox, "gamma-1.0.0.robotopiamod");
            var newFile = Path.Combine(paths.PackageInbox, "gamma-1.1.0.robotopiamod");
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

        private static void TestInboxFailureLeavesFile(string root)
        {
            var paths = NewPaths(root, "inbox-failure");
            var badFile = Path.Combine(paths.PackageInbox, "broken.robotopiamod");
            using (var zip = ZipFile.Open(badFile, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "Something.dll", "not a dll");
            }

            var results = new PackageInstaller().InstallInbox(paths, new ManagerState(), restartRequired: false);

            Assert(results.Count == 1, "failing inbox package should be reported");
            Assert(!results[0].Install!.Ok, "install should fail without a manifest");
            Assert(!results[0].Consumed && File.Exists(badFile), "failed inbox file should be left for inspection");
        }

        private static void TestScanIgnoresSupersededBrokenVersions(string root)
        {
            var paths = NewPaths(root, "scan-superseded");
            var state = new ManagerState();
            var package = Path.Combine(root, "scan-superseded.robotopiamod");
            CreatePackage(package, "delta.mod", "Delta", "1.0.0", "Delta.dll", "Delta.Entry");
            Assert(new PackageInstaller().Install(package, paths, state, restartRequired: false).Ok, "current version should install");

            // A stale version whose old-schema manifest no longer parses (the real-world source of the
            // per-launch warning wall).
            var staleDirectory = paths.GetPackagePath("delta.mod", "0.1.0");
            Directory.CreateDirectory(staleDirectory);
            File.WriteAllText(Path.Combine(staleDirectory, "robotopia.mod.json"), "not json at all");

            var packages = new ModRegistry().Scan(paths, state);
            var delta = packages.Where(p => p.PackagePath.Contains("delta.mod")).ToList();

            Assert(delta.Count == 1, "stale broken version should fold into its mod's group");
            Assert(delta[0].IsValid && delta[0].Manifest!.Version == "1.0.0", "the valid current version should win the pick");
        }

        private static void TestScanStillReportsFullyBrokenPackage(string root)
        {
            var paths = NewPaths(root, "scan-broken");
            var brokenDirectory = paths.GetPackagePath("epsilon.mod", "0.1.0");
            Directory.CreateDirectory(brokenDirectory);
            File.WriteAllText(Path.Combine(brokenDirectory, "robotopia.mod.json"), "not json at all");

            var packages = new ModRegistry().Scan(paths, new ManagerState());
            var epsilon = packages.Where(p => p.PackagePath.Contains("epsilon.mod")).ToList();

            Assert(epsilon.Count == 1, "a mod with no valid version should still surface");
            Assert(!epsilon[0].IsValid && epsilon[0].Errors.Count > 0, "the broken package should carry its error");
        }

        private static void TestPruneSupersededVersionsRespectsStatePin(string root)
        {
            var paths = NewPaths(root, "prune-startup");
            var state = new ManagerState();
            var pinnedManifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = "zeta.mod",
                Name = "Zeta",
                Version = "1.0.0",
                EntryAssembly = "Zeta.dll",
                EntryType = "Zeta.Entry"
            };
            state.Upsert(pinnedManifest, enabled: true, restartRequired: false);
            Directory.CreateDirectory(paths.GetPackagePath("zeta.mod", "1.0.0"));
            Directory.CreateDirectory(paths.GetPackagePath("zeta.mod", "1.1.0"));
            // No state entry for this id: nothing may be deleted.
            Directory.CreateDirectory(paths.GetPackagePath("orphan.mod", "0.1.0"));
            Directory.CreateDirectory(paths.GetPackagePath("orphan.mod", "0.2.0"));

            var pruned = new List<string>();
            new ModRegistry().PruneSupersededVersions(paths, state, pruned.Add);

            Assert(Directory.Exists(paths.GetPackagePath("zeta.mod", "1.0.0")), "state-pinned version should be kept");
            Assert(!Directory.Exists(paths.GetPackagePath("zeta.mod", "1.1.0")), "non-pinned version should be pruned even when higher");
            Assert(pruned.Count == 1 && pruned[0].Contains("1.1.0"), "prune should report the removed version");
            Assert(Directory.Exists(paths.GetPackagePath("orphan.mod", "0.1.0"))
                && Directory.Exists(paths.GetPackagePath("orphan.mod", "0.2.0")), "ids without state must not be touched");
        }

        private static void TestRequiredDependenciesHelper()
        {
            var manifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = "eta.mod",
                Name = "Eta",
                Version = "1.0.0",
                EntryAssembly = "Eta.dll",
                EntryType = "Eta.Entry"
            };
            manifest.VpmDependencies.Add("framework.mod", ">=1.0.0");
            manifest.Dependencies = new List<ModDependency>
            {
                new ModDependency { Id = "hard.mod" },
                new ModDependency { Id = "soft.mod", Optional = true }
            };

            var required = DependencyResolver.GetRequiredDependencies(manifest).Select(d => d.Id).ToList();
            Assert(required.Contains("framework.mod") && required.Contains("hard.mod"), "vpm + hard dependencies are required");
            Assert(!required.Contains("soft.mod"), "optional dependencies are not required");

            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FRAMEWORK.MOD" };
            Assert(DependencyResolver.FindFailedRequiredDependency(manifest, failed) == "framework.mod",
                "a failed required dependency should be found case-insensitively");
            Assert(DependencyResolver.FindFailedRequiredDependency(manifest, new HashSet<string>()) == null,
                "no failures means no gating");
        }

        private static void TestDependencyOrder(string root)
        {
            var state = new ManagerState();
            var depManifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = "dependency.mod",
                Name = "Dependency",
                Version = "1.0.0",
                EntryAssembly = "Dependency.dll",
                EntryType = "Dependency.Entry"
            };
            var mainManifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = "main.mod",
                Name = "Main",
                Version = "1.0.0",
                EntryAssembly = "Main.dll",
                EntryType = "Main.Entry"
            };
            mainManifest.VpmDependencies.Add("dependency.mod", ">=1.0.0");

            var dependency = new ModPackage(Path.Combine(root, "dep"), depManifest, state.Upsert(depManifest, true, false), Array.Empty<string>());
            var main = new ModPackage(Path.Combine(root, "main"), mainManifest, state.Upsert(mainManifest, true, false), Array.Empty<string>());
            var result = new DependencyResolver().Resolve(new[] { main, dependency });

            Assert(result.OrderedPackages.Count == 2, "both mods should be loadable");
            Assert(result.OrderedPackages[0].Manifest!.Id == "dependency.mod", "dependency should load first");
            Assert(result.OrderedPackages[1].Manifest!.Id == "main.mod", "dependent mod should load second");
        }

        private static void TestFrameworkDependencyOrder(string root)
        {
            var state = new ManagerState();
            var assetsManifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = "robotopia.assets",
                Name = "Robotopia Assets",
                Version = "0.1.0",
                EntryAssembly = "Robotopia.Assets.dll",
                EntryType = "Robotopia.Assets.AssetsMod"
            };
            var promptsManifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = "robotopia.prompts",
                Name = "Robotopia Prompts",
                Version = "0.1.0",
                EntryAssembly = "Robotopia.Prompts.dll",
                EntryType = "Robotopia.Prompts.PromptsMod"
            };
            var consumerManifest = new ModManifest
            {
                SchemaVersion = 2,
                Id = "consumer.mod",
                Name = "Consumer",
                Version = "1.0.0",
                EntryAssembly = "Consumer.dll",
                EntryType = "Consumer.Entry"
            };
            consumerManifest.VpmDependencies.Add("robotopia.assets", ">=0.1.0");
            consumerManifest.VpmDependencies.Add("robotopia.prompts", ">=0.1.0");
            consumerManifest.LoadAfter.Add("robotopia.assets");
            consumerManifest.LoadAfter.Add("robotopia.prompts");

            var assets = new ModPackage(Path.Combine(root, "assets"), assetsManifest, state.Upsert(assetsManifest, true, false), Array.Empty<string>());
            var prompts = new ModPackage(Path.Combine(root, "prompts"), promptsManifest, state.Upsert(promptsManifest, true, false), Array.Empty<string>());
            var consumer = new ModPackage(Path.Combine(root, "consumer"), consumerManifest, state.Upsert(consumerManifest, true, false), Array.Empty<string>());
            var result = new DependencyResolver().Resolve(new[] { consumer, prompts, assets });
            var orderedIds = result.OrderedPackages.Select(p => p.Manifest!.Id).ToList();

            Assert(orderedIds.Count == 3, "framework providers and consumer should all be loadable");
            Assert(orderedIds.IndexOf("robotopia.assets") < orderedIds.IndexOf("consumer.mod"), "assets provider should load before its consumer");
            Assert(orderedIds.IndexOf("robotopia.prompts") < orderedIds.IndexOf("consumer.mod"), "prompts provider should load before its consumer");
        }

        // Pins the shared UGC export JSON contract (the surface the Unity exporter writes and the game
        // importer deserializes into UgcExportProject). GameCode-free on purpose: the test harness targets
        // net8.0 and never references the game's Mono assemblies, so this validates the golden fixture against
        // the documented shape. The authoritative round-trip is exercised by the manual E2E (docs/UgcLiveSync.md)
        // and the Unity exporter self-check.
        private static void TestUgcExportSchemaContract()
        {
            var fixturePath = Path.Combine(FindRepoRoot(), "tests", "fixtures", "ugc", "sample-project.json");
            Assert(File.Exists(fixturePath), "UGC sample fixture should exist at tests/fixtures/ugc/sample-project.json");

            using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
            var root = document.RootElement;

            foreach (var key in new[] { "version", "name", "created", "modified", "assets", "local-assets", "scenes" })
            {
                Assert(root.TryGetProperty(key, out _), "UGC project must define '" + key + "'");
            }

            // local-assets values must carry a recognized 'type' discriminator (others only warn in-game).
            var supportedLocalAssetTypes = new[] { "lore", "lore-collection", "personality" };
            foreach (var asset in root.GetProperty("local-assets").EnumerateObject())
            {
                Assert(asset.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String,
                    "local asset '" + asset.Name + "' must have a string 'type'");
                Assert(supportedLocalAssetTypes.Contains(type.GetString()),
                    "local asset '" + asset.Name + "' has unsupported type '" + type.GetString() + "'");
            }

            Assert(root.GetProperty("scenes").TryGetProperty("main", out var scene), "fixture must contain scene 'main'");
            Assert(scene.GetProperty("id").GetString() == "main", "scene id must match its map key");
            var entities = scene.GetProperty("entities");

            // Every component group must be represented so the contract stays exercised end to end.
            var componentKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in entities.EnumerateObject())
            {
                Assert(entity.Value.TryGetProperty("components", out var components), "entity '" + entity.Name + "' must have components");
                foreach (var component in components.EnumerateObject())
                {
                    componentKeys.Add(component.Name);
                }
            }
            foreach (var required in new[] { "transform", "model-renderer", "prefab-instance", "spawn-location", "poi", "aoi", "agent" })
            {
                Assert(componentKeys.Contains(required), "fixture must exercise the '" + required + "' component");
            }
            // An unknown sibling key proves JsonExtensionData (extraComponents) tolerance.
            Assert(componentKeys.Contains("robotopia-future-component"),
                "fixture must include an unknown component to prove extraComponents tolerance");

            // Handedness pin: the game maps UGC position (x,y,z) to Unity (-x,y,z). ent-root is the golden case.
            var position = entities.GetProperty("ent-root").GetProperty("components").GetProperty("transform").GetProperty("position");
            var ugcX = position.GetProperty("x").GetDouble();
            Assert(Math.Abs(ugcX - 1.0) < 1e-9, "ent-root UGC x should be 1.0");
            Assert(Math.Abs(-ugcX - (-1.0)) < 1e-9, "documented handedness: Unity x must be -1.0 when UGC x is 1.0");
        }

        internal static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RobotopiaModManager.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate repo root (RobotopiaModManager.slnx) from " + AppContext.BaseDirectory);
        }

        private static ManagerPaths NewPaths(string root, string name)
        {
            var paths = new ManagerPaths(Path.Combine(root, name, "BepInEx"));
            paths.EnsureCreated();
            return paths;
        }

        private static void CreatePackage(string path, string id, string name, string version, string assembly, string type)
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "robotopia.mod.json", JsonUtil.Serialize(new ModManifest
                {
                    SchemaVersion = 2,
                    Id = id,
                    Name = name,
                    Author = new ModAuthor { Name = "QuantumWorks" },
                    Version = version,
                    EntryAssembly = assembly,
                    EntryType = type
                }));
                WriteEntry(zip, assembly, "not a real dll");
            }
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write(content);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }
    }
}
