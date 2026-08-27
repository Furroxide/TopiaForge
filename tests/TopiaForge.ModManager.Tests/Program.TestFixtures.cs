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
        private static string StringFromCodeUnits(params int[] codeUnits)
        {
            var characters = new char[codeUnits.Length];
            for (var index = 0; index < codeUnits.Length; index++)
            {
                characters[index] = checked((char)codeUnits[index]);
            }

            return new string(characters);
        }

        private static ModManifest TestManifest(string id)
        {
            return new ModManifest
            {
                SchemaVersion = 5,
                Id = id,
                Name = id,
                Version = "1.0.0",
                EntryAssembly = id + ".dll",
                EntryType = id + ".Entry"
            };
        }

        private static ModPackage TestPackage(string root, ManagerState state, ModManifest manifest)
        {
            return new ModPackage(
                Path.Combine(root, manifest.Id),
                manifest,
                state.Upsert(manifest, enabled: true, restartRequired: false),
                Array.Empty<string>());
        }

        // Pins the shared UGC export JSON contract (the surface the Unity exporter writes and the game
        // importer deserializes into UgcExportProject). GameCode-free on purpose: the test harness targets
        // net8.0 and never references the game's Mono assemblies, so this validates the golden fixture against
        // the documented shape. The authoritative round-trip is exercised by the manual E2E (docs/UgcLiveSync.md)
        // and the Unity exporter self-check.
        internal static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TopiaForge.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("Could not locate repo root (TopiaForge.slnx) from " + AppContext.BaseDirectory);
        }

        private static ManagerPaths NewPaths(string root, string name)
        {
            var paths = new ManagerPaths(Path.Combine(root, name, "BepInEx"));
            paths.EnsureCreated();
            return paths;
        }

        private static void CreatePackage(
            string path,
            string id,
            string name,
            string version,
            string assembly,
            string type,
            string category = "")
        {
            CreatePackageCandidate(
                path,
                id,
                name,
                version,
                supportedGameVersionRange: "*",
                corruptEntryAssembly: false,
                category: category);
        }

        private static void CreatePackageCandidate(
            string path,
            string id,
            string name,
            string version,
            string supportedGameVersionRange,
            bool corruptEntryAssembly,
            IReadOnlyDictionary<string, string>? dependencies = null,
            string category = "")
        {
            const string fixtureAssembly = "TopiaForge.ValidTestMod.dll";
            const string fixtureType = "TopiaForge.ValidTestMod.ValidMod";
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "topiaforge.mod.json", JsonUtil.Serialize(new ModManifest
                {
                    SchemaVersion = 5,
                    Id = id,
                    Name = name,
                    Author = new ModAuthor { Name = "TopiaForge" },
                    Version = version,
                    EntryAssembly = fixtureAssembly,
                    EntryType = fixtureType,
                    SupportedGameVersionRange = supportedGameVersionRange,
                    SupportedLoaderVersionRange = ">=0.1.0-rc.1 <0.2.0",
                    SupportedSdkVersionRange = ">=0.1.0-rc.1 <0.2.0",
                    Category = category,
                    Dependencies = dependencies == null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(dependencies, StringComparer.Ordinal)
                }));
                if (corruptEntryAssembly)
                {
                    WriteEntry(zip, fixtureAssembly, "not a managed PE image");
                }
                else
                {
                    var entry = zip.CreateEntry(fixtureAssembly);
                    using (var output = entry.Open())
                    using (var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, fixtureAssembly)))
                    {
                        input.CopyTo(output);
                    }
                }
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
