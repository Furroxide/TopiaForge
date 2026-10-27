using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapWorldPackageTests
    {
        internal static void Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "map-package-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var fixture = Path.Combine(Program.FindRepoRoot(), "tests/fixtures/gamemode-v6/serialization/manifest/v6-map-file-unhashed.json");
                using var document = JsonDocument.Parse(File.ReadAllText(fixture));
                var manifest = ModManifestJson.Deserialize(document.RootElement.GetProperty("manifest").GetRawText());
                manifest.EntryAssembly = "TopiaForge.ValidTestMod.dll";
                manifest.EntryType = "TopiaForge.ValidTestMod.ValidMod";
                manifest.SupportedLoaderVersionRange = ">=0.2.0 <1.0.0";
                var bytes = Encoding.UTF8.GetBytes("{\"scenes\":[]}");
                using var sha = SHA256.Create();
                manifest.Hashes["Content/world.json"] = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
                var paths = new ManagerPaths(Path.Combine(root, "BepInEx"));
                paths.EnsureCreated();
                var state = new ManagerState();
                var context = new ManifestValidationContext(loaderVersion: "0.2.0", sdkVersion: "0.2.0");
                var package = Path.Combine(root, "map.topiaforgemod");

                PackageInstallResult Install(string json, byte[]? payload)
                {
                    if (File.Exists(package)) File.Delete(package);
                    using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
                    {
                        Write(zip, "topiaforge.mod.json", Encoding.UTF8.GetBytes(json));
                        Write(zip, manifest.EntryAssembly, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, manifest.EntryAssembly)));
                        if (payload != null) Write(zip, "Content/world.json", payload);
                    }
                    return new PackageInstaller().Install(package, paths, state, false, context);
                }

                var source = JsonUtil.Serialize(manifest);
                var valid = Install(source, bytes);
                Check(valid.Ok, "A valid map archive must install: " + string.Join("; ", valid.Errors));
                var installedMap = Path.Combine(paths.GetPackagePath(manifest.Id, manifest.Version), "Content/world.json");
                Check(File.ReadAllBytes(installedMap).SequenceEqual(bytes), "Installation must preserve exact map bytes.");
                var tampered = Install(source, Encoding.UTF8.GetBytes("changed"));
                Check(!tampered.Ok && tampered.Errors.Any(error => error.Contains("does not match")), "Tampered archive payload must fail preflight.");
                var missing = Install(source, null);
                Check(!missing.Ok && missing.Errors.Any(error => error.Contains("not an ordinary package file")), "Missing archive payload must fail preflight.");
                manifest.Hashes.Clear();
                var unhashed = Install(JsonUtil.Serialize(manifest), bytes);
                Check(!unhashed.Ok && unhashed.Errors.Any(error => error.Contains("pack-time SHA-256")), "Unhashed archive payload must fail preflight.");

                // These pass the schema's lexical pattern but fail the full portable-path contract.
                foreach (var unsafePath in new[] { "Content/\uFF23\uFF2F\uFF2E.json", "Content/cafe\u0301.json" })
                {
                    manifest.Contributions!.Worlds.Single().Content!.File = unsafePath;
                    var unsafeResult = Install(JsonUtil.Serialize(manifest), null);
                    Check(!unsafeResult.Ok && unsafeResult.Errors.Any(error => error.Contains("content.file")),
                        "An unsafe map reference cannot bypass payload admission. Ok=" + unsafeResult.Ok + "; " + string.Join("; ", unsafeResult.Errors));
                }

                foreach (var field in new[] { "file", "kind", "scene" })
                {
                    var raw = source.Replace("\"kind\":\"map\"", "\"" + field + "\":\"first\",\"" + field + "\":\"second\",\"kind\":\"map\"");
                    var rejected = Install(raw, bytes);
                    Check(!rejected.Ok && rejected.Errors.Any(error => error.Contains("duplicate")), "Duplicate map fields must fail native archive admission.");
                }
                Check(File.ReadAllBytes(installedMap).SequenceEqual(bytes), "Failed replacements must preserve the previous installed map.");
                Console.WriteLine("Map world package boundary: 11 checks passed.");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Write(ZipArchive zip, string path, byte[] bytes)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Map world package: " + message);
        }
    }
}
