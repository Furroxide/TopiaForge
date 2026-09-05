using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class ScopedModContextTests
    {
        internal static void Run(string root)
        {
            ScopedConstructionDrainTests.Run(root + "-constructor-drain");
            ScopedCleanupReentrancyTests.Run(root + "-reentrant-cleanup");
            TestConcurrentCancellation();
            TestRebindingAndEvents(Path.Combine(root, "scope-events"));
            TestCancellationAndCleanup(Path.Combine(root, "scope-cleanup"));
            TestConstructorFailures(Path.Combine(root, "scope-construction"));
            TestLocalizationAndVisibility(Path.Combine(root, "scope-catalogs"));
            Console.WriteLine("ScopedModContextTests passed.");
        }

        private static void TestConcurrentCancellation()
        {
            var lifetime = new OwnerModLifetime();
            using var cancellationEntered = new ManualResetEventSlim();
            using var finishCancellation = new ManualResetEventSlim();
            using var cleanupEntered = new ManualResetEventSlim();
            lifetime.StoppingToken.Register(() => { cancellationEntered.Set(); finishCancellation.Wait(); });
            lifetime.Defer(cleanupEntered.Set);
            var cancellation = Task.Run(lifetime.BeginStop);
            Assert(cancellationEntered.Wait(TimeSpan.FromSeconds(5)), "cancellation callback should begin");
            var disposal = Task.Run(lifetime.Dispose);
            var disposedBeforeCancellationFinished = cleanupEntered.Wait(TimeSpan.FromMilliseconds(150));
            finishCancellation.Set();
            Task.WhenAll(cancellation, disposal).GetAwaiter().GetResult();
            Assert(!disposedBeforeCancellationFinished && cleanupEntered.IsSet,
                "concurrent disposal must await the active cancellation callback before resource cleanup");
        }

        private static void TestRebindingAndEvents(string root)
        {
            using var host = new HostDispatcher();
            var factory = new RecordingFactory();
            var parent = Parent(root, factory);
            var first = Scope(parent, "first", host);
            var second = Scope(parent, "second", host);
            Assert(ReferenceEquals(first.Context.Lifetime, first.Lifetime), "the context must expose its scope lifetime view");
            Assert(first.Context.Identity.Id == parent.Identity.Id && first.Context.Identity.Version == parent.Identity.Version,
                "scoped identity remains the real package identity");
            Assert(factory.Lifetimes.Count == 3 && ReferenceEquals(factory.Lifetimes[1], first.Lifetime),
                "the gameplay factory must receive a fresh child lifetime");
            Assert(!ReferenceEquals(parent.Files, first.Context.Files) && !ReferenceEquals(parent.Extensions, first.Context.Extensions)
                && !ReferenceEquals(parent.Assets, first.Context.Assets), "resource-producing facades must be rebound");
            var counts = new int[3];
            parent.Events.SubscribeUpdate(_ => counts[0]++);
            first.Context.Events.SubscribeUpdate(_ => counts[1]++);
            second.Context.Events.SubscribeUpdate(_ => counts[2]++);
            var fixedCount = 0; var lateCount = 0; var sceneCount = 0; var detailedCount = 0; var lifecycleCount = 0;
            first.Context.Events.SubscribeFixedUpdate(_ => fixedCount++);
            first.Context.Events.SubscribeLateUpdate(_ => lateCount++);
            first.Context.Events.SubscribeSceneLoaded(_ => sceneCount++);
            first.Context.Events.SubscribeSceneLoaded((SceneLoadEvent _) => detailedCount++);
            first.Context.Events.SubscribeSceneLifecycle(_ => lifecycleCount++);
            parent.RaiseUpdate(1);
            parent.RaiseFixedUpdate(default);
            parent.RaiseLateUpdate(default);
            parent.RaiseSceneLoaded(new SceneLoadEvent("World", SceneLoadMode.Single, true));
            parent.RaiseSceneActivated(new SceneLoadEvent("World", SceneLoadMode.Single, true));
            parent.RaiseSceneLifecycle(new SceneLifecycleEvent(1, "World", SceneLifecyclePhase.Loaded, SceneLoadMode.Single, true));
            Assert(fixedCount == 1 && lateCount == 1 && sceneCount == 1 && detailedCount == 2 && lifecycleCount == 1,
                "all child event channels must preserve package delivery exactly once");
            first.BeginStop();
            Assert(first.Context.LocalPlayer.Heal(1, "stale").ErrorCode == ModErrorCode.Cancelled,
                "stopped player mutations must be rejected before reaching a backend");
            Assert(first.Context.Entities.SetTransform(null!, TransformState.Identity).ErrorCode == ModErrorCode.Cancelled,
                "stopped entity mutations must be rejected before reaching a backend");
            parent.RaiseUpdate(1);
            parent.RaiseFixedUpdate(default);
            Assert(counts[0] == 2 && counts[1] == 1 && counts[2] == 2 && fixedCount == 1,
                "cancel-only suppresses child callbacks immediately while parent/sibling continue");
            first.Dispose();
            parent.RaiseUpdate(1);
            Assert(counts[0] == 3 && counts[1] == 1 && counts[2] == 3, "child cleanup must not remove sibling subscriptions");
            second.BeginStop(); second.Dispose(); parent.DisposeLifetime();
        }

        private static void TestCancellationAndCleanup(string root)
        {
            using var host = new HostDispatcher();
            var parent = Parent(root);
            var stops = 0;
            var first = Scope(parent, "first", host, () => stops++);
            var second = Scope(parent, "second", host, () => stops++);
            var cleanup = 0;
            first.Lifetime.Defer(() => { Assert(second.Lifetime.IsStopping, "siblings cancel before cleanup"); cleanup++; });
            first.Lifetime.Defer(() => { cleanup++; throw new InvalidOperationException("cleanup failure"); });
            second.Lifetime.Defer(() => cleanup++);
            Task.Run(first.Lifetime.Dispose).GetAwaiter().GetResult();
            Assert(stops == 0 && cleanup == 0, "public lifetime Dispose must enqueue stop rather than dispose on the worker");
            host.Drain();
            Assert(stops == 1 && cleanup == 0, "public Dispose requests session stop only");
            parent.BeginStopping();
            Assert(first.Lifetime.IsStopping && second.Lifetime.IsStopping && cleanup == 0,
                "parent cancellation must notify and cancel every scope without destroying resources");
            var thread = Thread.CurrentThread.ManagedThreadId;
            var rejected = new Resource(() => Assert(Thread.CurrentThread.ManagedThreadId == thread, "rejected cleanup belongs to host"));
            Task.Run(() => Throws<ObjectDisposedException>(() => first.Lifetime.Track(rejected))).GetAwaiter().GetResult();
            host.Drain();
            Assert(rejected.Count == 0, "late Track must retain its resource until the lifecycle drain barrier");
            first.DrainRejectedResourcesAsync().GetAwaiter().GetResult();
            Assert(rejected.Count == 1, "the explicit drain barrier releases rejected resources exactly once");
            Throws<AggregateException>(first.Dispose);
            second.Dispose();
            Assert(cleanup == 3 && parent.ActiveChildScopeCount == 0, "throwing cleanup must release every scope registration");
            var terminal = new Resource(() => Assert(Thread.CurrentThread.ManagedThreadId == thread, "terminal cleanup belongs to host"));
            Task.Run(() => Throws<ObjectDisposedException>(() => first.Lifetime.Track(terminal))).GetAwaiter().GetResult();
            Assert(terminal.Count == 0, "terminal late Track cannot dispose on its worker");
            host.Drain();
            Assert(terminal.Count == 1, "terminal late Track cannot wait for a nonexistent future scope drain");
            parent.DisposeLifetime();
        }

        private static void TestConstructorFailures(string root)
        {
            using var host = new HostDispatcher();
            var factory = new RecordingFactory { FailAt = 1 };
            Throws<InvalidOperationException>(() => Parent(root + "-parent", factory));
            Assert(factory.Disposed == 1, "package context construction must clean resources allocated by a failing service factory");
            factory = new RecordingFactory { FailAt = 2 };
            var parent = Parent(root, factory);
            Throws<InvalidOperationException>(() => Scope(parent, "failing", host));
            Assert(factory.Disposed == 1 && parent.ActiveChildScopeCount == 0 && !parent.Lifetime.IsStopping,
                "scoped context construction failure must clean only its own allocations and release parent retention");
            factory.FailAt = 0;
            var scope = Scope(parent, "retry", host);
            var allocated = new Resource();
            try { scope.Lifetime.Track(allocated); throw new InvalidOperationException("author constructor failure"); }
            catch (InvalidOperationException) { scope.BeginStop(); scope.Dispose(); }
            Assert(allocated.Count == 1, "allocations tracked before an author constructor failure must be cleaned");
            parent.DisposeLifetime();
        }

        private static void TestLocalizationAndVisibility(string root)
        {
            using var host = new HostDispatcher();
            var registry = new ModServiceRegistry();
            var provider = new Provider();
            registry.RegisterExtension<IProbe>("provider.mod", provider, ExtensionCardinality.Singleton);
            var manifest = Manifest();
            manifest.Dependencies.Add("provider.mod", ">=1.0.0");
            manifest.OptionalDependencies.Add("optional.mod", ">=2.0.0");
            var providerManifest = Manifest("provider.mod");
            var parent = Parent(root, registry: registry, manifest: manifest, available: new[] { providerManifest });
            parent.Localization.Register(new LocalizationCatalog("en", new Dictionary<string, string> { ["label"] = "parent" }));
            var child = Scope(parent, "child", host);
            var sibling = Scope(parent, "sibling", host);
            child.Context.Localization.Register(new LocalizationCatalog("en", new Dictionary<string, string> { ["label"] = "child" }));
            Assert(child.Context.Localization.Get("label", "missing") == "child" && sibling.Context.Localization.Get("label", "missing") == "parent",
                "children inherit parent catalogs while keeping overrides isolated");
            var culture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
                parent.Localization.Register(new LocalizationCatalog("fr", new Dictionary<string, string> { ["label"] = "parent-fr" }));
                Assert(child.Context.Localization.Get("label", "missing") == "parent-fr",
                    "a child fallback locale must not mask the parent's exact language");
            }
            finally { CultureInfo.CurrentUICulture = culture; }
            Assert(child.Context.Extensions.TryGet<IProbe>(out var first) && sibling.Context.Extensions.TryGet<IProbe>(out var second)
                && !ReferenceEquals(first, second), "each child resolves its own owner-bound facade");
            Assert(provider.Lifetimes.Count == 2 && ReferenceEquals(provider.Lifetimes[0], child.Lifetime)
                && provider.OwnerIds[0] == parent.Identity.Id, "extension factories receive scope lifetime with authentic package identity");
            child.BeginStop(); child.Dispose();
            Assert(sibling.Context.Localization.Get("label", "missing") == "parent" && provider.Active == 1,
                "child cleanup preserves parent localization and sibling extension resources");
            Assert(child.Context.Extensions.GetAll<IProbe>().Count == 0, "a stopped child cannot recreate an extension facade");
            sibling.BeginStop(); sibling.Dispose(); parent.DisposeLifetime();
            Assert(provider.Active == 0, "all child extension allocations must be released");
        }

        private static ModContextScope Scope(ModContext parent, string session, HostDispatcher host, Action? stop = null)
        {
            var creation = parent.CreateChildScopeAsync(session, CancellationToken.None, stop ?? (() => { }),
                new NativeTransitionAccessSlot(session + ":" + parent.Identity.Id, session, () => !parent.Lifetime.IsStopping), host);
            HostDispatcherTests.Pump(host, creation);
            return creation.Result;
        }
        private static ModManifest Manifest(string id = "scope.mod") => new ModManifest
        { SchemaVersion = 5, Id = id, Name = "Scope", Version = "1.0.0", EntryAssembly = "Scope.dll", EntryType = "Scope.Entry" };
        private static ModContext Parent(string root, RecordingFactory? factory = null, ModServiceRegistry? registry = null,
            ModManifest? manifest = null, IEnumerable<ModManifest>? available = null)
        {
            var paths = new ManagerPaths(root); paths.EnsureCreated();
            return new ModContext(manifest ?? Manifest(), paths, Path.Combine(root, "package"), new OwnerFacadeStoppingTests.Logger(),
                registry ?? new ModServiceRegistry(), null, factory, available);
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        private sealed class Resource : IDisposable
        {
            private readonly Action? action;
            internal int Count;
            internal Resource(Action? action = null) { this.action = action; }
            public void Dispose() { Count++; action?.Invoke(); }
        }
        private sealed class RecordingFactory : IGameplayContextFactory
        {
            internal readonly List<IModLifetime> Lifetimes = new List<IModLifetime>();
            internal int FailAt; internal int Disposed;
            public GameplayContextServices Create(string ownerModId, string packagePath, string dataPath,
                IModLifetime lifetime, IModLogger logger, NativeTransitionAccessSlot? transitionAccess = null)
            {
                Lifetimes.Add(lifetime);
                lifetime.Defer(() => Disposed++);
                if (FailAt == Lifetimes.Count) throw new InvalidOperationException("factory allocation then failure");
                return GameplayContextServices.Unavailable(lifetime);
            }
        }
        private interface IProbe { }
        private sealed class Probe : IProbe { }
        private sealed class Provider : IProbe, IOwnerBoundExtensionFactory
        {
            internal readonly List<IModLifetime> Lifetimes = new List<IModLifetime>();
            internal readonly List<string> OwnerIds = new List<string>();
            internal int Active;
            public object CreateOwnerFacade(Type contractType, string ownerModId, IModLifetime lifetime)
            { Lifetimes.Add(lifetime); OwnerIds.Add(ownerModId); Active++; lifetime.Defer(() => Active--); return new Probe(); }
        }
    }
}
