using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Process-wide ref-counted cursor ownership. While any lease is held the cursor is
    /// re-asserted unlocked and visible EVERY frame (via QwRuntime) because the game
    /// re-locks it per frame; when the last lease releases, the saved state returns.
    /// </summary>
    public static class QwCursor
    {
        private static int activeLeases;
        private static CursorLockMode savedLockState;
        private static bool savedVisible;

        public static int ActiveLeases => activeLeases;

        internal static void AddLease()
        {
            if (activeLeases == 0)
            {
                savedLockState = Cursor.lockState;
                savedVisible = Cursor.visible;
                QwRuntime.Ensure();
            }

            activeLeases++;
            Assert();
        }

        internal static void RemoveLease()
        {
            if (activeLeases == 0)
            {
                return;
            }

            activeLeases--;
            if (activeLeases == 0)
            {
                Cursor.lockState = savedLockState;
                Cursor.visible = savedVisible;
            }
        }

        internal static void Tick()
        {
            if (activeLeases > 0)
            {
                Assert();
            }
        }

        private static void Assert()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    /// <summary>
    /// A single participant's handle on the shared cursor state. Mirrors the proven
    /// NeonCursorLease acquire/release semantics; windows and modals hold one
    /// automatically while visible.
    /// </summary>
    public sealed class QwCursorLease
    {
        private bool active;

        public void SetActive(bool shouldOwnCursor)
        {
            if (shouldOwnCursor)
            {
                Acquire();
            }
            else
            {
                Release();
            }
        }

        public void Acquire()
        {
            if (active)
            {
                return;
            }

            active = true;
            QwCursor.AddLease();
        }

        public void Release()
        {
            if (!active)
            {
                return;
            }

            active = false;
            QwCursor.RemoveLease();
        }
    }
}
