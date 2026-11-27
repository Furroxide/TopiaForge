using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using TopiaForge.WorldCompanion.Editor;
using UnityEditor;
using UnityEngine;

namespace TopiaForge.WorldCompanion.Editor.Tests
{
    public sealed class WorldValidatorTests
    {
        private const int MaximumEntries = 4096;
        private const long MaximumSnapshotBytes = 256L * 1024 * 1024;
        private static readonly StringComparison PathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private string folder;
        private string projectRoot;
        private bool ownsFolder;

        [SetUp] public void SetUp()
        {
            ownsFolder = false;
            GuardEditorArguments(Environment.GetCommandLineArgs());
            projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!string.Equals(projectRoot, Path.GetFullPath(Directory.GetCurrentDirectory()), PathComparison)
                || !string.Equals(Path.GetFullPath(Application.dataPath), Path.Combine(projectRoot, "Assets"), PathComparison))
                throw new InvalidDataException("Run the fixture from its disposable Unity project root.");
            GuardPath(projectRoot, Application.dataPath, true, false);
            folder = "Assets/MarkerTests-" + Guid.NewGuid().ToString("N");
            var fullFolder = GuardPath(projectRoot, Path.Combine(projectRoot, folder), true, true);
            var meta = GuardPath(projectRoot, fullFolder + ".meta", false, true);
            if (Directory.Exists(fullFolder) || File.Exists(meta))
                throw new InvalidDataException("The fixture cannot take ownership of an existing asset path.");
            var guid = AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            if (string.IsNullOrEmpty(guid) || AssetDatabase.GUIDToAssetPath(guid) != folder)
                throw new InvalidDataException("Unity did not create the exact requested fixture folder.");
            ownsFolder = true;
            GuardPath(projectRoot, fullFolder, true, false);
        }

        [TearDown] public void TearDown()
        {
            if (!ownsFolder) return;
            var fullFolder = GuardPath(projectRoot, Path.Combine(projectRoot, folder), true, false);
            var count = 0;
            // Reject links and an unexpectedly large tree before Unity's recursive asset deletion.
            GuardedFiles(projectRoot, fullFolder, ref count);
            GuardPath(projectRoot, fullFolder + ".meta", false, true);
            if (!AssetDatabase.DeleteAsset(folder))
                throw new IOException("Unity could not delete the owned marker fixture.");
            ownsFolder = false;
        }

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
            var configPath = GuardPath(projectRoot, Path.Combine(projectRoot, "topiaforge.world.json"), false, true);
            var oldConfig = File.Exists(configPath) ? ReadConfigBytes(configPath) : null;
            // Pair only to a fixed child of the folder this test created, never an environment-selected temp root.
            var pairedMod = GuardPath(projectRoot, Path.Combine(projectRoot, folder, "PairedMod"), true, true);
            Directory.CreateDirectory(pairedMod);
            var manifestPath = GuardPath(projectRoot, Path.Combine(pairedMod, "topiaforge.mod.json"), false, true);
            using (var manifest = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes("{}");
                manifest.Write(bytes, 0, bytes.Length);
            }
            var config = new ExportConfig { worldPrefab = prefabPath, modPath = pairedMod };
            var testConfig = Encoding.UTF8.GetBytes(JsonUtility.ToJson(config));
            var wroteConfig = false;
            try
            {
                ReplaceConfig(projectRoot, configPath, oldConfig, testConfig);
                wroteConfig = true;
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
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
                if (wroteConfig) ReplaceConfig(projectRoot, configPath, testConfig, oldConfig);
                // TearDown owns the guarded paired-mod directory as part of the fixture asset folder.
            }
        }

        [TestCase("-topiaforgeModPath")]
        [TestCase("-topiaforgeBundleName")]
        [TestCase("-topiaforgeWorldPrefab")]
        public void ExportOverridesCannotReplaceTheOwnedFixture(string option)
        {
            Assert.Throws<InvalidDataException>(() => GuardEditorArguments(new[] { "Unity", option, "unowned" }));
        }

        [Test] public void SnapshotRejectsUnownedPairedDirectories()
        {
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var sibling = project + "-unowned";
            foreach (var unowned in new[] { project, sibling, Path.Combine(project, "..", "MarkerUnowned") })
                Assert.Throws<InvalidDataException>(() => Snapshot(project, unowned));
        }

        [Test] public void FixtureGuardsRejectWrongFileSystemKinds()
        {
            var path = GuardPath(projectRoot, Path.Combine(projectRoot, folder, "kind-probe.txt"), false, true);
            File.WriteAllText(path, "retained");
            Assert.Throws<InvalidDataException>(() => GuardPath(projectRoot, path, true, false));
            Assert.Throws<InvalidDataException>(() => GuardPath(projectRoot, Path.Combine(projectRoot, folder), false, false));
            Assert.That(Encoding.UTF8.GetString(ReadConfigBytes(path)), Is.EqualTo("retained"));
        }

        [Test] public void TraversalStopsAtItsEntryBudget()
        {
            var path = GuardPath(projectRoot, Path.Combine(projectRoot, folder, "budget-probe.txt"), false, true);
            File.WriteAllText(path, "retained");
            var count = MaximumEntries;
            Assert.Throws<InvalidDataException>(() => GuardedFiles(projectRoot, Path.Combine(projectRoot, folder), ref count));
            Assert.That(Encoding.UTF8.GetString(ReadConfigBytes(path)), Is.EqualTo("retained"));
        }

        [Test] public void ConfigurationRestoreCannotOverwriteAnExternalChange()
        {
            var path = GuardPath(projectRoot, Path.Combine(projectRoot, folder, "config-probe.json"), false, true);
            var installed = Encoding.UTF8.GetBytes("fixture");
            ReplaceConfig(projectRoot, path, null, installed);
            File.WriteAllText(GuardPath(projectRoot, path, false, false), "external change");
            Assert.Throws<InvalidDataException>(() => ReplaceConfig(projectRoot, path, installed, Encoding.UTF8.GetBytes("original")));
            Assert.That(Encoding.UTF8.GetString(ReadConfigBytes(path)), Is.EqualTo("external change"));
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
                GuardPath(projectRoot, Path.Combine(projectRoot, path), false, true);
                GuardPath(projectRoot, Path.Combine(projectRoot, path + ".meta"), false, true);
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
        private static void ReplaceConfig(string project, string path, byte[] expected, byte[] replacement)
        {
            path = GuardPath(project, path, false, expected == null);
            using (var stream = new FileStream(path, expected == null ? FileMode.CreateNew : FileMode.Open,
                FileAccess.ReadWrite, FileShare.None))
            {
                if (stream.Length > 1024 * 1024)
                    throw new InvalidDataException("The pairing configuration exceeded the fixture bound.");
                var actual = new byte[(int)stream.Length];
                var offset = 0;
                while (offset < actual.Length)
                {
                    var read = stream.Read(actual, offset, actual.Length - offset);
                    if (read == 0) throw new EndOfStreamException("The pairing configuration changed while reading.");
                    offset += read;
                }
                if (!(expected ?? Array.Empty<byte>()).SequenceEqual(actual))
                    throw new InvalidDataException("The pairing configuration changed; the fixture will not overwrite it.");
                if (replacement != null)
                {
                    stream.Position = 0;
                    stream.Write(replacement, 0, replacement.Length);
                    stream.SetLength(replacement.Length);
                    stream.Flush();
                }
            }
            if (replacement == null) File.Delete(GuardPath(project, path, false, false));
        }

        private static void GuardEditorArguments(IEnumerable<string> arguments)
        {
            if (arguments.Any(value => value == "-topiaforgeModPath" || value == "-topiaforgeBundleName"
                || value == "-topiaforgeWorldPrefab"))
                throw new InvalidDataException("Run marker tests without export overrides; they could replace the owned test input or destination.");
        }

        private static string GuardPath(string project, string path, bool directory, bool allowMissing)
        {
            var root = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("Fixture paths must remain strictly inside the project root.");
            for (var current = full; current != null; current = Path.GetDirectoryName(current))
            {
                var attributes = Attributes(current);
                if (attributes == null) continue;
                if ((attributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new InvalidDataException("Fixture paths cannot traverse links, junctions or devices.");
                if (current != full && (attributes.Value & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("A fixture ancestor is not an ordinary directory.");
            }
            var leaf = Attributes(full);
            if (leaf == null)
            {
                if (!allowMissing) throw new InvalidDataException("A required fixture path is missing.");
            }
            else if (((leaf.Value & FileAttributes.Directory) != 0) != directory)
                throw new InvalidDataException("A fixture path has the wrong file-system kind.");
            return full;
        }

        private static FileAttributes? Attributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static List<string> GuardedFiles(string project, string directory, ref int count)
        {
            var files = new List<string>();
            var pending = new Queue<string>();
            pending.Enqueue(GuardPath(project, directory, true, false));
            while (pending.Count != 0)
            {
                var current = GuardPath(project, pending.Dequeue(), true, false);
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (++count > MaximumEntries)
                        throw new InvalidDataException("The fixture snapshot exceeds its entry bound.");
                    var attributes = Attributes(entry);
                    if (attributes == null) throw new InvalidDataException("A snapshot entry disappeared.");
                    var isDirectory = (attributes.Value & FileAttributes.Directory) != 0;
                    var guarded = GuardPath(project, entry, isDirectory, false);
                    if (isDirectory) pending.Enqueue(guarded); else files.Add(guarded);
                }
            }
            return files;
        }

        private static SortedDictionary<string, string> Snapshot(string project, string pairedMod)
        {
            // Admit every root before traversing any of them, including the caller-supplied paired path.
            var directories = new[] { Path.Combine(project, "Assets"), Path.Combine(project, "ProjectSettings"),
                Path.Combine(project, "Build"), pairedMod };
            foreach (var directory in directories) GuardPath(project, directory, true, true);
            var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var count = 0;
            long bytes = 0;
            using (var hash = SHA256.Create())
                foreach (var directory in directories)
                    if (Directory.Exists(directory))
                        foreach (var file in GuardedFiles(project, directory, ref count).OrderBy(x => x, StringComparer.Ordinal))
                        {
                            if (result.ContainsKey(file)) continue;
                            using (var stream = File.OpenRead(GuardPath(project, file, false, false)))
                            {
                                if (stream.Length > MaximumSnapshotBytes - bytes)
                                    throw new InvalidDataException("The fixture snapshot exceeds its byte bound.");
                                hash.Initialize();
                                var buffer = new byte[8192];
                                int read;
                                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                                {
                                    bytes += read;
                                    if (bytes > MaximumSnapshotBytes)
                                        throw new InvalidDataException("The fixture snapshot grew beyond its byte bound.");
                                    hash.TransformBlock(buffer, 0, read, buffer, 0);
                                }
                                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                                result[file] = Convert.ToBase64String(hash.Hash);
                            }
                        }
            return result;
        }
    }
}
