using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using RuntimeUnderTest = TopiaForge.ModManager.ModRuntime;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private const string FixtureAssembly = "TopiaForge.ValidTestMod.dll";

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "TopiaForgeModRuntimeTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                TestNormalLifecycleAndSubscriberIsolation(root);
                TestModEventDispatch(root);
                TestInitialSceneReplayAndDeduplication(root);
                TestInitialBackgroundSceneReplay(root);
                TestDetailedSceneLoadDelivery(root);
                TestCompleteSceneLifecycleDelivery(root);
                TestOwnerHarmonyLeaseCleanup();
                TestNativeInitialSceneRaceIsDeduplicated(root);
                TestInvalidInitialSceneWaitsForNativeDelivery(root);
                TestPartialLoadFailureCleanup(root);
                TestUnloadFailureStillCleans(root);
                TestRequiredDependencyRuntimeFailure(root);
                TestOptionalDependencyRuntimeFailureDoesNotBlock(root);
                TestConstructorFailure(root);
                TestRuntimeCompatibilityDefense(root);
                TestReceiptRecheckedImmediatelyBeforeLoad(root);
                TestResolverLifetime(root);
                Console.WriteLine("ModRuntime synthetic-assembly integration tests passed.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable("TOPIAFORGE_RUNTIME_TEST_TRACE", null);
                TryDelete(root);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CreateUnloadedRuntimeReference(string root)
        {
            var fixture = NewFixture(root, "resolver-lifetime", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            var runtime = fixture.CreateRuntimeInstance();
            var reference = new WeakReference(runtime);
            runtime.UnloadAll();
            return reference;
        }

        private static Fixture NewFixture(string root, string name, string entryType)
        {
            var testRoot = Path.Combine(root, name);
            var packagePath = Path.Combine(testRoot, "package");
            Directory.CreateDirectory(packagePath);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, FixtureAssembly),
                Path.Combine(packagePath, FixtureAssembly),
                true);
            var tracePath = Path.Combine(testRoot, "trace.txt");
            Environment.SetEnvironmentVariable("TOPIAFORGE_RUNTIME_TEST_TRACE", tracePath);
            var manifest = new ModManifest
            {
                SchemaVersion = 5,
                Id = "tests." + name,
                Name = "Runtime " + name,
                Version = "1.0.0",
                EntryAssembly = FixtureAssembly,
                EntryType = entryType,
                SupportedGameVersionRange = "*",
                SupportedLoaderVersionRange = ">=0.1.0-rc.1 <0.2.0",
                SupportedSdkVersionRange = ">=0.1.0-rc.1 <0.2.0"
            };
            JsonUtil.SaveFile(
                Path.Combine(packagePath, PackageInstallReceipt.FileName),
                PackageInstallReceipt.Create(
                    Path.Combine(packagePath, FixtureAssembly),
                    packagePath,
                    manifest));
            var package = new ModPackage(
                packagePath,
                manifest,
                new InstalledModState
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    Enabled = true
                },
                Array.Empty<string>());
            return new Fixture(
                new ManagerPaths(testRoot),
                manifest,
                package,
                tracePath,
                new CapturedRuntimeLogger(),
                new CapturedLoadObserver(),
                new FakeGameplayHost());
        }

        private static void AssertTrace(string path, params string[] expected)
        {
            var actual = File.Exists(path)
                ? File.ReadAllLines(path).Where(line => line.Length != 0).ToArray()
                : Array.Empty<string>();
            Assert(actual.SequenceEqual(expected),
                "expected trace [" + string.Join(",", expected) + "] but got [" + string.Join(",", actual) + "]");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("ModRuntime integration test failed: " + message);
        }

        private static void TryDelete(string path)
        {
            try { Directory.Delete(path, true); }
            catch { }
        }

        private sealed class Fixture
        {
            public Fixture(
                ManagerPaths paths,
                ModManifest manifest,
                ModPackage package,
                string tracePath,
                CapturedRuntimeLogger logger,
                CapturedLoadObserver observer,
                FakeGameplayHost gameplayHost)
            {
                Paths = paths;
                Manifest = manifest;
                Package = package;
                TracePath = tracePath;
                Logger = logger;
                Observer = observer;
                GameplayHost = gameplayHost;
            }

            public ManagerPaths Paths { get; }
            public ModManifest Manifest { get; }
            public ModPackage Package { get; }
            public string TracePath { get; }
            public CapturedRuntimeLogger Logger { get; }
            public CapturedLoadObserver Observer { get; }
            public FakeGameplayHost GameplayHost { get; }

            public RuntimeLease CreateRuntime() => new RuntimeLease(CreateRuntimeInstance());

            public RuntimeUnderTest CreateRuntimeInstance() =>
                new RuntimeUnderTest(
                    Paths,
                    Logger,
                    new ManifestValidationContext(
                        "0.0.2309",
                        "1.0.0",
                        "1.0.0",
                        platform: "windows",
                        architecture: "x64",
                        contentTargets: new[] { "code", "standalonewindows64" },
                        enforceRuntimeCompatibility: true),
                    Observer,
                    GameplayHost);
        }

        private sealed class RuntimeLease : IDisposable
        {
            private RuntimeUnderTest? runtime;

            public RuntimeLease(RuntimeUnderTest runtime) => this.runtime = runtime;

            public static implicit operator RuntimeUnderTest(RuntimeLease lease) => lease.runtime!;

            public void Load(IEnumerable<ModPackage> packages) => runtime!.Load(packages);
            public bool IsLoaded(string id) => runtime!.IsLoaded(id);
            public string? GetLoadFailure(string id) => runtime!.GetLoadFailure(id);
            public void DispatchUpdate(float deltaTime) => runtime!.DispatchUpdate(deltaTime);
            public bool DispatchInitialScene(int sceneHandle, string sceneName, bool isValid) =>
                runtime!.DispatchInitialScene(sceneHandle, sceneName, isValid);
            public bool DispatchInitialScenes(params RuntimeUnderTest.InitialSceneReplay[] scenes) =>
                runtime!.DispatchInitialScenes(scenes);
            public bool DispatchSceneLoaded(int sceneHandle, string sceneName, bool isValid) =>
                runtime!.DispatchSceneLoaded(sceneHandle, sceneName, isValid);
            public bool DispatchSceneLoaded(
                int sceneHandle,
                string sceneName,
                bool isValid,
                SceneLoadMode mode,
                bool isActive) =>
                runtime!.DispatchSceneLoaded(sceneHandle, sceneName, isValid, mode, isActive);
            public bool DispatchSceneActivated(
                int sceneHandle,
                string sceneName,
                bool isValid,
                SceneLoadMode mode) =>
                runtime!.DispatchSceneActivated(sceneHandle, sceneName, isValid, mode);
            public bool DispatchSceneLifecycleActivated(
                int sceneHandle,
                string sceneName,
                bool isValid,
                SceneLoadMode mode) =>
                runtime!.DispatchSceneLifecycleActivated(sceneHandle, sceneName, isValid, mode);
            public bool DispatchSceneUnloaded(
                int sceneHandle,
                string sceneName,
                bool isValid,
                SceneLoadMode mode) =>
                runtime!.DispatchSceneUnloaded(sceneHandle, sceneName, isValid, mode);
            public void UnloadAll() => runtime!.UnloadAll();

            public void Dispose()
            {
                var value = runtime;
                runtime = null;
                if (value != null)
                {
                    value.UnloadAll();
                }
            }
        }

        private sealed class CapturedRuntimeLogger : IModRuntimeLogger
        {
            public List<string> Errors { get; } = new List<string>();

            public IModLogger ForMod(string modId) => new CapturedModLogger(this, modId);
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) => Errors.Add(message);
            public void Error(Exception exception, string message) => Errors.Add(message + ": " + exception.Message);

            private sealed class CapturedModLogger : IModLogger
            {
                private readonly CapturedRuntimeLogger owner;
                private readonly string modId;

                public CapturedModLogger(CapturedRuntimeLogger owner, string modId)
                {
                    this.owner = owner;
                    this.modId = modId;
                }

                public void Debug(string message) { }
                public void Info(string message) { }
                public void Warn(string message) { }
                public void Error(string message) => owner.Errors.Add("[" + modId + "] " + message);
                public void Error(Exception exception, string message) =>
                    owner.Errors.Add("[" + modId + "] " + message + ": " + exception.Message);
            }
        }

        private sealed class CapturedLoadObserver : IModLoadObserver
        {
            public List<string> Events { get; } = new List<string>();
            public void OnLoading(string modId) => Events.Add("loading:" + modId);
            public void OnLoadCompleted(string modId, bool succeeded) =>
                Events.Add((succeeded ? "loaded:" : "failed:") + modId);
        }

        private sealed class FakeGameplayHost : IRuntimeGameplayHost
        {
            public event Action<GameTimeSample>? FixedUpdate
            {
                add { }
                remove { }
            }

            public event Action<GameTimeSample>? LateUpdate
            {
                add { }
                remove { }
            }
            public bool Disposed { get; private set; }

            public GameplayContextServices Create(
                string ownerModId,
                string packagePath,
                string dataPath,
                IModLifetime lifetime,
                IModLogger logger) => GameplayContextServices.Unavailable(lifetime);

            public GameTimeSample BeginFrame(float deltaTime) =>
                new GameTimeSample(GameLoopPhase.Frame, deltaTime, deltaTime, 0d, 0);

            public void Dispose()
            {
                Disposed = true;
            }
        }
    }
}
