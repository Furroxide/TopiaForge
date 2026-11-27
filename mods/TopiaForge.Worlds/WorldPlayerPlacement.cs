using System;

namespace TopiaForge.Worlds
{
    internal static class WorldPlayerPlacement
    {
        public static void PreserveControllerState(Func<bool> readEnabled, Action<bool> writeEnabled, Action move)
        {
            var wasEnabled = readEnabled();
            try
            {
                if (wasEnabled) writeEnabled(false);
                move();
            }
            finally { writeEnabled(wasEnabled); }
        }
    }
}
