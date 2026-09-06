using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private const string GeneratedWorldsId = "io.github.furroxide.topiaforge.worlds";
        private const string GeneratedDependencyAssembly = "TopiaForge.GeneratedWorldsTestMod.dll";

        private static void TestGeneratedPackagesInFreshProcesses()
        {
            foreach (var name in new[] { "gamemode", "world" })
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(Program).Assembly.Location);
                start.ArgumentList.Add("--generated-binding-case");
                start.ArgumentList.Add(name);
                RunGeneratedProcess(start, "generated " + name, 300000);
            }
        }

        private static int RunGeneratedBindingCase(string name)
        {
            if (name != "gamemode" && name != "world") return 2;
            var repository = FindGeneratedRepository();
            var root = Directory.CreateTempSubdirectory("TopiaForgeGeneratedBinding-").FullName;
            try
            {
                var output = Path.Combine(root, "generated");
                var dart = GeneratedDart();
                Assert(RunGeneratedTool(dart, repository, "--version").Contains("Dart SDK version: 3.12.2 ", StringComparison.Ordinal),
                    "generated acceptance requires exactly Dart 3.12.2");
                var packageConfig = Path.Combine(repository, "apps", "topiaforge_cli", ".dart_tool", "package_config.json");
                Assert(File.Exists(packageConfig), "run pinned Dart pub get --enforce-lockfile in apps/topiaforge_cli before C# verification");
                RunGeneratedTool(dart, repository, "--packages=" + packageConfig,
                    Path.Combine(repository, "tests", "TopiaForge.ModRuntime.Tests", "generate_template_packages.dart"), repository, output, name);
                using var generated = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "generated.json")));
                var item = generated.RootElement[0];
                var project = item.GetProperty("project").GetString()!;
                var assembly = item.GetProperty("assembly").GetString()!;
                RunGeneratedTool("dotnet", repository, "build", Path.Combine(project, Path.ChangeExtension(assembly, ".csproj")), "-c", "Release", "--nologo");
                foreach (var source in item.GetProperty("sources").EnumerateObject())
                    Assert(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(project, source.Name)))) == source.Value.GetString(),
                        "building must not rewrite generated source or manifest: " + source.Name);
                TestInstalledGeneratedPackage(repository, root, project, assembly, name);
                Console.WriteLine("Generated production binding passed: " + name);
                return 0;
            }
            finally { TryDelete(root); }
        }

        private static void TestInstalledGeneratedPackage(string repository, string root, string project, string assembly, string name)
        {
            var paths = new ManagerPaths(Path.Combine(root, "runtime"));
            var state = new ManagerState();
            var validation = new ManifestValidationContext("0.0.2409", "0.1.0-rc.1", "0.1.0-rc.1",
                platform: "windows", architecture: "x64", contentTargets: new[] { "code", "standalonewindows64" }, enforceRuntimeCompatibility: true);
            var fixtureManifest = GeneratedWorldsManifest();
            InstallGeneratedArchive(Path.Combine(root, "dependency.topiaforgemod"), paths, state, validation,
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 6,
                    name = fixtureManifest.Id,
                    displayName = fixtureManifest.Name,
                    version = fixtureManifest.Version,
                    author = new { name = fixtureManifest.Author.Name },
                    capabilities = new[] { "world-service", "scene-management" },
                    entryAssembly = fixtureManifest.EntryAssembly,
                    entryType = fixtureManifest.EntryType,
                    supportedGameVersionRange = fixtureManifest.SupportedGameVersionRange,
                    supportedLoaderVersionRange = fixtureManifest.SupportedLoaderVersionRange,
                    supportedSdkVersionRange = fixtureManifest.SupportedSdkVersionRange,
                    contributions = JsonSerializer.Deserialize<JsonElement>(JsonUtil.Serialize(fixtureManifest.Contributions))
                }),
                new Dictionary<string, string> { [GeneratedDependencyAssembly] = Path.Combine(AppContext.BaseDirectory, GeneratedDependencyAssembly) });
            var manifestBytes = File.ReadAllBytes(Path.Combine(project, "topiaforge.mod.json"));
            var manifest = ModManifestJson.Deserialize(System.Text.Encoding.UTF8.GetString(manifestBytes));
            var files = new Dictionary<string, string>
            {
                [assembly] = Path.Combine(project, "bin", "Release", "netstandard2.1", assembly),
                ["LICENSE.md"] = Path.Combine(project, "LICENSE.md")
            };
            if (name == "world")
                files[manifest.Contributions!.Worlds.Single().Content!.Bundle!] = Path.Combine(repository,
                    "mods", "TopiaForge.Worlds", "AssetBundles", "topiaforge-representative-world.bundle");
            InstallGeneratedArchive(Path.Combine(root, "generated.topiaforgemod"), paths, state, validation, manifestBytes, files);
            var packages = new ModRegistry().Scan(paths, state, validation);
            Assert(packages.Count == 2 && packages.All(package => package.IsValid), "both real installed packages pass receipt/manifest validation");
            var installed = packages.Single(package => package.Manifest!.Id == manifest.Id);
            Assert(File.ReadAllBytes(Path.Combine(installed.PackagePath, "topiaforge.mod.json")).SequenceEqual(manifestBytes),
                "the installed manifest is exactly the Dart scaffolder output");
            Assert(packages.All(package => File.Exists(Path.Combine(package.PackagePath, PackageInstallReceipt.FileName))), "installer receipts exist for both packages");
            var profile = new EffectiveProfile("generated", 1, packages.Select(package => new ResolvedPackage(package.Manifest!.Id, package.Manifest.Version, package.Manifest)).ToArray(),
                new InstallFacts("windows", "x64", "standalonewindows64", "0.0.2409"));
            var host = new GeneratedGameplayHost();
            var logger = new CapturedRuntimeLogger();
            var runtime = new TopiaForge.ModManager.ModRuntime(paths, logger, validation, new CapturedLoadObserver(), host);
            var orchestrator = runtime.ActivateSessionRuntime(profile);
            try
            {
                runtime.Load(packages);
                Assert(runtime.LoadedModIds.Count == 2, "unchanged generated entry and declared dependency both load: " + string.Join("; ", logger.Errors));
                var snapshot = runtime.CaptureSessionRuntime();
                Assert(snapshot.BindingFailures.Count == 0, "production declaration binding succeeds: " + string.Join("; ", snapshot.BindingFailures.Select(failure => failure.Message)));
                Assert(snapshot.Profile.Packages.All(package => package.Identity.Id != "io.github.furroxide.topiaforge.sandbox"), "generated world Free Play requires no Sandbox package");
                var target = manifest.Contributions!.LaunchTargets.Single();
                var resolution = runtime.ResolveLaunch(new LaunchRequest(target.Id));
                Assert(resolution.Resolved, "loaded-manifest resolution accepts the generated target");
                var launch = runtime.LaunchTargetAsync(new LaunchRequest(target.Id), "generated-" + name);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (host.PendingReadiness == null && !launch.IsCompleted && DateTime.UtcNow < deadline)
                { runtime.NativeDispatcher.Drain(); Thread.Sleep(1); }
                Assert(host.PendingReadiness != null && !launch.IsCompleted && orchestrator.Current.Phase == SessionPhase.LoadingWorld,
                    "world readiness holds generated gameplay startup in LoadingWorld");
                Assert(host.NativeDispatches == 1, "native preparation uses the one runtime transition executor");
                host.CompleteReadiness();
                PumpBinding(runtime.NativeDispatcher, launch);
                Assert(launch.Result.Succeeded && orchestrator.Current.Phase == SessionPhase.Running,
                    "production-generated launch reaches Running: " + launch.Result.ErrorMessage);
                Assert(host.LastSpawnPolicy?.Kind == (name == "world" ? WorldSpawnKind.AuthoredMarker : WorldSpawnKind.ProviderDefault),
                    "generated spawn policy reaches native preparation");
                if (name == "world")
                    Assert(host.LastSpawnPolicy!.MarkerName == "SpawnPoint" && host.BundleLoads == 1 && host.PrefabLoads == 1 && host.Spawns == 1,
                        "generated bundle and exact authored marker pass through the production built-in provider");
                var owners = snapshot.Contexts.Values.ToArray();
                var stopped = ((IWorldSessionService)orchestrator).Current.Session!.StopAsync();
                PumpBinding(runtime.NativeDispatcher, stopped);
                Assert(stopped.Result.Succeeded && orchestrator.Current.Phase == SessionPhase.Idle
                    && host.ActiveResources == 0 && owners.All(owner => !owner.Lifetime.IsStopping && owner.ActiveChildScopeCount == 0)
                    && runtime.LoadedModIds.Count == 2,
                    "session-bound stop releases generated resources while both package parents remain alive");
                PumpBinding(runtime.NativeDispatcher, runtime.UnloadAllAsync());
                Assert(host.ActiveResources == 0 && host.Disposed && owners.All(owner => owner.ActiveChildScopeCount == 0),
                    "runtime unload closes every generated child and native/asset resource");
                Assert(logger.Errors.Count == 0, "generated runtime has no hidden cleanup errors: " + string.Join("; ", logger.Errors));
            }
            finally { PumpBinding(runtime.NativeDispatcher, runtime.UnloadAllAsync()); }
        }

        private static ModManifest GeneratedWorldsManifest() => new ModManifest
        {
            SchemaVersion = 6,
            Id = GeneratedWorldsId,
            Name = "Declared engine fixture",
            Author = new ModAuthor { Name = "TopiaForge Tests" },
            Version = "0.1.0-rc.1",
            EntryAssembly = GeneratedDependencyAssembly,
            EntryType = "TopiaForge.GeneratedWorldsTestMod.Entry",
            SupportedGameVersionRange = "*",
            SupportedLoaderVersionRange = ">=0.1.0-rc.1 <0.2.0",
            SupportedSdkVersionRange = ">=0.1.0-rc.1 <0.2.0",
            Contributions = new ModContributions
            {
                Worlds = new List<ModWorldDeclaration> { new ModWorldDeclaration { Id = GeneratedWorldsId + ".open_sandbox", Name = "Engine fixture",
                    Content = new ModWorldContent { Kind = ModWorldContent.GameSceneKind, SceneName = "GeneratedScene" },
                    Transitions = new List<string> { ModTransitions.AdditiveArena }, Spawn = new ModSpawnPolicy { Kind = ModSpawnPolicy.ProviderDefaultKind }, OpenToAnyCompatible = true } },
                Gamemodes = new List<ModGamemodeDeclaration> { new ModGamemodeDeclaration { Id = GeneratedWorldsId + ".freeplay", Name = "Free Play",
                    Implementation = new ModImplementationBinding { Type = "TopiaForge.Worlds.FreePlayGamemode" }, SceneChangePolicy = "end-session" } }
            }
        };

        private static void InstallGeneratedArchive(string archivePath, ManagerPaths paths, ManagerState state,
            ManifestValidationContext validation, byte[] manifest, IReadOnlyDictionary<string, string> files)
        {
            using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                using (var stream = zip.CreateEntry("topiaforge.mod.json").Open()) stream.Write(manifest);
                foreach (var file in files) zip.CreateEntryFromFile(file.Value, file.Key);
            }
            var result = new PackageInstaller().Install(archivePath, paths, state, false, validation);
            Assert(result.Ok, "generated archive installs: " + string.Join("; ", result.Errors));
        }

        private static string FindGeneratedRepository()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "TopiaForge.slnx"))) return directory.FullName;
            throw new InvalidOperationException("Generated package acceptance must run from this repository's build output.");
        }
        private static string GeneratedDart()
        {
            var configured = Environment.GetEnvironmentVariable("TOPIAFORGE_TEST_DART");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                Assert(Path.IsPathFullyQualified(configured) && File.Exists(configured), "TOPIAFORGE_TEST_DART must name an explicit existing SDK executable");
                return configured;
            }
            return OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "fvm", "versions", "3.44.6", "bin", "cache", "dart-sdk", "bin", "dart.exe") : "dart";
        }
        private static string RunGeneratedTool(string executable, string workingDirectory, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            return RunGeneratedProcess(start, executable, 180000);
        }
        private static string RunGeneratedProcess(ProcessStartInfo start, string label, int timeout)
        {
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + label);
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout)) { process.Kill(true); throw new InvalidOperationException(label + " timed out"); }
            var transcript = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            Assert(process.ExitCode == 0, label + " failed: " + transcript);
            return transcript;
        }
    }
}
