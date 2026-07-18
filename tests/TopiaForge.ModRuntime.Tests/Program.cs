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
    internal static class Program
    {
        private const string FixtureAssembly = "TopiaForge.ValidTestMod.dll";

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "TopiaForgeModRuntimeTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                TestNormalLifecycleAndSubscriberIsolation(root);
                TestInitialSceneReplayAndDeduplication(root);
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

        private static void TestNormalLifecycleAndSubscriberIsolation(string root)
        {
            var fixture = NewFixture(root, "normal", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });
            Assert(runtime.IsLoaded(fixture.Manifest.Id), "a valid synthetic assembly should load");
            runtime.DispatchUpdate(1f / 60f);
            runtime.UnloadAll();

            AssertTrace(fixture.TracePath, "load", "update-after-failure", "unload", "cleanup-second", "cleanup-first");
            Assert(fixture.Logger.Errors.Count == 1
                && fixture.Logger.Errors[0].Contains("subscriber", StringComparison.OrdinalIgnoreCase),
                "one throwing event subscriber must be attributed without blocking later subscribers");
            Assert(fixture.Observer.Events.SequenceEqual(new[] { "loading:" + fixture.Manifest.Id, "loaded:" + fixture.Manifest.Id }),
                "the startup observer should bracket the successful load callback");
            Assert(fixture.GameplayHost.Disposed, "runtime shutdown should release the manager-owned gameplay host");
        }

        private static void TestPartialLoadFailureCleanup(string root)
        {
            var fixture = NewFixture(root, "load-failure", "TopiaForge.ValidTestMod.RuntimeFailingLoadMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id)
                && runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains("synthetic load failure", StringComparison.Ordinal) == true,
                "a throwing OnLoad must remain failed and observable");
            AssertTrace(fixture.TracePath, "load", "unload", "cleanup");
            Assert(fixture.Observer.Events.SequenceEqual(new[] { "loading:" + fixture.Manifest.Id, "failed:" + fixture.Manifest.Id }),
                "the startup observer should close a failed callback boundary");
            runtime.UnloadAll();
        }

        private static void TestInitialSceneReplayAndDeduplication(string root)
        {
            var fixture = NewFixture(root, "initial-scene", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(runtime.DispatchInitialScene(41, "Startup", isValid: true),
                "a valid active scene should be replayed immediately after mod loading");
            Assert(!runtime.DispatchSceneLoaded(41, "Startup", isValid: true),
                "the native callback for the replayed scene handle should be suppressed once");
            Assert(runtime.DispatchSceneLoaded(41, "Startup", isValid: true),
                "a later real callback must not be suppressed merely because Unity reused a scene handle");
            Assert(!runtime.DispatchSceneLoaded(42, "   ", isValid: true)
                   && !runtime.DispatchSceneLoaded(42, "Gameplay", isValid: false),
                "invalid or unnamed Unity scenes must not reach mods");
            Assert(runtime.DispatchSceneLoaded(42, "Gameplay", isValid: true),
                "the next valid loaded scene should be delivered");
            runtime.UnloadAll();

            AssertTrace(
                fixture.TracePath,
                "load",
                "scene:Startup",
                "scene:Startup",
                "scene:Gameplay",
                "unload",
                "cleanup-second",
                "cleanup-first");
        }

        private static void TestNativeInitialSceneRaceIsDeduplicated(string root)
        {
            var fixture = NewFixture(root, "initial-scene-race", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(runtime.DispatchSceneLoaded(51, "Startup", isValid: true),
                "a native scene callback that wins the startup race should be delivered");
            Assert(!runtime.DispatchInitialScene(51, "Startup", isValid: true),
                "the explicit active-scene replay should recognize an already-delivered native callback");
            runtime.UnloadAll();

            AssertTrace(
                fixture.TracePath,
                "load",
                "scene:Startup",
                "unload",
                "cleanup-second",
                "cleanup-first");
        }

        private static void TestInvalidInitialSceneWaitsForNativeDelivery(string root)
        {
            var fixture = NewFixture(root, "invalid-initial-scene", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.DispatchInitialScene(0, string.Empty, isValid: false),
                "an invalid active scene must not be synthesized for mods");
            Assert(runtime.DispatchSceneLoaded(61, "Startup", isValid: true),
                "a valid native scene callback should still deliver after an invalid initial snapshot");
            runtime.UnloadAll();

            AssertTrace(
                fixture.TracePath,
                "load",
                "scene:Startup",
                "unload",
                "cleanup-second",
                "cleanup-first");
        }

        private static void TestUnloadFailureStillCleans(string root)
        {
            var fixture = NewFixture(root, "unload-failure", "TopiaForge.ValidTestMod.RuntimeFailingUnloadMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });
            runtime.UnloadAll();

            AssertTrace(fixture.TracePath, "load", "unload", "cleanup");
            Assert(fixture.Logger.Errors.Any(error => error.Contains("OnUnload", StringComparison.Ordinal)),
                "unload exceptions should be attributed after lifetime cleanup continues");
        }

        private static void TestRequiredDependencyRuntimeFailure(string root)
        {
            var fixture = NewFixture(root, "dependency-failure", "TopiaForge.ValidTestMod.RuntimeDependentMod");
            fixture.Manifest.Dependencies.Add("tests.missing-provider", ">=1.0.0 <2.0.0");
            var invalidProvider = ModPackage.Invalid(
                Path.Combine(root, "dependency-failure", "tests.missing-provider"),
                "synthetic corrupt provider");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { invalidProvider, fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id)
                && runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains("required dependency", StringComparison.Ordinal) == true,
                "runtime failure of a required dependency must block its consumer");
            AssertTrace(fixture.TracePath);
            runtime.UnloadAll();
        }

        private static void TestOptionalDependencyRuntimeFailureDoesNotBlock(string root)
        {
            var fixture = NewFixture(root, "optional-dependency-failure", "TopiaForge.ValidTestMod.RuntimeDependentMod");
            fixture.Manifest.OptionalDependencies.Add("tests.optional-provider", ">=1.0.0 <2.0.0");
            var invalidProvider = ModPackage.Invalid(
                Path.Combine(root, "optional-dependency-failure", "tests.optional-provider"),
                "synthetic corrupt optional provider");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { invalidProvider, fixture.Package });

            Assert(runtime.IsLoaded(fixture.Manifest.Id),
                "a corrupt optional dependency must not block an otherwise healthy consumer");
            runtime.UnloadAll();
            AssertTrace(fixture.TracePath, "dependent-load");
        }

        private static void TestConstructorFailure(string root)
        {
            var fixture = NewFixture(root, "constructor-failure", "TopiaForge.ValidTestMod.RuntimeThrowingConstructorMod");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id)
                && runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains("constructor failure", StringComparison.Ordinal) == true,
                "constructor failures must not strand a loaded mod");
            AssertTrace(fixture.TracePath, "constructor");
            Assert(fixture.Observer.Events.Count == 0,
                "the startup journal boundary must not blame OnLoad when construction failed first");
            runtime.UnloadAll();
        }

        private static void TestRuntimeCompatibilityDefense(string root)
        {
            var fixture = NewFixture(root, "runtime-incompatible", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            fixture.Manifest.Platforms.Add("macos");
            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id) &&
                   runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains(
                       "host platform windows",
                       StringComparison.Ordinal) == true,
                "runtime loading must recheck compatibility even for a preconstructed valid package");
            AssertTrace(fixture.TracePath);
            Assert(fixture.Observer.Events.Count == 0,
                "runtime compatibility rejection must happen before assembly activation and OnLoad journaling");
            runtime.UnloadAll();
        }

        private static void TestReceiptRecheckedImmediatelyBeforeLoad(string root)
        {
            var fixture = NewFixture(root, "load-time-tamper", "TopiaForge.ValidTestMod.RuntimeSuccessMod");
            File.AppendAllText(
                Path.Combine(fixture.Package.PackagePath, FixtureAssembly),
                "changed-after-registry-scan");

            using var runtime = fixture.CreateRuntime();
            runtime.Load(new[] { fixture.Package });

            Assert(!runtime.IsLoaded(fixture.Manifest.Id)
                && runtime.GetLoadFailure(fixture.Manifest.Id)?.Contains(
                    "package integrity changed before load",
                    StringComparison.Ordinal) == true,
                "the runtime must reverify installed bytes at the last safe point before Assembly.LoadFrom");
            AssertTrace(fixture.TracePath);
            Assert(fixture.Observer.Events.Count == 0,
                "receipt rejection must occur before activation and the OnLoad startup-journal boundary");
            runtime.UnloadAll();
        }

        private static void TestResolverLifetime(string root)
        {
            var reference = CreateUnloadedRuntimeReference(root);
            for (var attempt = 0; attempt < 3 && reference.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert(!reference.IsAlive,
                "UnloadAll must detach the AppDomain assembly resolver and release the runtime instance");
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
                SchemaVersion = 4,
                Id = "tests." + name,
                Name = "Runtime " + name,
                Version = "1.0.0",
                EntryAssembly = FixtureAssembly,
                EntryType = entryType,
                SupportedGameVersionRange = "*",
                SupportedLoaderVersionRange = ">=1.0.0 <2.0.0",
                SupportedSdkVersionRange = ">=1.0.0 <2.0.0"
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
                        "0.0.2227",
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
            public bool DispatchSceneLoaded(int sceneHandle, string sceneName, bool isValid) =>
                runtime!.DispatchSceneLoaded(sceneHandle, sceneName, isValid);
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
