using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// The kit's single hidden driver MonoBehaviour (the loader dedupes this assembly
    /// process-wide, so exactly one exists). Ticks the cursor hold, the tween pool, the
    /// Back stack, the game's navigate bindings, the kit's own navigation, the mouse on
    /// kit surfaces, scroll views following the selection, the hotkey poll and the toast
    /// queue. While it runs, TopiaForge keeps what its surfaces claim away from the game;
    /// when it stops, it lets go of everything and puts back what it turned on.
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

        private void OnEnable()
        {
            TopiaForgeInputOwnership.Install();
        }

        private void OnDisable()
        {
            TopiaForgeInputOwnership.Reset();
            TopiaForgePointer.Reset();
            TopiaForgeScrollFollow.Reset();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            // The Input System releases every key when the game loses focus, and a release while unfocused never arrives.
            if (!hasFocus)
            {
                TopiaForgeInputOwnership.ForgetHeld();
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
            TopiaForgeEventSystems.Tick();
            TopiaForgeTween.Tick(Time.unscaledDeltaTime);
            TopiaForgeDismissStack.TickEscape();
            TopiaForgeNavigateAction.Follow();
            TopiaForgeNavigation.Tick(Time.unscaledDeltaTime);
            TopiaForgePointer.Tick();
            TopiaForgeScrollFollow.Tick();
            TopiaForgeHotkeys.Tick();
            TopiaForgeToasts.Tick(Time.unscaledDeltaTime);
        }
    }
}
