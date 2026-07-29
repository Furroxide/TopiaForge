using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// The kit's single hidden driver MonoBehaviour (the loader dedupes this assembly
    /// process-wide, so exactly one exists). Ticks the cursor hold, and — in later
    /// milestones — the tween pool, toast queue, ESC stack, and hotkey poll.
    /// </summary>
    internal sealed class TopiaForgeRuntime : MonoBehaviour
    {
        private static TopiaForgeRuntime? instance;

        public static TopiaForgeRuntime Instance
        {
            get
            {
                if (instance == null)
                {
                    var go = new GameObject("TopiaForgeUiRuntime");
                    go.hideFlags = HideFlags.HideAndDontSave;
                    Object.DontDestroyOnLoad(go);
                    instance = go.AddComponent<TopiaForgeRuntime>();
                }

                return instance;
            }
        }

        /// <summary>Ensures the driver exists (call from any kit entry point).</summary>
        public static void Ensure()
        {
            _ = Instance;
        }

        /// <summary>Stops and destroys the hidden driver. Safe to call repeatedly.</summary>
        internal static void Shutdown()
        {
            var current = instance;
            instance = null;
            if (current != null)
            {
                current.enabled = false;
                Object.Destroy(current.gameObject);
            }
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(instance, this))
            {
                instance = null;
            }
        }

        private void Update()
        {
            // The game re-asserts its own cursor lock every frame, so the lease must
            // fight back every frame while held (proven by the Zombies modal behavior).
            TopiaForgeCursor.Tick();
            TopiaForgeTween.Tick(Time.unscaledDeltaTime);
            TopiaForgeDismissStack.TickEscape();
            TopiaForgeHotkeys.Tick();
            TopiaForgeToasts.Tick(Time.unscaledDeltaTime);
        }
    }
}
