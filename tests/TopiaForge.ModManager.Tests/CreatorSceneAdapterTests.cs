using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.CreatorContent;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class CreatorSceneAdapterTests
    {
        public static void Run()
        {
            TestAuthenticatedRegistrationAndTargetIdentity();
            TestExclusiveLeasesAndOwnerUnloadCleanup();
            TestAdapterFaultsReleaseReservations();
            Console.WriteLine("CreatorSceneAdapterTests passed.");
        }

        private static void TestAuthenticatedRegistrationAndTargetIdentity()
        {
            using var context = new FakeModContext();
            using var service = new CreatorContentService("provider", context.Runtime, context.Logger);
            using var owner = new FakeModLifetime();
            using var otherOwner = new FakeModLifetime();
            var registry = Owner<ICreatorSceneAdapterRegistry>(service, "mod.alpha", owner);
            var content = Owner<ICreatorContentService>(service, "mod.alpha", owner);
            var otherContent = Owner<ICreatorContentService>(service, "mod.beta", otherOwner);
            var ownCatalog = Require(content.Register(Content("own-prop")), "owner content should register");
            var foreignCatalog = Require(otherContent.Register(Content("foreign-prop")), "foreign content should register");

            var ordinary = new TestTarget("ordinary", "North crate", new FakeEntity("entity-a", "crate", Vec3.Zero));
            var ownedDuplicate = new TestTarget(
                "owned", "North duplicate", new FakeEntity("entity-b", "owned", new Vec3(1f, 0f, 0f)),
                CreatorSceneTargetCapabilities.Transform | CreatorSceneTargetCapabilities.CatalogDuplicate,
                ownCatalog.Descriptor.ContentId);
            var foreignDuplicate = new TestTarget(
                "foreign", "Foreign duplicate", new FakeEntity("entity-c", "foreign", new Vec3(2f, 0f, 0f)),
                CreatorSceneTargetCapabilities.Transform | CreatorSceneTargetCapabilities.CatalogDuplicate,
                foreignCatalog.Descriptor.ContentId);
            var adapter = new TestAdapter(ordinary, ownedDuplicate, foreignDuplicate);
            var registration = Require(
                registry.RegisterSceneAdapter(new CreatorSceneAdapterRegistrationRequest("native", "Native props", adapter)),
                "scene adapter should register");

            Assert(registration.Descriptor.AdapterId == "mod.alpha.native", "adapter id must be derived from the authenticated owner");
            Assert(registration.Descriptor.SourceId == "mod.alpha", "adapter descriptor must retain the authenticated owner");
            Assert(registry.RegisterSceneAdapter(new CreatorSceneAdapterRegistrationRequest("native", "Duplicate", adapter)).ErrorCode
                == ModErrorCode.Conflict, "one owner cannot reuse a local adapter id");

            using var session = Require(service.BeginSession(new CreatorSessionOptions("identity")), "session should begin");
            var resolved = Require(session.ResolveSceneTarget(ordinary.Entity), "ordinary target should resolve");
            Assert(resolved.AdapterId == registration.Descriptor.AdapterId, "provider wrapper must replace a source's spoofed adapter claim");
            Assert(resolved.Id == "mod.alpha.native:ordinary", "process-local target ids should include the authenticated adapter id");
            Assert(ReferenceEquals(resolved.Entity, ordinary.Entity), "resolve must preserve the exact input entity identity");

            var duplicate = Require(session.ResolveSceneTarget(ownedDuplicate.Entity), "same-owner duplicate recipe should resolve");
            Assert(duplicate.CatalogContentId == ownCatalog.Descriptor.ContentId, "same-owner catalog recipes should remain available");
            Assert(session.ResolveSceneTarget(foreignDuplicate.Entity).ErrorCode == ModErrorCode.External,
                "an adapter must not claim another package's content as its duplicate recipe");

            var queried = Require(session.QuerySceneTargets(new CreatorSceneQuery(
                new Vec3(0f, 0f, 0f), 1.1f, "North", 4, registration.Descriptor.AdapterId)),
                "authenticated adapter query should succeed");
            Assert(queried.Count == 2 && queried.All(target => target.AdapterId == "mod.alpha.native"),
                "query filters and authenticated identity should survive provider wrapping");
            AssertThrows<ArgumentOutOfRangeException>(
                () => new CreatorSceneQuery(nameContains: new string('x', 129)),
                "scene query name filters must remain bounded");

            var secondRegistry = Owner<ICreatorSceneAdapterRegistry>(service, "mod.beta", otherOwner);
            var second = Require(
                secondRegistry.RegisterSceneAdapter(new CreatorSceneAdapterRegistrationRequest("native", "Other native props", new TestAdapter())),
                "another authenticated owner should be able to reuse the local id");
            Assert(second.Descriptor.AdapterId == "mod.beta.native", "local adapter ids must be namespaced per authenticated owner");
        }

        private static void TestExclusiveLeasesAndOwnerUnloadCleanup()
        {
            using var context = new FakeModContext();
            using var service = new CreatorContentService("provider", context.Runtime, context.Logger);
            using var owner = new FakeModLifetime();
            var first = new TestTarget(
                "first", "First prop", new FakeEntity("entity-first", "first", Vec3.Zero),
                CreatorSceneTargetCapabilities.Transform | CreatorSceneTargetCapabilities.TemporaryVisibility);
            var second = new TestTarget(
                "second", "Second prop", new FakeEntity("entity-second", "second", new Vec3(2f, 0f, 0f)),
                CreatorSceneTargetCapabilities.Transform | CreatorSceneTargetCapabilities.TemporaryVisibility);
            var adapter = new TestAdapter(first, second);
            var registration = Require(
                Owner<ICreatorSceneAdapterRegistry>(service, "mod.leases", owner).RegisterSceneAdapter(
                    new CreatorSceneAdapterRegistrationRequest("props", "Lease props", adapter)),
                "lease adapter should register");
            using var sessionOne = Require(service.BeginSession(new CreatorSessionOptions("lease one")), "first session should begin");
            using var sessionTwo = Require(service.BeginSession(new CreatorSessionOptions("lease two")), "second session should begin");
            var firstOne = Require(sessionOne.ResolveSceneTarget(first.Entity), "first session should resolve target");
            var firstTwo = Require(sessionTwo.ResolveSceneTarget(first.Entity), "second session should resolve target");

            var editOne = Require(sessionOne.BeginTemporaryEdit(firstOne), "first exclusive lease should begin");
            Assert(sessionTwo.BeginTemporaryEdit(firstTwo).ErrorCode == ModErrorCode.Conflict,
                "the provider must enforce exclusivity across sessions before calling the adapter");
            Assert(editOne.SetTransform(new TransformState(
                new Vec3(5f, 0f, 0f), Quat.Identity, new Vec3(1f, 1f, 1f))).Succeeded,
                "a supported temporary transform should apply");
            Assert(editOne.SetTemporarilyHidden(true).Succeeded && first.Hidden,
                "a supported temporary visibility edit should apply");
            Assert(editOne.Restore().Succeeded && first.Transform == TransformState.Identity && !first.Hidden,
                "explicit restore should release the lease and restore the snapshot");
            using (var editTwo = Require(sessionTwo.BeginTemporaryEdit(firstTwo), "restoration should release provider exclusivity"))
            {
                Assert(editTwo.IsAlive, "replacement lease should be live");
            }

            adapter.RestoreOrder.Clear();
            first.RestoreCount = 0;
            second.RestoreCount = 0;
            var firstLease = Require(sessionOne.BeginTemporaryEdit(firstOne), "first ordered lease should begin");
            var secondTarget = Require(sessionOne.ResolveSceneTarget(second.Entity), "second target should resolve");
            var secondLease = Require(sessionOne.BeginTemporaryEdit(secondTarget), "second ordered lease should begin");
            Assert(firstLease.SetTemporarilyHidden(true).Succeeded && secondLease.SetTemporarilyHidden(true).Succeeded,
                "ordered leases should both mutate before owner unload");

            owner.Dispose();
            Assert(!registration.IsAlive && !firstLease.IsAlive && !secondLease.IsAlive,
                "source unload must invalidate its registration and all active leases");
            Assert(adapter.RestoreOrder.SequenceEqual(new[] { "second", "first" }),
                "source unload must restore active edits exactly once in reverse acquisition order");
            Assert(first.RestoreCount == 1 && second.RestoreCount == 1 && !first.Hidden && !second.Hidden,
                "source unload must restore each native target exactly once");
            Assert(sessionOne.QuerySceneTargets(new CreatorSceneQuery()).ErrorCode == ModErrorCode.Unavailable,
                "an unloaded source must disappear from subsequent session queries");
        }

        private static void TestAdapterFaultsReleaseReservations()
        {
            using var context = new FakeModContext();
            using var service = new CreatorContentService("provider", context.Runtime, context.Logger);
            using var owner = new FakeModLifetime();
            var target = new TestTarget("fault", "Fault prop", new FakeEntity("entity-fault", "fault", Vec3.Zero));
            var adapter = new TestAdapter(target);
            Require(
                Owner<ICreatorSceneAdapterRegistry>(service, "mod.faults", owner).RegisterSceneAdapter(
                    new CreatorSceneAdapterRegistrationRequest("native", "Fault adapter", adapter)),
                "fault adapter should register");
            var session = Require(service.BeginSession(new CreatorSessionOptions("faults")), "fault session should begin");

            adapter.ThrowOnQuery = true;
            Assert(session.QuerySceneTargets(new CreatorSceneQuery()).ErrorCode == ModErrorCode.External,
                "adapter query exceptions must become stable external failures");
            adapter.ThrowOnQuery = false;
            adapter.ThrowOnResolve = true;
            Assert(session.ResolveSceneTarget(target.Entity).ErrorCode == ModErrorCode.External,
                "adapter resolve exceptions must become stable external failures");
            adapter.ThrowOnResolve = false;
            var wrapped = Require(session.ResolveSceneTarget(target.Entity), "target should resolve after a transient adapter fault");

            adapter.ThrowOnBegin = true;
            Assert(session.BeginTemporaryEdit(wrapped).ErrorCode == ModErrorCode.External,
                "adapter begin exceptions must fail closed");
            adapter.ThrowOnBegin = false;
            var edit = Require(session.BeginTemporaryEdit(wrapped), "failed begin must not leak the provider reservation");
            Assert(edit.SetTransform(new TransformState(
                new Vec3(3f, 0f, 0f), Quat.Identity, new Vec3(1f, 1f, 1f))).Succeeded,
                "recovered adapter should accept a temporary edit");
            session.Dispose();
            Assert(!edit.IsAlive && target.Transform == TransformState.Identity && adapter.ActiveEditCount == 0,
                "session cleanup must restore and release adapter edits after transient faults");
        }

        private static T Owner<T>(CreatorContentService service, string ownerId, IModLifetime lifetime) where T : class =>
            (T)((IOwnerBoundExtensionFactory)service).CreateOwnerFacade(typeof(T), ownerId, lifetime);

        private static CreatorContentRegistrationRequest Content(string localId) => new CreatorContentRegistrationRequest(
            localId, localId, string.Empty, CreatorContentKind.Prop, CreatorTransformCapabilities.All, new UnavailableFactory());

        private static T Require<T>(OperationResult<T> result, string message) where T : notnull
        {
            if (!result.TryGetValue(out var value)) throw new InvalidOperationException(message + ": " + result.ErrorMessage);
            return value;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }

        private sealed class UnavailableFactory : ICreatorContentFactory
        {
            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform) =>
                OperationResult<ICreatorSourceInstance>.Failure(ModErrorCode.Unavailable, "Not needed by this test.");
        }

        private sealed class TestAdapter : ICreatorSceneAdapter
        {
            private readonly IReadOnlyList<TestTarget> targets;
            public TestAdapter(params TestTarget[] targets) => this.targets = targets;
            public bool ThrowOnResolve { get; set; }
            public bool ThrowOnQuery { get; set; }
            public bool ThrowOnBegin { get; set; }
            public int ActiveEditCount { get; private set; }
            public List<string> RestoreOrder { get; } = new List<string>();

            public OperationResult<ICreatorSceneTarget> ResolveSceneTarget(IEntity entity)
            {
                if (ThrowOnResolve) throw new InvalidOperationException("resolve fault");
                var target = targets.FirstOrDefault(candidate => ReferenceEquals(candidate.Entity, entity));
                return target == null
                    ? OperationResult<ICreatorSceneTarget>.Failure(ModErrorCode.NotFound, "Unknown test target.")
                    : OperationResult<ICreatorSceneTarget>.Success(target);
            }

            public OperationResult<IReadOnlyList<ICreatorSceneTarget>> QuerySceneTargets(CreatorSceneQuery query)
            {
                if (ThrowOnQuery) throw new InvalidOperationException("query fault");
                var matches = targets
                    .Where(target => string.IsNullOrEmpty(query.NameContains)
                        || target.DisplayName.IndexOf(query.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Where(target => !query.Center.HasValue || query.Radius == 0f
                        || Vec3.Distance(target.Entity.Position, query.Center.Value) <= query.Radius)
                    .Take(query.MaximumResults)
                    .Cast<ICreatorSceneTarget>()
                    .ToArray();
                return OperationResult<IReadOnlyList<ICreatorSceneTarget>>.Success(matches);
            }

            public OperationResult<ICreatorTemporaryEdit> BeginTemporaryEdit(ICreatorSceneTarget target)
            {
                if (ThrowOnBegin) throw new InvalidOperationException("begin fault");
                if (!(target is TestTarget typed) || !targets.Contains(typed))
                {
                    return OperationResult<ICreatorTemporaryEdit>.Failure(ModErrorCode.InvalidArgument, "Unknown test target.");
                }
                ActiveEditCount++;
                return OperationResult<ICreatorTemporaryEdit>.Success(new TestEdit(this, typed));
            }

            public void Restored(TestTarget target)
            {
                ActiveEditCount--;
                RestoreOrder.Add(target.Id);
                target.RestoreCount++;
            }
        }

        private sealed class TestTarget : ICreatorSceneTarget
        {
            public TestTarget(
                string id,
                string displayName,
                FakeEntity entity,
                CreatorSceneTargetCapabilities capabilities = CreatorSceneTargetCapabilities.Transform,
                string catalogContentId = "")
            {
                Id = id;
                DisplayName = displayName;
                FakeEntity = entity;
                Capabilities = capabilities;
                CatalogContentId = catalogContentId;
            }

            public string Id { get; }
            public string DisplayName { get; }
            public CreatorContentKind Kind => CreatorContentKind.Prop;
            public CreatorSceneTargetCapabilities Capabilities { get; }
            public string CatalogContentId { get; }
            public string AdapterId => "spoofed.cross.package.adapter";
            public IEntity Entity => FakeEntity;
            public bool IsAlive => FakeEntity.IsAlive;
            public FakeEntity FakeEntity { get; }
            public bool Hidden { get; set; }
            public int RestoreCount { get; set; }
            public TransformState Transform => new TransformState(FakeEntity.Position, FakeEntity.Rotation, FakeEntity.Scale);

            public void Apply(TransformState transform)
            {
                FakeEntity.Position = transform.Position;
                FakeEntity.Rotation = transform.Rotation;
                FakeEntity.Scale = transform.Scale;
            }
        }

        private sealed class TestEdit : ICreatorTemporaryEdit
        {
            private readonly TestAdapter adapter;
            private readonly TestTarget target;
            private readonly TransformState original;
            private readonly bool originalHidden;
            private bool alive = true;

            public TestEdit(TestAdapter adapter, TestTarget target)
            {
                this.adapter = adapter;
                this.target = target;
                original = target.Transform;
                originalHidden = target.Hidden;
            }

            public ICreatorSceneTarget Target => target;
            public CreatorSceneTargetCapabilities Capabilities => target.Capabilities
                & (CreatorSceneTargetCapabilities.Transform | CreatorSceneTargetCapabilities.TemporaryVisibility);
            public bool IsAlive => alive && target.IsAlive;

            public bool TryGetTransform(out TransformState transform)
            {
                transform = target.Transform;
                return IsAlive;
            }

            public OperationResult<TransformState> SetTransform(TransformState transform)
            {
                if (!IsAlive) return OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "Edit ended.");
                target.Apply(transform);
                return OperationResult<TransformState>.Success(transform);
            }

            public OperationResult<bool> SetTemporarilyHidden(bool hidden)
            {
                if (!IsAlive) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Edit ended.");
                target.Hidden = hidden;
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> Restore()
            {
                if (!alive) return OperationResult<bool>.Success(false);
                alive = false;
                target.Apply(original);
                target.Hidden = originalHidden;
                adapter.Restored(target);
                return OperationResult<bool>.Success(true);
            }

            public void Dispose() => Restore();
        }
    }
}
