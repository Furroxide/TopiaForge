using System;
using System.Threading;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class BuiltinWorldProviderTests
    {
        internal static void Run()
        {
            BundleWaitsForReadinessAndOwnsContent();
            GameSceneUsesActualReadiness();
            FailedPrefabReleasesPreparationAndBundle();
            CancellationCannotPublishLateReadiness();
            CleanupFailureRetainsStartupFailure();
            WrongSelectionHasNoNativeEffects();
            OwnerCancellationReachesPendingNativeWork();
            AlreadyStoppedOwnerIsCancellation();
            EarlyFailureReleasesLifetimeRegistration();
            OwnerStopDuringTrackingReleasesReturnedLease();
            Console.WriteLine("Built-in world provider tests passed.");
        }

        private static ModWorldDeclaration Bundle() => new ModWorldDeclaration
        {
            Id = "example.worlds.world",
            Name = "Bundle world",
            Content = new ModWorldContent { Kind = ModWorldContent.BundleKind, Bundle = "content/world.bundle", Prefab = "AuthoredWorld" }
        };
        private static BuiltinWorldImplementation Bind(ModWorldDeclaration world) =>
            new BuiltinWorldImplementation(new PackageIdentity("example.worlds", "1.0.0"), world);

        private static void BundleWaitsForReadinessAndOwnsContent()
        {
            using var fixture = new BuiltinWorldFixture();
            var declaration = Bundle();
            var binding = Bind(declaration);
            declaration.Id = "example.worlds.changed";
            declaration.Content!.Kind = ModWorldContent.GameSceneKind;
            declaration.Content.Bundle = "changed.bundle";
            declaration.Content.Prefab = "Changed";
            var pending = binding.Create().LoadAsync(fixture, CancellationToken.None);
            Assert(!pending.IsCompleted && fixture.Native.Request?.Kind == NativeWorldLoadKind.OpenSandbox,
                "bundle loading must reach native preparation and await actual readiness");
            Assert(fixture.Fake.Assets.ActiveBundleCount == 1 && fixture.Fake.Assets.ActivePrefabCount == 1
                && fixture.Fake.Assets.ActiveSpawnCount == 1, "bundle, prefab and content are owned while readiness waits");
            Assert(fixture.Native.Content?.Name == "AuthoredWorld" && fixture.Native.Policy?.MarkerName == "Spawn",
                "activation must use the declaration snapshot and requested authored marker");
            fixture.Native.Succeed();
            var result = pending.GetAwaiter().GetResult();
            Assert(result.TryGetValue(out var world) && world.Readiness.Scene.InstanceId == 42
                && world.Readiness.Spawn.Position.Equals(new Vec3(3, 4, 5)), "return actual applied scene and spawn");
            world!.Dispose(); world.Dispose();
            Assert(fixture.Native.DisposeCount == 1 && fixture.Fake.Assets.ActiveBundleCount == 0
                && fixture.Fake.Assets.ActivePrefabCount == 0 && fixture.Fake.Assets.ActiveSpawnCount == 0,
                "world disposal must release each owned content resource exactly once");
        }

        private static void GameSceneUsesActualReadiness()
        {
            using var fixture = new BuiltinWorldFixture { SpawnPolicy = new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault) };
            var baselineResources = fixture.Fake.Lifetime.TrackedResourceCount;
            var declaration = Bundle();
            declaration.Content = new ModWorldContent { Kind = ModWorldContent.GameSceneKind, SceneName = "DeclaredScene" };
            var pending = Bind(declaration).Create().LoadAsync(fixture, CancellationToken.None);
            Assert(!pending.IsCompleted && fixture.Native.Request?.Kind == NativeWorldLoadKind.Scene
                && fixture.Native.Request.Key == "DeclaredScene" && fixture.Fake.Assets.ActiveBundleCount == 0,
                "game-scene loading must route through native preparation without fabricated assets");
            fixture.Native.Succeed();
            var result = pending.GetAwaiter().GetResult();
            Assert(result.TryGetValue(out var world) && world.Readiness.Scene.Name == "ActualLoadedScene",
                "requested scene text cannot replace actual scene identity");
            world!.Dispose();
            Assert(fixture.Fake.Lifetime.TrackedResourceCount == baselineResources,
                "early world disposal must release its lifetime registration");
        }

        private static void FailedPrefabReleasesPreparationAndBundle()
        {
            using var fixture = new BuiltinWorldFixture();
            fixture.Fake.Assets.PrefabLoadErrorCode = ModErrorCode.NotFound;
            var result = Bind(Bundle()).Create().LoadAsync(fixture, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.NotFound && fixture.Native.DisposeCount == 1
                && fixture.Fake.Assets.ActiveBundleCount == 0 && fixture.Native.Policy == null,
                "partial content failure must release ownership before returning and never claim readiness");
        }

        private static void CancellationCannotPublishLateReadiness()
        {
            using var fixture = new BuiltinWorldFixture();
            using var cancellation = new CancellationTokenSource();
            var pending = Bind(Bundle()).Create().LoadAsync(fixture, cancellation.Token);
            cancellation.Cancel();
            fixture.Native.Succeed();
            var result = pending.GetAwaiter().GetResult();
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Cancelled
                && fixture.Native.DisposeCount == 1 && fixture.Fake.Assets.ActiveSpawnCount == 0,
                "late native readiness after cancellation cannot publish a world or retain content");
        }

        private static void CleanupFailureRetainsStartupFailure()
        {
            using var fixture = new BuiltinWorldFixture();
            fixture.Fake.Assets.PrefabLoadErrorCode = ModErrorCode.NotFound;
            fixture.Native.ThrowOnDispose = true;
            try
            {
                Bind(Bundle()).Create().LoadAsync(fixture, CancellationToken.None).GetAwaiter().GetResult();
                throw new InvalidOperationException("expected aggregate startup/cleanup failure");
            }
            catch (AggregateException error)
            {
                Assert(error.Flatten().InnerExceptions.Count >= 2 && fixture.Native.DisposeCount == 1
                    && fixture.Fake.Assets.ActiveBundleCount == 0, "cleanup failure must preserve the original failure and still release assets");
            }
        }

        private static void OwnerCancellationReachesPendingNativeWork()
        {
            using var fixture = new BuiltinWorldFixture();
            var pending = Bind(Bundle()).Create().LoadAsync(fixture, CancellationToken.None);
            fixture.Fake.Lifetime.Dispose();
            Assert(fixture.Native.PrepareToken.IsCancellationRequested && fixture.Native.FinishToken.IsCancellationRequested,
                "owner cancellation must reach native operations before their late completion");
            fixture.Native.Succeed();
            var result = pending.GetAwaiter().GetResult();
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Cancelled && fixture.Native.DisposeCount == 1,
                "owner cancellation cannot publish late readiness or dispose preparation twice");
        }

        private static void AlreadyStoppedOwnerIsCancellation()
        {
            using var fixture = new BuiltinWorldFixture();
            fixture.Fake.Lifetime.Dispose();
            var result = Bind(Bundle()).Create().LoadAsync(fixture, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Cancelled && fixture.Native.Request == null,
                "an already stopped world owner must cancel before native work");
        }

        private static void EarlyFailureReleasesLifetimeRegistration()
        {
            using var fixture = new BuiltinWorldFixture();
            var baselineResources = fixture.Fake.Lifetime.TrackedResourceCount;
            fixture.Fake.Assets.BundleLoadErrorCode = ModErrorCode.NotFound;
            var result = Bind(Bundle()).Create().LoadAsync(fixture, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!result.Succeeded && fixture.Fake.Lifetime.TrackedResourceCount == baselineResources,
                "failed startup must release the world ownership registration");
        }

        private static void OwnerStopDuringTrackingReleasesReturnedLease()
        {
            using var fixture = new BuiltinWorldFixture();
            using var lifetime = new StopDuringTrackLifetime();
            fixture.LifetimeOverride = lifetime;
            var result = Bind(Bundle()).Create().LoadAsync(fixture, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Cancelled && fixture.Native.Request == null
                && lifetime.ReleasedLeases == 1, "owner stop during Track must release the returned lease and cancel before native work");
        }

        private sealed class StopDuringTrackLifetime : IModLifetime
        {
            private readonly TopiaForge.Mods.Testing.FakeModLifetime inner = new TopiaForge.Mods.Testing.FakeModLifetime();
            internal int ReleasedLeases;
            public CancellationToken StoppingToken => inner.StoppingToken;
            public bool IsStopping => inner.IsStopping;
            public IDisposable Track(IDisposable resource)
            {
                var lease = inner.Track(resource);
                inner.Dispose();
                return new Lease(this, lease);
            }
            public IDisposable Defer(Action action) => throw new InvalidOperationException("Unexpected defer.");
            public void Dispose() => inner.Dispose();
            private sealed class Lease : IDisposable
            {
                private StopDuringTrackLifetime? owner;
                private readonly IDisposable lease;
                internal Lease(StopDuringTrackLifetime owner, IDisposable lease) { this.owner = owner; this.lease = lease; }
                public void Dispose()
                {
                    var current = Interlocked.Exchange(ref owner, null);
                    if (current == null) return;
                    current.ReleasedLeases++;
                    lease.Dispose();
                }
            }
        }

        private static void WrongSelectionHasNoNativeEffects()
        {
            using var fixture = new BuiltinWorldFixture { WorldId = "example.worlds.other" };
            var result = Bind(Bundle()).Create().LoadAsync(fixture, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!result.Succeeded && fixture.Native.Request == null && fixture.Fake.Assets.ActiveBundleCount == 0,
                "a built-in implementation cannot activate a different declaration identity");
        }

        private static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
