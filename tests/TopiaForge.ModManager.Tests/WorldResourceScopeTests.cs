using System;
using System.Collections.Generic;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldResourceScopeTests
    {
        public static void Run()
        {
            FailureAfterAllocationAttemptsEveryResource();
            LateAllocationIsDisposedAndRejected();
            LifetimeAndEarlyReleaseDisposeExactlyOnce();
            Console.WriteLine("All world resource scope tests passed.");
        }
        private static void FailureAfterAllocationAttemptsEveryResource()
        {
            var calls = new List<int>();
            var scope = new WorldResourceScope();
            scope.Add(new Resource(() => calls.Add(1)));
            scope.Add(new Resource(() => { calls.Add(2); throw new InvalidOperationException("cleanup-two"); }));
            scope.Add(new Resource(() => { calls.Add(3); throw new InvalidOperationException("cleanup-three"); }));
            try { scope.Dispose(); throw new InvalidOperationException("throwing disposers must be reported"); }
            catch (AggregateException error) { Assert(error.Flatten().InnerExceptions.Count == 2, "all cleanup failures must be retained"); }
            scope.Dispose();
            Assert(string.Join(",", calls) == "3,2,1", "cleanup must continue in reverse order exactly once");
        }
        private static void LateAllocationIsDisposedAndRejected()
        {
            var scope = new WorldResourceScope();
            var calls = 0;
            scope.Dispose();
            try { scope.Add(new Resource(() => calls++)); throw new InvalidOperationException("late allocation must fail"); }
            catch (ObjectDisposedException) { }
            Assert(calls == 1, "allocation racing a stopped scope must not leak");
        }
        private static void LifetimeAndEarlyReleaseDisposeExactlyOnce()
        {
            using var lifetime = new FakeModLifetime();
            var calls = 0;
            var scope = WorldResourceScope.Create(lifetime);
            scope.Add(new Resource(() => calls++));
            Assert(lifetime.TrackedResourceCount == 1, "scope must be registered before provider work starts");
            scope.Dispose();
            Assert(calls == 1 && lifetime.TrackedResourceCount == 0, "early instance release must remove the lifetime node");
            lifetime.Dispose();
            Assert(calls == 1, "lifetime cleanup cannot release resources twice");
        }
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        private sealed class Resource : IDisposable
        {
            private readonly Action action;
            public Resource(Action action) { this.action = action; }
            public void Dispose() => action();
        }
    }
}
