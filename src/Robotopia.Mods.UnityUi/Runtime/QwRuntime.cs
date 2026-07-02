using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// The kit's single hidden driver MonoBehaviour (the loader dedupes this assembly
    /// process-wide, so exactly one exists). Ticks the cursor hold, and — in later
    /// milestones — the tween pool, toast queue, ESC stack, and hotkey poll.
    /// </summary>
    internal sealed class QwRuntime : MonoBehaviour
    {
        private static QwRuntime? instance;

        public static QwRuntime Instance
        {
            get
            {
                if (instance == null)
                {
                    var go = new GameObject("QuantumWorksUiRuntime");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    Object.DontDestroyOnLoad(go);
                    instance = go.AddComponent<QwRuntime>();
                }

                return instance;
            }
        }

        /// <summary>Ensures the driver exists (call from any kit entry point).</summary>
        public static void Ensure()
        {
            _ = Instance;
        }

        private void Update()
        {
            // The game re-asserts its own cursor lock every frame, so the lease must
            // fight back every frame while held (proven by the Zombies modal behavior).
            QwCursor.Tick();
        }
    }
}
