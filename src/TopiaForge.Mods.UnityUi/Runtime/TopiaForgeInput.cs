using System;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Input backend detection. The game currently runs with both backends enabled
    /// (legacy Input works), but the kit never assumes it: the probe result decides
    /// which EventSystem module to add and how keys are read.
    /// </summary>
    public static class TopiaForgeInput
    {
        private static bool probed;
        private static bool legacyAvailable;

        /// <summary>True when UnityEngine.Input is usable (Active Input Handling includes legacy).</summary>
        public static bool LegacyAvailable
        {
            get
            {
                Probe();
                return legacyAvailable;
            }
        }

        /// <summary>Dual-backend Escape check used by the dismiss stack.</summary>
        public static bool EscapePressedThisFrame()
        {
            Probe();
            if (legacyAvailable)
            {
                return Input.GetKeyDown(KeyCode.Escape);
            }

            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            return keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
        }

        private static void Probe()
        {
            if (probed)
            {
                return;
            }

            probed = true;
            try
            {
                _ = Input.anyKey;
                legacyAvailable = true;
            }
            catch (InvalidOperationException)
            {
                legacyAvailable = false;
            }

            TopiaForgeLog.Info("Input backend probe: legacy input " + (legacyAvailable ? "available" : "DISABLED (InputSystem-only mode)") + ".");
        }
    }
}
