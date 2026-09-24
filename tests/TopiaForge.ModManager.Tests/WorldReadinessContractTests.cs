using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldReadinessContractTests
    {
        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            foreach (var test in new Action[] { DefaultTransformIsRejected, ActualOriginRemainsLegal,
                () => InvalidProviderCannotStartGameplay(root) })
            {
                try { test(); } catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 0) throw new AggregateException(failures);
            Console.WriteLine("World readiness contract tests passed (3 cases).");
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
            Assert(readiness.Spawn == TransformState.Identity && readiness.Scene.InstanceId == -12,
                "A validated real spawn may be at the origin and scene handles may be negative.");
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
