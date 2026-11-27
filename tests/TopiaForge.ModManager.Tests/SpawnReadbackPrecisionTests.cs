using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class SpawnReadbackPrecisionTests
    {
        internal static void Run()
        {
            var requested = new TransformState(new Vec3(3, 5, 7), new Quat(0, 1, 0, 1), new Vec3(1, 1, 1));
            var readback = requested;
            for (var index = 0; index < 8; index++)
            {
                readback = new TransformState(readback.Position, readback.Rotation, readback.Scale);
                Assert(readback == requested && readback.GetHashCode() == requested.GetHashCode(),
                    "Unchanged native readback retains the exact normalized spawn rotation and transform hash.");
            }

            var actual = new TransformState(new Vec3(3, 5.13f, 7), new Quat(1, 0, 0, 1), new Vec3(2, 2, 2));
            var accepted = new TransformState(actual.Position, actual.Rotation, actual.Scale);
            var readiness = new WorldReadiness(new WorldSceneIdentity(-17, "Scene"), accepted, requested);
            Assert(readiness.Spawn == actual && readiness.RequestedSpawn == requested && readiness.Spawn != requested,
                "Readback precision preserves the actual settled position, rotation and scale separately from the request.");
            Assert(Quat.Identity.Normalized == Quat.Identity && default(Quat).Normalized == Quat.Identity
                && new Quat(0, 0, 0, 0.0000001f).Normalized == Quat.Identity,
                "Identity and the existing near-zero normalization fallback remain unchanged.");
            var large = new TransformState(requested.Position,
                new Quat(0, float.MaxValue, 0, float.MaxValue), requested.Scale);
            Assert(large.Rotation.IsFinite && large.Rotation == requested.Rotation,
                "A large finite spawn rotation normalizes without float squared-norm overflow.");
            foreach (var component in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                try
                {
                    _ = new TransformState(requested.Position, new Quat(component, 0, 0, 1), requested.Scale);
                }
                catch (ArgumentException) { continue; }
                throw new InvalidOperationException("Nonfinite readback rotations remain invalid transform evidence.");
            }
            Console.WriteLine("Spawn readback precision tests passed.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
