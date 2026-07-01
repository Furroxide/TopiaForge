using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    public sealed class NeonCursorLease
    {
        private static int activeLeases;
        private static CursorLockMode savedLockState;
        private static bool savedVisible;

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
            if (!active)
            {
                if (activeLeases == 0)
                {
                    savedLockState = Cursor.lockState;
                    savedVisible = Cursor.visible;
                }

                activeLeases++;
                active = true;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Release()
        {
            if (!active)
            {
                return;
            }

            active = false;
            activeLeases = Mathf.Max(0, activeLeases - 1);
            if (activeLeases != 0)
            {
                return;
            }

            Cursor.lockState = savedLockState;
            Cursor.visible = savedVisible;
        }
    }
}
