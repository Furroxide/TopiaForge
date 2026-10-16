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
            Assert(componentKeys.Contains("topiaforge-future-component"),
                "fixture must include an unknown component to prove extraComponents tolerance");

            // Handedness pin: the game maps UGC position (x,y,z) to Unity (-x,y,z). ent-root is the golden case.
            var position = entities.GetProperty("ent-root").GetProperty("components").GetProperty("transform").GetProperty("position");
            var ugcX = position.GetProperty("x").GetDouble();
            Assert(Math.Abs(ugcX - 1.0) < 1e-9, "ent-root UGC x should be 1.0");
            Assert(Math.Abs(-ugcX - (-1.0)) < 1e-9, "documented handedness: Unity x must be -1.0 when UGC x is 1.0");
        }

        private static void TestPendingRuntimeManifestContracts()
        {
            var root = FindRepoRoot();
            using var sandbox = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                root, "mods", "TopiaForge.Sandbox", "topiaforge.mod.json")));
            using var zombies = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                root, "mods", "TopiaForge.Zombies", "topiaforge.mod.json")));
            using var worlds = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                root, "mods", "TopiaForge.Worlds", "topiaforge.mod.json")));

            Assert(sandbox.RootElement.GetProperty("dependencies").GetProperty("io.github.furroxide.topiaforge.worlds").GetString()
                    == ">=0.1.0-rc.1 <0.2.0",
                "Sandbox must require the V1 Worlds contract");
            Assert(zombies.RootElement.GetProperty("dependencies").GetProperty("io.github.furroxide.topiaforge.worlds").GetString()
                    == ">=0.1.0-rc.1 <0.2.0",
                "Zombies must require the V1 Worlds contract");
            Assert(worlds.RootElement.GetProperty("supportedSdkVersionRange").GetString() == ">=0.1.0-rc.1 <0.2.0",
                "scene-coordinated framework mods must require the V1 SDK line");
            Assert(sandbox.RootElement.GetProperty("version").GetString() == "0.1.0-rc.1"
                && zombies.RootElement.GetProperty("version").GetString() == "0.1.0-rc.1"
                && worlds.RootElement.GetProperty("version").GetString() == "0.1.0-rc.1",
                "first-party runtime packages must move atomically to the V1 release candidate");
        }

    }
}
