using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;
using TopiaForge.ValidTestMod;

namespace TopiaForge.ModManager.Tests
{
    internal static class ServiceScaffoldRuntimeTests
    {
        private const string EntryAssembly = "TopiaForge.ValidTestMod.dll";
        private const string ApiAssembly = "TopiaForge.ValidTestMod.Api.dll";

        public static void Run(string root)
        {
            AssertTemplateExportsOnlyItsContractAssembly();

            var manifest = CreateManifest();
            var manifestErrors = ManifestValidator.Validate(manifest);
            Assert(manifestErrors.Count == 0,
                "service scaffold manifest should pass the C# validator: " + string.Join("; ", manifestErrors));

            var colliding = CreateManifest();
            colliding.ApiAssemblies[0] = EntryAssembly;
            Assert(ManifestValidator.Validate(colliding).Any(error =>
                    error.Contains("apiAssemblies", StringComparison.Ordinal) &&
                    error.Contains("collision", StringComparison.OrdinalIgnoreCase)),
                "the C# validator must reject exporting the service entry assembly as its own API contract");

            var package = Path.Combine(root, "service-scaffold-runtime.topiaforgemod");
            WritePackage(package, manifest);

            var paths = new ManagerPaths(Path.Combine(root, "service-scaffold-runtime", "BepInEx"));
            var state = new ManagerState();
            var install = new PackageInstaller().Install(package, paths, state, restartRequired: false);
            Assert(install.Ok,
                "service package should install after metadata validation: " + string.Join("; ", install.Errors));

            var scanned = new ModRegistry().Scan(paths, state);
            Assert(scanned.Count == 1, "runtime scan should discover exactly one installed service package");
            Assert(scanned[0].IsValid,
                "runtime scan should accept the service package: " + string.Join("; ", scanned[0].Errors));
            Assert(scanned[0].Manifest!.ApiAssemblies.SequenceEqual(new[] { ApiAssembly }, StringComparer.Ordinal),
                "runtime scan must expose only the separate contract assembly");
            Assert(File.Exists(Path.Combine(scanned[0].PackagePath, EntryAssembly)) &&
                   File.Exists(Path.Combine(scanned[0].PackagePath, ApiAssembly)),
                "installed service package should contain both private entry and exported contract assemblies");

            Console.WriteLine("ServiceScaffoldRuntimeTests passed.");
        }

        private static void AssertTemplateExportsOnlyItsContractAssembly()
        {
            var repository = Program.FindRepoRoot();
            var templateRoot = Path.Combine(repository, "templates", "mod", "service");
            using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(templateRoot, "template.json"))))
            {
                var apiAssemblies = document.RootElement
                    .GetProperty("manifestDefaults")
                    .GetProperty("apiAssemblies");
                Assert(apiAssemblies.GetArrayLength() == 1 &&
                       apiAssemblies[0].GetString() == "{{ASSEMBLY_NAME}}.Api.dll",
                    "service template must export exactly its separate contract assembly");
            }

            var entryProject = File.ReadAllText(Path.Combine(templateRoot, "{{ASSEMBLY_NAME}}.csproj"));
            Assert(entryProject.Contains(
                    "ProjectReference Include=\"contracts\\{{ASSEMBLY_NAME}}.Api\\{{ASSEMBLY_NAME}}.Api.csproj\"",
                    StringComparison.Ordinal),
                "service entry project must reference its local contract project");
            Assert(entryProject.Contains("Compile Remove=\"contracts\\**\\*.cs\"", StringComparison.Ordinal),
                "service entry project must not compile contract source into the implementation assembly");

            var contractRoot = Path.Combine(templateRoot, "contracts", "{{ASSEMBLY_NAME}}.Api");
            Assert(File.Exists(Path.Combine(contractRoot, "{{ASSEMBLY_NAME}}.Api.csproj")) &&
                   File.Exists(Path.Combine(contractRoot, "I{{TYPE_NAME}}Service.cs")),
                "service template must include a standalone contract project and source file");
        }

        private static ModManifest CreateManifest()
        {
            return new ModManifest
            {
                SchemaVersion = 4,
                Id = "tests.service-scaffold",
                Name = "Service scaffold",
                Author = new ModAuthor { Name = "TopiaForge" },
                Version = "1.0.0",
                EntryAssembly = EntryAssembly,
                EntryType = "TopiaForge.ValidTestMod.ValidMod",
                SupportedGameVersionRange = "0.0.2227",
                SupportedLoaderVersionRange = ">=1.0.0 <2.0.0",
                SupportedSdkVersionRange = ">=1.0.0 <2.0.0",
                ApiAssemblies = { ApiAssembly }
            };
        }

        private static void WritePackage(string path, ModManifest manifest)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry("topiaforge.mod.json");
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write(JsonUtil.Serialize(manifest));
                }

                AddAssembly(archive, EntryAssembly, typeof(ValidMod).Assembly.Location);
                AddAssembly(archive, ApiAssembly, typeof(IValidTestService).Assembly.Location);
            }
        }

        private static void AddAssembly(ZipArchive archive, string name, string source)
        {
            var entry = archive.CreateEntry(name);
            using (var input = File.OpenRead(source))
            using (var output = entry.Open())
            {
                input.CopyTo(output);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Service scaffold runtime test: " + message);
            }
        }
    }
}
