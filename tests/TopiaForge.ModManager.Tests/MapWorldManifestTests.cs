using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class MapWorldManifestTests
    {
        internal static void Run()
        {
            var fixture = Path.Combine(Program.FindRepoRoot(), "tests/fixtures/gamemode-v6/serialization/manifest/v6-map-file-json.json");
            using var body = JsonDocument.Parse(File.ReadAllText(fixture));
            var manifest = ModManifestJson.Deserialize(body.RootElement.GetProperty("manifest").GetRawText());
            manifest.SupportedLoaderVersionRange = ">=0.2.0 <1.0.0";
            var world = manifest.Contributions!.Worlds.Single();
            Check(world.Transitions.SequenceEqual(new[] { ModTransitions.SceneReplacement }) && world.Spawn!.Kind == ModSpawnPolicy.ProviderDefaultKind,
                "Omitted map defaults must be effective in the typed model.");
            var owned = Directory.CreateTempSubdirectory("MapWorldManifestTests-");
            var root = owned.FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Content"));
                var file = Path.Combine(root, "Content/world.json");
                File.WriteAllText(file, "{\"scenes\":[]}");
                manifest.Hashes["Content/world.json"] = Hash(file);
                var issues = ManifestValidator.Validate(manifest, new ManifestValidationContext(loaderVersion: "0.2.0", sdkVersion: "0.2.0"));
                Check(issues.Count == 0, "A hash-covered map declaration is valid: " + string.Join("; ", issues));
                Check(ManifestValidator.Validate(manifest, new ManifestValidationContext(loaderVersion: "0.1.0-rc.1"))
                    .Any(e => e.Contains("needs TopiaForge 0.2.0")), "An older runtime must still refuse map packages.");
                Check(ManifestContentValidator.Validate(root, manifest).Count == 0, "Valid map bytes pass the native package boundary.");
                var archive = Path.Combine(root, "example.topiaforgemod");
                File.WriteAllText(archive, "receipt fixture");
                Check(PackageInstallReceipt.Create(archive, root, manifest).Files.Single(f => f.Path == "Content/world.json").Critical,
                    "A packaged map is critical receipt content.");
                File.AppendAllText(file, " ");
                Check(ManifestContentValidator.Validate(root, manifest).Any(e => e.Contains("does not match")), "Tampered map bytes fail.");
                File.Delete(file);
                Check(ManifestContentValidator.Validate(root, manifest).Any(e => e.Contains("not an ordinary package file")), "A missing map fails.");
                manifest.Hashes.Clear();
                Check(ManifestContentValidator.Validate(root, manifest).Any(e => e.Contains("missing its pack-time SHA-256")), "An unhashed packaged map fails semantic validation.");
                foreach (var range in new[] { "*", ">=0.1.0 <1.0.0", ">=0.2.0-rc.1 <1.0.0" })
                {
                    manifest.SupportedLoaderVersionRange = range;
                    Check(ManifestContentValidator.Validate(root, manifest).Any(e => e.StartsWith("TFPKG180:")), "Map features enforce the stable 0.2.0 loader floor.");
                }
                manifest.SupportedLoaderVersionRange = ">=0.2.0 <1.0.0";
                Check(!ManifestContentValidator.Validate(root, manifest).Any(e => e.StartsWith("TFPKG180:")), "The stable loader floor is sufficient.");
            }
            finally { owned.Delete(true); }
            Console.WriteLine("Map world manifest: 12 checks passed.");
            MapWorldPackageTests.Run();
        }
        private static string Hash(string path)
        { using var sha = SHA256.Create(); return string.Concat(sha.ComputeHash(File.ReadAllBytes(path)).Select(b => b.ToString("x2"))); }
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Map world: " + message); }
    }
}
