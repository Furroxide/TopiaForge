using System;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class GeneratedArenaGeometryTests
    {
        public static void Run()
        {
            foreach (var spawnY in new[] { -10f, 0f, 25f })
            {
                var platformTop = GeneratedArenaGeometry.SpawnPlatformCenterY(spawnY) + GeneratedArenaGeometry.SpawnPlatformHeight / 2f;
                if (Math.Abs(platformTop - spawnY) > 0.00001f)
                    throw new InvalidOperationException("Generated spawn platform must end at the resolved spawn, not embed the player above it.");
            }
            Console.WriteLine("All generated arena geometry tests passed.");
        }
    }
}
