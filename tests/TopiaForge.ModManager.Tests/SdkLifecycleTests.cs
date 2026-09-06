using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class SdkLifecycleTests
    {
        public static void Run(string root)
        {
            OwnerFacadeStoppingTests.Run(root);
            TestSemanticVersion();
            TestOperationResult();
            TestSceneLifecycleEvent();
            TestLifetimeCleanup();
            TestBaseClassAndContext(Path.Combine(root, "sdk-lifecycle"));
            Console.WriteLine("SdkLifecycleTests passed.");
        }

        private static void TestSemanticVersion()
        {
            const string text = "999999999999999999999.2.3-rc.10+build.abc";
            Assert(SemanticVersion.TryParse(text, out var version), "complete SemVer should parse");
            Assert(version.ToString() == text, "SemVer must retain unbounded core, prerelease, and build metadata");
            Assert(version.Major == "999999999999999999999" && version.Prerelease == "rc.10",
                "SemVer identifiers should remain discoverable");
            Assert(SemanticVersion.Parse("1.0.0-alpha.2") < SemanticVersion.Parse("1.0.0-alpha.10"),
                "numeric prerelease identifiers should use numeric precedence");
            Assert(SemanticVersion.Parse("1.0.0") > SemanticVersion.Parse("0.1.0-rc.1"),
                "stable versions should sort after prereleases");
            Assert(SemanticVersion.Parse("1.0.0+one").CompareTo(SemanticVersion.Parse("1.0.0+two")) == 0,
                "build metadata should not affect precedence");
            Assert(SemanticVersion.Parse("1.0.0+one") != SemanticVersion.Parse("1.0.0+two"),
                "exact value equality should retain build metadata");
            Assert(!SemanticVersion.TryParse("1.0.0-01", out _),
                "numeric prerelease identifiers with leading zeroes should be rejected");
            Assert(default(SemanticVersion).ToString() == "0.0.0",
                "the default value should be safe and canonical");
        }

        private static void TestOperationResult()
        {
            var success = OperationResult<string>.Success("ready");
            Assert(success.Succeeded && success.ErrorCode == ModErrorCode.None &&
                   success.TryGetValue(out var value) && value == "ready",
                "successful operation results should expose their value");

            var failure = OperationResult<string>.Failure(ModErrorCode.NotFound, "missing");
            Assert(!failure.Succeeded && failure.Value == null && failure.ErrorCode == ModErrorCode.NotFound &&
                   failure.ErrorMessage == "missing" && !failure.TryGetValue(out _),
                "failed operation results should expose stable diagnostics");
            AssertThrows<ArgumentOutOfRangeException>(
                () => OperationResult<string>.Failure(ModErrorCode.None, "bad"),
                "failure without an error code should be rejected");
        }

        private static void TestSceneLifecycleEvent()
        {
            AssertThrows<ArgumentException>(
                () => new SceneLifecycleEvent(
                    1,
                    "Gameplay",
                    SceneLifecyclePhase.Activated,
                    SceneLoadMode.Single,
                    isActive: false),
                "an Activated lifecycle transition must reject inactive state");

            var initialBackground = new SceneLifecycleEvent(
                2,
                "Lighting",
                SceneLifecyclePhase.Loaded,
                SceneLoadMode.Additive,
                isActive: false,
                isInitial: true);
            Assert(initialBackground.IsInitial && !initialBackground.IsActive,
                "initial replay metadata should support already-loaded background scenes");

            // Robotopia 2409 handed Unity's Scene.handle back negative. The manager passes that
            // through verbatim, so rejecting the sign threw inside SceneManager.sceneLoaded and
            // no mod received a scene event at all. The id is an opaque correlation key.
            var negativeHandle = new SceneLifecycleEvent(
                -1877,
                "Gameplay",
                SceneLifecyclePhase.Loaded,
                SceneLoadMode.Single,
                isActive: true);
            Assert(negativeHandle.SceneInstanceId == -1877,
                "a negative host scene handle should be preserved, not rejected");

            var extremeHandle = new SceneLifecycleEvent(
                int.MinValue,
                "Gameplay",
                SceneLifecyclePhase.Loaded,
                SceneLoadMode.Single,
                isActive: true);
            Assert(extremeHandle.SceneInstanceId == int.MinValue,
                "no scene handle value should be treated as out of range");

            var detailedOnlyEvents = new DetailedOnlyModEvents();
            SceneLifecycleEvent? fallback = null;
            detailedOnlyEvents.SubscribeSceneLifecycle(scene => fallback = scene);
            detailedOnlyEvents.Raise(new SceneLoadEvent(
                "Lighting",
                SceneLoadMode.Additive,
                isActive: false));
            Assert(fallback != null
                && fallback.SceneInstanceId == 0
                && fallback.SceneName == "Lighting"
                && fallback.Phase == SceneLifecyclePhase.Loaded
                && fallback.Mode == SceneLoadMode.Additive
                && !fallback.IsActive,
                "lifecycle fallback should preserve detailed load mode and active metadata when available");
        }

        private static void TestLifetimeCleanup()
        {
            var order = new List<string>();
            var lifetime = new OwnerModLifetime();
            lifetime.StoppingToken.Register(() => order.Add("cancel"));
            lifetime.Defer(() => order.Add("first"));
            lifetime.Defer(() =>
            {
                order.Add("second");
                throw new InvalidOperationException("expected cleanup failure");
            });
            lifetime.Defer(() => order.Add("third"));

            AssertThrows<AggregateException>(() => lifetime.Dispose(),
                "lifetime should report cleanup failures after attempting every resource");
            Assert(string.Join(",", order) == "cancel,third,second,first",
                "lifetime should cancel first and dispose resources in reverse order");
            Assert(lifetime.IsStopping && lifetime.StoppingToken.IsCancellationRequested,
                "lifetime should remain observably stopped");
            lifetime.Dispose();
            Assert(order.Count == 4, "lifetime cleanup should be idempotent");

            var earlyCount = 0;
            var earlyLifetime = new OwnerModLifetime();
            var lease = earlyLifetime.Defer(() => earlyCount++);
            lease.Dispose();
            lease.Dispose();
            earlyLifetime.Dispose();
            Assert(earlyCount == 1, "an early-release lease should dispose its resource exactly once");

            var rejectedResource = new CountingDisposable();
            AssertThrows<ObjectDisposedException>(() => earlyLifetime.Track(rejectedResource),
                "tracking after shutdown should fail rather than leak a resource");
            Assert(rejectedResource.DisposeCount == 1,
                "a resource rejected after shutdown should still be disposed exactly once");
        }

        private static void TestBaseClassAndContext(string root)
        {
            Directory.CreateDirectory(root);
            var paths = new ManagerPaths(root);
            paths.EnsureCreated();
            var logger = new CapturedLogger();
            var context = new ModContext(
                new ModManifest
                {
                    SchemaVersion = 5,
                    Id = "example.lifecycle",
                    Name = "Lifecycle Example",
                    Version = "1.2.3-beta.1+test",
                    EntryAssembly = "Example.dll",
                    EntryType = "Example.Mod"
                },
                paths,
                Path.Combine(root, "package"),
                logger,
                new ModServiceRegistry(),
                new RuntimeInfo("0.0.2309"));

            Assert(context.Identity.Id == "example.lifecycle" &&
                   context.Identity.Version.ToString() == "1.2.3-beta.1+test",
                "context identity should preserve complete manifest identity");
            Assert(context.Runtime.TryGetGameVersion(out var gameVersion) &&
                   gameVersion.ToString() == "0.0.2309" &&
                   context.Runtime.RuntimeIdentifier.Contains("-", StringComparison.Ordinal),
                "context should expose real runtime metadata");
            Assert(!context.LocalPlayer.TryGetSnapshot(out _)
                && context.LocalPlayer.AcquireControl("headless probe").ErrorCode == ModErrorCode.Unavailable
                && context.Ui.ShowToast("headless probe").ErrorCode == ModErrorCode.Unavailable,
                "hosts without gameplay presentation should expose unavailable local facades, not fabricate a player");
            var fileWrite = context.Files.WriteDataTextAsync("nested/value.txt", "owned").GetAwaiter().GetResult();
            var storageWrite = context.LocalStorage.Save("nested/value", new StoredValue { Value = "owned" });
            Assert(fileWrite.Succeeded
                && context.Files.DataFileExists("nested/value.txt")
                && storageWrite.Succeeded
                && context.LocalStorage.Contains("nested/value"),
                "files and typed storage should remain owner-scoped without exposing raw paths");

            var updateCount = 0;
            context.Events.SubscribeUpdate(_ => throw new InvalidOperationException("subscriber failure"));
            context.Events.SubscribeUpdate(_ => updateCount++);
            context.RaiseUpdate(0.1f);
            Assert(updateCount == 1 && logger.ErrorCount == 1,
                "one V1 event subscriber should not block later subscribers");

            var cleanupOrder = new List<string>();
            var mod = new ProbeMod(cleanupOrder);
            mod.Load(context);
            Assert(ReferenceEquals(mod.LoadContext, context),
                "TopiaForgeMod should attach Context before OnLoad");
            mod.Unload();
            Assert(ReferenceEquals(mod.UnloadContext, context) && !mod.CanReadContext,
                "TopiaForgeMod should retain Context through OnUnload and detach it afterwards");

            context.Lifetime.Defer(() => cleanupOrder.Add("lifetime"));
            context.DisposeLifetime();
            context.RaiseUpdate(0.1f);
            Assert(updateCount == 1, "lifetime cleanup should detach owner-scoped event subscriptions");
            Assert(context.Lifetime.IsStopping && cleanupOrder[^1] == "lifetime",
                "context lifetime should be cancelled and cleaned after unload");

            var failingContext = new ModContext(
                new ModManifest
                {
                    SchemaVersion = 5,
                    Id = "example.partial",
                    Name = "Partial Example",
                    Version = "1.0.0",
                    EntryAssembly = "Partial.dll",
                    EntryType = "Partial.Mod"
                },
                paths,
                Path.Combine(root, "partial-package"),
                logger,
                new ModServiceRegistry());
            var partialCleanup = 0;
            var failingMod = new FailingLoadMod(() => partialCleanup++);
            try
            {
                AssertThrows<InvalidOperationException>(() => failingMod.Load(failingContext),
                    "a failing OnLoad should reach runtime cleanup");
            }
            finally
            {
                try
                {
                    failingMod.Unload();
                }
                finally
                {
                    failingContext.DisposeLifetime();
                }
            }

            Assert(failingMod.UnloadCalled && partialCleanup == 1 && failingContext.Lifetime.IsStopping,
                "partial load failure should run OnUnload and dispose the owner lifetime");
        }

        [DataContract]
        private sealed class StoredValue
        {
            [DataMember(Name = "value")]
            public string Value { get; set; } = string.Empty;
        }

        private sealed class ProbeMod : TopiaForgeMod
        {
            private readonly List<string> cleanupOrder;

            public ProbeMod(List<string> cleanupOrder)
            {
                this.cleanupOrder = cleanupOrder;
            }

            public IModContext? LoadContext { get; private set; }
            public IModContext? UnloadContext { get; private set; }

            public bool CanReadContext
            {
                get
                {
                    try
                    {
                        _ = Context;
                        return true;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }
            }

            protected override void OnLoad()
            {
                LoadContext = Context;
                cleanupOrder.Add("load");
            }

            protected override void OnUnload()
            {
                UnloadContext = Context;
                cleanupOrder.Add("unload");
            }
        }

        private sealed class FailingLoadMod : TopiaForgeMod
        {
            private readonly Action cleanup;

            public FailingLoadMod(Action cleanup)
            {
                this.cleanup = cleanup;
            }

            public bool UnloadCalled { get; private set; }

            protected override void OnLoad()
            {
                Context.Lifetime.Defer(cleanup);
                throw new InvalidOperationException("expected load failure");
            }

            protected override void OnUnload()
            {
                UnloadCalled = true;
            }
        }

        private sealed class CountingDisposable : IDisposable
        {
            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                DisposeCount++;
            }
        }

        private sealed class DetailedOnlyModEvents : IModEvents, ISceneLoadEventSource
        {
            private Action<SceneLoadEvent>? detailedSceneLoaded;

            public IDisposable SubscribeUpdate(Action<float> handler) => new CountingDisposable();
            public IDisposable SubscribeFixedUpdate(Action<GameTimeSample> handler) => new CountingDisposable();
            public IDisposable SubscribeLateUpdate(Action<GameTimeSample> handler) => new CountingDisposable();
            public IDisposable SubscribeSceneLoaded(Action<string> handler) => new CountingDisposable();

            public IDisposable SubscribeSceneLoaded(Action<SceneLoadEvent> handler)
            {
                detailedSceneLoaded = handler;
                return new CountingDisposable();
            }

            public void Raise(SceneLoadEvent scene) => detailedSceneLoaded?.Invoke(scene);
        }

        private sealed class CapturedLogger : IModLogger
        {
            public int ErrorCount { get; private set; }

            public void Debug(string message) { }
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message) => ErrorCount++;
            public void Error(Exception exception, string message) => ErrorCount++;
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
