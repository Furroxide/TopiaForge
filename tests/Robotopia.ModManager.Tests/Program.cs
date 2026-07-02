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
                TestDependencyOrder(root);
                TestFrameworkDependencyOrder(root);
                TestUgcExportSchemaContract();
                UgcLiveSyncTests.Run();
                SdkSurfaceTests.Run();
                PromptRegistryTests.Run();
                OverrideTests.Run();
                ConversationTests.Run();
                ConversationDirectorTests.Run();
                ChronosTests.Run();
                GameCompatTests.Run();
                UiKitCoreTests.Run();
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
                SchemaVersion = 1,
                Id = "applied.mod",
                Name = "Applied",
                Version = "1.0.0",
                EntryAssembly = "Applied.dll",
                EntryType = "Applied.Entry"
            };
            var pendingManifest = new ModManifest
            {
                SchemaVersion = 1,
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
                    SchemaVersion = 1,
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

        private static void TestDependencyOrder(string root)
        {
            var state = new ManagerState();
            var depManifest = new ModManifest
            {
                SchemaVersion = 1,
                Id = "dependency.mod",
                Name = "Dependency",
                Version = "1.0.0",
                EntryAssembly = "Dependency.dll",
                EntryType = "Dependency.Entry"
            };
            var mainManifest = new ModManifest
            {
                SchemaVersion = 1,
                Id = "main.mod",
                Name = "Main",
                Version = "1.0.0",
                EntryAssembly = "Main.dll",
                EntryType = "Main.Entry"
            };
            mainManifest.Dependencies.Add(new ModDependency { Id = "dependency.mod", Version = "1.0.0" });

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
                SchemaVersion = 1,
                Id = "robotopia.assets",
                Name = "Robotopia Assets",
                Version = "0.1.0",
                EntryAssembly = "Robotopia.Assets.dll",
                EntryType = "Robotopia.Assets.AssetsMod"
            };
            var promptsManifest = new ModManifest
            {
                SchemaVersion = 1,
                Id = "robotopia.prompts",
                Name = "Robotopia Prompts",
                Version = "0.1.0",
                EntryAssembly = "Robotopia.Prompts.dll",
                EntryType = "Robotopia.Prompts.PromptsMod"
            };
            var consumerManifest = new ModManifest
            {
                SchemaVersion = 1,
                Id = "consumer.mod",
                Name = "Consumer",
                Version = "1.0.0",
                EntryAssembly = "Consumer.dll",
                EntryType = "Consumer.Entry"
            };
            consumerManifest.Dependencies.Add(new ModDependency { Id = "robotopia.assets", VersionRange = ">=0.1.0" });
            consumerManifest.Dependencies.Add(new ModDependency { Id = "robotopia.prompts", VersionRange = ">=0.1.0" });
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

        private static string FindRepoRoot()
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
                    SchemaVersion = 1,
                    Id = id,
                    Name = name,
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
