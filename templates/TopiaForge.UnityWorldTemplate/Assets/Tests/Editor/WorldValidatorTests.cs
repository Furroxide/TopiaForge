using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using TopiaForge.WorldCompanion.Editor;
using UnityEditor;
using UnityEngine;

namespace TopiaForge.WorldCompanion.Editor.Tests
{
    public sealed class WorldValidatorTests
    {
        private string folder;
        [SetUp] public void SetUp()
        {
            folder = "Assets/MarkerTests-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
        }
        [TearDown] public void TearDown() => AssetDatabase.DeleteAsset(folder);

        [TestCase(0, false, false, "SpawnPoint", false)]
        [TestCase(1, false, false, "SpawnPoint", true)]
        [TestCase(2, false, false, "SpawnPoint", false)]
        [TestCase(0, true, false, "SpawnPoint", true)]
        [TestCase(1, false, true, "SpawnPoint", true)]
        [TestCase(1, false, false, "spawnpoint", false)]
        public void ActualPrefabMatchesRuntimeMarkerRules(int children, bool rootMarker, bool inactive, string name, bool valid)
        {
            var path = SavePrefab(children, rootMarker, inactive, name);
            var result = WorldValidator.Validate(path);
            Assert.That(result.Errors.Count == 0, Is.EqualTo(valid), string.Join("; ", result.Errors));
        }

        [Test] public void DuplicatePrefabCannotWriteHdrpImporterOrExports()
        {
            var prefabPath = SavePrefab(2, false, false, "SpawnPoint");
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var configPath = Path.Combine(projectRoot, "topiaforge.world.json");
            var oldConfig = File.Exists(configPath) ? ReadConfigBytes(configPath) : null;
            var pairedMod = Path.Combine(Path.GetTempPath(), "TopiaForgeMarkerMod-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(pairedMod);
            File.WriteAllText(Path.Combine(pairedMod, "topiaforge.mod.json"), "{}");
            try
            {
                var config = new ExportConfig { worldPrefab = prefabPath, modPath = pairedMod };
                File.WriteAllText(configPath, JsonUtility.ToJson(config));
                var before = Snapshot(projectRoot, pairedMod);
                var importer = AssetImporter.GetAtPath(prefabPath);
                var label = importer.assetBundleName;
                var build = typeof(WorldBundleBuilder).GetMethod("BuildInternal", BindingFlags.Static | BindingFlags.NonPublic);
                var error = Assert.Throws<TargetInvocationException>(() => build.Invoke(null, null));
                Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(error.InnerException.Message, Does.Contain("Multiple objects named 'SpawnPoint'"));
                Assert.That(importer.assetBundleName, Is.EqualTo(label));
                Assert.That(Snapshot(projectRoot, pairedMod), Is.EquivalentTo(before), "Rejected export must leave assets, settings and both output trees unchanged.");
            }
            finally
            {
                if (oldConfig == null) File.Delete(configPath); else File.WriteAllBytes(configPath, oldConfig);
                Directory.Delete(pairedMod, true);
            }
        }

        [Serializable] private sealed class ExportConfig
        {
            public int schemaVersion = 2;
            public string worldId = "example.marker.world";
            public string bundleName = "marker-test";
            public string worldPrefab;
            public string modPath;
        }
        private string SavePrefab(int children, bool rootMarker, bool inactive, string name)
        {
            var root = new GameObject(rootMarker ? "SpawnPoint" : "World");
            try
            {
                root.AddComponent<BoxCollider>();
                for (var index = 0; index < children; index++)
                {
                    var child = new GameObject(name); child.transform.SetParent(root.transform); child.SetActive(!inactive);
                }
                var path = folder + (rootMarker ? "/SpawnPoint.prefab" : "/World.prefab");
                PrefabUtility.SaveAsPrefabAsset(root, path); return path;
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        private static byte[] ReadConfigBytes(string path)
        {
            const int maximum = 1024 * 1024;
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                var bytes = reader.ReadBytes(maximum + 1);
                if (bytes.Length > maximum) throw new InvalidDataException("World configuration exceeds the test's bound.");
                return bytes;
            }
        }
        private static SortedDictionary<string, string> Snapshot(string project, string pairedMod)
        {
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using (var hash = SHA256.Create())
                foreach (var directory in new[] { Path.Combine(project, "Assets"), Path.Combine(project, "ProjectSettings"), Path.Combine(project, "Build"), pairedMod })
                    if (Directory.Exists(directory))
                        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                            using (var stream = File.OpenRead(file))
                                result[file] = Convert.ToBase64String(hash.ComputeHash(stream));
            return result;
        }
    }
}
