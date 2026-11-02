using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldReadinessContractTests
    {
        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            foreach (var test in new Action[] { DefaultTransformIsRejected, ActualOriginRemainsLegal, RequestedAndSettledRemainDistinct, RequestedSpawnIsValidated, TestingMapPreservesBothPoses,
                () => InvalidProviderCannotStartGameplay(root) })
            {
                try { test(); } catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 0) throw new AggregateException(failures);
            Console.WriteLine("World readiness contract tests passed (6 cases).");
        }

        private static void DefaultTransformIsRejected()
        {
            try
            {
                _ = new WorldReadiness(new WorldSceneIdentity(1, "World"), default);
            }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("Default transform has no valid rotation or scale and cannot assert readiness.");
        }

        private static void ActualOriginRemainsLegal()
        {
            var readiness = new WorldReadiness(new WorldSceneIdentity(-12, "World"), TransformState.Identity);
            Assert(readiness.Spawn == TransformState.Identity && readiness.Scene.InstanceId == -12 && readiness.RequestedSpawn == null,
                "A validated real spawn may be at the origin and scene handles may be negative.");
        }

        private static void RequestedAndSettledRemainDistinct()
        {
            var requested = TransformState.Identity;
            var settled = new TransformState(new Vec3(0, 0.13f, 0), Quat.Identity, new Vec3(1, 1, 1));
            var readiness = new WorldReadiness(new WorldSceneIdentity(4, "World"), settled, requested);
            Assert(readiness.Spawn == settled && readiness.RequestedSpawn == requested,
                "Accepted placement retains the +0.13 m settle without changing the provider request.");
            var unknown = new WorldReadiness(readiness.Scene, settled, (TransformState?)null);
            Assert(unknown.RequestedSpawn == null && unknown.Spawn == settled, "Unknown requests remain unknown.");
            using var map = new FakeAuthoredMap();
            var legacyMap = new WorldReadiness(readiness.Scene, settled, map);
            Assert(legacyMap.RequestedSpawn == null, "The legacy map constructor must not invent a request.");
        }

        private static void RequestedSpawnIsValidated()
        {
            foreach (var invalid in new[] { default(TransformState) })
            {
                try { _ = new WorldReadiness(new WorldSceneIdentity(1, "World"), TransformState.Identity, invalid); }
                catch (ArgumentException error) when (error.ParamName == "requestedSpawn") { continue; }
                throw new InvalidOperationException("A known request needs a valid transform.");
            }
        }

        private static void TestingMapPreservesBothPoses()
        {
            var settled = new TransformState(new Vec3(0, 0.13f, 0), Quat.Identity, new Vec3(1, 1, 1));
            foreach (var requested in new TransformState?[] { null, TransformState.Identity })
            {
                var world = new WorldReadiness(new WorldSceneIdentity(4, "World"), settled, requested);
                using var runner = new GamemodeSessionRunner(new EmptyGamemode(), world: world);
                using var map = new FakeAuthoredMap();
                runner.WithMap(map);
                Assert(runner.World.Spawn == settled && runner.World.RequestedSpawn == requested && ReferenceEquals(runner.World.Map, map),
                    "Adding a testing map preserves known or unknown requests without replacing the settled pose.");
            }
        }

        private sealed class EmptyGamemode : IGamemodeFactory
        {
            public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, System.Threading.CancellationToken token)
                => throw new NotSupportedException();
        }

        private static void InvalidProviderCannotStartGameplay(string root)
        {
            using var fixture = new SessionFixture(root);
            var world = new InvalidReadinessWorld();
            var released = 0; var modeConstructors = 0;
            SessionFixture.Load = (context, _) =>
            {
                context.Context.Lifetime.Defer(() => released++);
                return Task.FromResult(OperationResult<IWorldInstance>.Success(world));
            };
            SessionFixture.FactoryConstructor = () => modeConstructors++;
            var launch = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "invalid-provider-readiness");
            fixture.Wait(launch);
            if (!launch.Result.Succeeded) fixture.Until(() => fixture.Hosted.Current.Phase == SessionPhase.Idle);
            Assert(!launch.Result.Succeeded && modeConstructors == 0 && world.Disposals == 1 && released == 1,
                "Invalid provider readiness must fail before gameplay and release the returned world and allocated scope.");
        }

        private sealed class InvalidReadinessWorld : IWorldInstance
        {
            internal int Disposals;
            public WorldReadiness Readiness => new WorldReadiness(new WorldSceneIdentity(1, "World"), default);
            public void Dispose() { Disposals++; }
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
