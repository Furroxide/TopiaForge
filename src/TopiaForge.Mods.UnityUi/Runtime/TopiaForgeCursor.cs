using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Process-wide ref-counted cursor ownership. While any lease is held the cursor is
    /// re-asserted unlocked and visible EVERY frame (via TopiaForgeRuntime) because the game
    /// re-locks it per frame; when the last lease releases, the saved state returns.
    /// </summary>
    public static class TopiaForgeCursor
    {
        private static int activeLeases;
        private static CursorLockMode savedLockState;
        private static bool savedVisible;

        /// <summary>Gets active leases.</summary>
        public static int ActiveLeases => activeLeases;

        internal static void AddLease()
        {
            if (activeLeases == 0)
            {
                savedLockState = Cursor.lockState;
                savedVisible = Cursor.visible;
                TopiaForgeRuntime.Ensure();
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

        internal static void Reset()
        {
            if (activeLeases > 0)
            {
                Cursor.lockState = savedLockState;
                Cursor.visible = savedVisible;
            }

            activeLeases = 0;
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
    public sealed class TopiaForgeCursorLease
    {
        private bool active;

        /// <summary>Sets whether this cursor ownership token is active.</summary>
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

        /// <summary>Acquires an ownership token for this resource.</summary>
        public void Acquire()
        {
            if (active)
            {
                return;
            }

            active = true;
            TopiaForgeCursor.AddLease();
        }

        /// <summary>Releases a previously acquired ownership token.</summary>
        public void Release()
        {
            if (!active)
            {
                return;
            }

            active = false;
            TopiaForgeCursor.RemoveLease();
        }
    }
}
