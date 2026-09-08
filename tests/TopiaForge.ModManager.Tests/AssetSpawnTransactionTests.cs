using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class AssetSpawnTransactionTests
    {
        internal static void Run()
        {
            using var lifetime = new OwnerModLifetime();
            var failed = new Allocation();
            Throws<InvalidOperationException>(() => AssetSpawnTransaction.Create<Allocation, Resource>(
                () => failed, _ => throw new InvalidOperationException("entity setup failed"), item => item.Destroy(), lifetime));
            Assert(failed.Destroyed == 1, "setup failure after native allocation must destroy the untracked instance exactly once");
            var allocateCleanup = 0;
            Throws<InvalidOperationException>(() => AssetSpawnTransaction.Create<Allocation, Resource>(
                () => throw new InvalidOperationException("allocation failed"), item => new Resource(item), _ => allocateCleanup++, lifetime));
            Assert(allocateCleanup == 0, "failed native allocation has no instance to destroy");
            AggregateException? both = null;
            try
            {
                AssetSpawnTransaction.Create<Allocation, Resource>(() => new Allocation(),
                    _ => throw new InvalidOperationException("setup"), _ => throw new InvalidOperationException("destroy"), lifetime);
            }
            catch (AggregateException exception) { both = exception; }
            Assert(both?.InnerExceptions.Count == 2, "initialization and cleanup failures must both remain observable");
            using var rejectedLifetime = new DeferredRejection();
            var rejected = new Allocation();
            Throws<ObjectDisposedException>(() => AssetSpawnTransaction.Create(() => rejected,
                item => new Resource(item), item => item.Destroy(), rejectedLifetime));
            Assert(rejected.Destroyed == 0 && rejectedLifetime.Resources.Count == 1,
                "Track rejection transfers cleanup ownership and must not destroy again before the host drain");
            rejectedLifetime.Dispose();
            Assert(rejected.Destroyed == 1, "the rejecting lifetime destroys its accepted allocation exactly once");
            var success = new Allocation();
            AssetSpawnTransaction.Create(() => success, item => new Resource(item), item => item.Destroy(), lifetime);
            Assert(success.Destroyed == 0, "successful allocation remains live until lifetime cleanup");
            lifetime.Dispose();
            Assert(success.Destroyed == 1, "successful allocation is owned by its lifetime");
            PrefabNamesRemainAuthoredAndOwned();
            Console.WriteLine("AssetSpawnTransactionTests passed (generic transaction and 5 prefab cases).");
        }
        private static void PrefabNamesRemainAuthoredAndOwned()
        {
            var failures = new List<string>(); var passed = 0;
            void Case(string name, Action test)
            { try { test(); passed++; } catch (Exception error) { failures.Add(name + ": " + error.Message); } }
            foreach (var authored in new[] { "SpawnPoint", "SpawnPoint(Clone)" }) Case(authored, () => {
                using var lifetime = new OwnerModLifetime();
                var allocation = new Allocation { Name = authored + "(Clone)" };
                var initialized = false;
                AssetSpawnTransaction.CreatePrefab(() => allocation, authored, (item, name) => item.Name = name,
                    item => { Assert(item.Name == authored, "exact authored name must be set before entity publication"); initialized = true; return new Resource(item); },
                    item => item.Destroy(), lifetime);
                Assert(initialized && allocation.Destroyed == 0, "named instance remains owned and live");
                lifetime.Dispose();
                Assert(allocation.Destroyed == 1, "named prefab lifetime destroys exactly once");
            });
            Case("name-set-failure", () => {
                using var lifetime = new OwnerModLifetime(); var allocation = new Allocation(); var initialized = false;
                Throws<InvalidOperationException>(() => AssetSpawnTransaction.CreatePrefab(() => allocation, "SpawnPoint",
                    (_, _) => throw new InvalidOperationException("name assignment failed"),
                    item => { initialized = true; return new Resource(item); }, item => item.Destroy(), lifetime));
                Assert(!initialized && allocation.Destroyed == 1, "name failure destroys before entity publication");
                lifetime.Dispose(); Assert(allocation.Destroyed == 1, "failed naming cannot transfer ownership");
            });
            Case("later-entity-failure", () => {
                using var lifetime = new OwnerModLifetime(); var allocation = new Allocation { Name = "SpawnPoint(Clone)" };
                Throws<InvalidOperationException>(() => AssetSpawnTransaction.CreatePrefab<Allocation, Resource>(() => allocation,
                    "SpawnPoint", (item, name) => item.Name = name,
                    _ => throw new InvalidOperationException("entity setup failed"), item => item.Destroy(), lifetime));
                Assert(allocation.Name == "SpawnPoint" && allocation.Destroyed == 1, "later initialization failure cleans named instance once");
            });
            Case("lifetime-rejection", () => {
                using var lifetime = new DeferredRejection(); var allocation = new Allocation { Name = "SpawnPoint(Clone)" };
                Throws<ObjectDisposedException>(() => AssetSpawnTransaction.CreatePrefab(() => allocation, "SpawnPoint",
                    (item, name) => item.Name = name, item => new Resource(item), item => item.Destroy(), lifetime));
                Assert(allocation.Name == "SpawnPoint" && allocation.Destroyed == 0 && lifetime.Resources.Count == 1,
                    "rejected lifetime retains sole cleanup ownership of named instance until drain");
                lifetime.Dispose(); Assert(allocation.Destroyed == 1, "rejected named instance drained once");
            });
            if (failures.Count != 0) throw new InvalidOperationException("Prefab cases passed " + passed + "; failed " + failures.Count + ":\n" + string.Join("\n", failures));
        }
        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
        private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        private sealed class Allocation
        {
            internal int Destroyed;
            internal string Name = string.Empty;
            internal void Destroy() => Destroyed++;
        }
        private sealed class Resource : IDisposable
        {
            private Allocation? allocation;
            internal Resource(Allocation allocation) { this.allocation = allocation; }
            public void Dispose() => Interlocked.Exchange(ref allocation, null)?.Destroy();
        }
        private sealed class DeferredRejection : IModLifetime
        {
            internal readonly List<IDisposable> Resources = new List<IDisposable>();
            public bool IsStopping => true;
            public CancellationToken StoppingToken => new CancellationToken(true);
            public IDisposable Track(IDisposable resource) { Resources.Add(resource); throw new ObjectDisposedException("scope"); }
            public IDisposable Defer(Action cleanup) => throw new NotSupportedException();
            public void Dispose() { foreach (var resource in Resources) resource.Dispose(); Resources.Clear(); }
        }
    }
}
