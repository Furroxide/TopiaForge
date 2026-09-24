using System;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldPlayerPlacementTests
    {
        public static void Run()
        {
            foreach (var originallyEnabled in new[] { false, true })
            {
                var enabled = originallyEnabled;
                var moves = 0;
                try
                {
                    WorldPlayerPlacement.PreserveControllerState(() => enabled, value => enabled = value,
                        () => { moves++; if (enabled) throw new InvalidOperationException("controller was still enabled"); throw new ApplicationException("move failure"); });
                    throw new InvalidOperationException("movement failure was swallowed");
                }
                catch (ApplicationException) { }
                if (enabled != originallyEnabled || moves != 1) throw new InvalidOperationException("throwing movement changed the original controller state");
            }
            Console.WriteLine("All world player placement tests passed.");
        }
    }
}
