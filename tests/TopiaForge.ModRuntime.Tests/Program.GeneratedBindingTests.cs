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
            foreach (var name in new[] { "tool-invocation", "gamemode", "world" })
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
            if (name == "tool-invocation")
            {
                TestGeneratedToolRejectsExecutableOverride();
                TestGeneratedToolArgumentsAndSdkGate();
                Console.WriteLine("Generated tool invocation tests passed.");
                return 0;
            }
            if (name != "gamemode" && name != "world") return 2;
            var repository = FindGeneratedRepository();
            var root = Directory.CreateTempSubdirectory("TopiaForgeGeneratedBinding-").FullName;
            try
            {
                // These fixtures use only the generated local SDK feed and installed targeting packs.
                // Keep host NuGet feeds out; generated props still supply RestoreAdditionalProjectSources.
                File.WriteAllText(Path.Combine(root, "NuGet.Config"),
                    "<configuration><packageSources><clear /></packageSources></configuration>");
                var output = Path.Combine(root, "generated");
                Assert(RunGeneratedTool(GeneratedTool.Dart, repository, "--version").Contains("Dart SDK version: 3.12.2 ", StringComparison.Ordinal),
                    "generated acceptance requires exactly Dart 3.12.2");
                var packageConfig = Path.Combine(repository, "apps", "topiaforge_cli", ".dart_tool", "package_config.json");
                Assert(File.Exists(packageConfig), "run pinned Dart pub get --enforce-lockfile in apps/topiaforge_cli before C# verification");
                RunGeneratedTool(GeneratedTool.Dart, repository, "--packages=" + packageConfig,
                    Path.Combine(repository, "tests", "TopiaForge.ModRuntime.Tests", "generate_template_packages.dart"), repository, output, name);
                using var generated = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "generated.json")));
                var item = generated.RootElement[0];
                var project = item.GetProperty("project").GetString()!;
                var assembly = item.GetProperty("assembly").GetString()!;
                RunGeneratedTool(GeneratedTool.Dotnet, repository, "build", Path.Combine(project, Path.ChangeExtension(assembly, ".csproj")), "-c", "Release", "--nologo");
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
        private static void TestGeneratedToolRejectsExecutableOverride()
        {
            var root = Directory.CreateTempSubdirectory("TopiaForgeGeneratedTool-").FullName;
            var previous = Environment.GetEnvironmentVariable("TOPIAFORGE_TEST_DART");
            try
            {
                var unexpected = Path.Combine(root, "unexpected dart.exe");
                File.WriteAllText(unexpected, "An existing arbitrary executable must never be admitted.");
                Environment.SetEnvironmentVariable("TOPIAFORGE_TEST_DART", unexpected);
                var rejected = false;
                try { GeneratedDart(); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "an existing absolute executable override must be rejected before any process can start");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TOPIAFORGE_TEST_DART", previous);
                TryDelete(root);
            }
        }
        private static void TestGeneratedToolArgumentsAndSdkGate()
        {
            var root = Directory.CreateTempSubdirectory("TopiaForgeGeneratedSdk-").FullName;
            try
            {
                var arguments = new[] { "build", "a project;echo unsafe.csproj", "literal\"argument" };
                var command = CreateGeneratedToolStart(GeneratedTool.Dotnet, root, arguments);
                Assert(command.FileName == "dotnet" && !command.UseShellExecute && command.CreateNoWindow
                    && command.Arguments.Length == 0 && command.ArgumentList.SequenceEqual(arguments),
                    "fixed tools keep each operand distinct without shell or combined argument parsing");
                var rejected = false;
                try { CreateGeneratedToolStart((GeneratedTool)99, root, arguments); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "unknown tool identities never become executable names");

                var cache = Path.Combine(root, ".fvm", "flutter_sdk", "bin", "cache");
                var sdk = Path.Combine(cache, "dart-sdk");
                Directory.CreateDirectory(Path.Combine(sdk, "bin"));
                var expected = Path.Combine(sdk, "bin", "dart.exe");
                File.WriteAllText(expected, "Metadata fixture only: never executed.");
                File.WriteAllText(Path.Combine(cache, "flutter.version.json"),
                    "{\"frameworkVersion\":\"3.44.6\",\"dartSdkVersion\":\"3.12.2\"}");
                foreach (var version in new[] { "", "3.12.1", "3.12.2 extra" })
                {
                    File.WriteAllText(Path.Combine(sdk, "version"), version);
                    rejected = false;
                    try { ValidateGeneratedWindowsSdk(root); }
                    catch (InvalidOperationException) { rejected = true; }
                    Assert(rejected, "SDK version metadata must be exact before any process can start");
                }
                File.WriteAllText(Path.Combine(sdk, "version"), "3.12.2\n");
                Assert(ValidateGeneratedWindowsSdk(root) == expected,
                    "the Windows executable is fixed inside the repository's pinned FVM SDK");
                File.WriteAllText(Path.Combine(cache, "flutter.version.json"),
                    "{\"frameworkVersion\":\"3.44.5\",\"dartSdkVersion\":\"3.12.2\"}");
                rejected = false;
                try { ValidateGeneratedWindowsSdk(root); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "a different Flutter SDK cannot be admitted by matching only Dart metadata");
            }
            finally { TryDelete(root); }
        }

        private enum GeneratedTool { Dart, Dotnet }

        private static string GeneratedDart()
        {
            Assert(Environment.GetEnvironmentVariable("TOPIAFORGE_TEST_DART") == null,
                "TOPIAFORGE_TEST_DART is retired; select the pinned SDK with the repository's .fvm/flutter_sdk link");
            return OperatingSystem.IsWindows() ? ValidateGeneratedWindowsSdk(FindGeneratedRepository()) : "dart";
        }

        private static string ValidateGeneratedWindowsSdk(string repository)
        {
            var cache = Path.Combine(repository, ".fvm", "flutter_sdk", "bin", "cache");
            var sdk = Path.Combine(cache, "dart-sdk");
            var executable = Path.Combine(sdk, "bin", "dart.exe");
            var version = Path.Combine(sdk, "version");
            var flutterVersion = Path.Combine(cache, "flutter.version.json");
            Assert(File.Exists(executable) && File.Exists(version) && File.Exists(flutterVersion),
                "select Flutter 3.44.6 using the repository's .fvm/flutter_sdk link before C# verification");
            Assert(File.ReadAllText(version).Trim() == "3.12.2", "generated acceptance requires exactly Dart 3.12.2 metadata");
            using var flutter = JsonDocument.Parse(File.ReadAllText(flutterVersion));
            Assert(flutter.RootElement.GetProperty("frameworkVersion").GetString() == "3.44.6"
                && flutter.RootElement.GetProperty("dartSdkVersion").GetString() == "3.12.2",
                "generated acceptance requires the pinned Flutter 3.44.6 SDK with Dart 3.12.2");
            return executable;
        }

        private static ProcessStartInfo CreateGeneratedToolStart(GeneratedTool tool, string workingDirectory, params string[] arguments)
        {
            var start = tool switch
            {
                GeneratedTool.Dart => new ProcessStartInfo(GeneratedDart()),
                GeneratedTool.Dotnet => new ProcessStartInfo("dotnet"),
                _ => throw new InvalidOperationException("Unknown generated acceptance tool.")
            };
            start.WorkingDirectory = workingDirectory;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            return start;
        }

        private static string RunGeneratedTool(GeneratedTool tool, string workingDirectory, params string[] arguments) =>
            RunGeneratedProcess(CreateGeneratedToolStart(tool, workingDirectory, arguments), tool.ToString(), 180000);
        private static string RunGeneratedProcess(ProcessStartInfo start, string label, int timeout)
        {
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start " + label);
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(timeout);
            if (!exited) { process.Kill(true); process.WaitForExit(); }
            var transcript = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            if (!exited)
                throw new InvalidOperationException(label + " timed out after " + timeout + " ms: "
                    + start.FileName + " " + string.Join(" ", start.ArgumentList.Select(argument => JsonSerializer.Serialize(argument)))
                    + Environment.NewLine + transcript);
            Assert(process.ExitCode == 0, label + " failed: " + transcript);
            return transcript;
        }
    }
}
