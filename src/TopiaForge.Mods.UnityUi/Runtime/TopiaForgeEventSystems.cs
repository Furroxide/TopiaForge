using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// EventSystem management: reuse the game's if one exists, otherwise create one
    /// with the input module matching the active backend (StandaloneInputModule under
    /// legacy/both; InputSystemUIInputModule when legacy input is disabled).
    /// </summary>
    public static class TopiaForgeEventSystems
    {
        private static GameObject? ownedEventSystem;

        /// <summary>Ensures that a compatible Unity event system is available.</summary>
        public static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null)
            {
                return;
            }

            var go = new GameObject("TopiaForgeEventSystem");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            ownedEventSystem = go;

            if (TopiaForgeInput.LegacyAvailable)
            {
                go.AddComponent<StandaloneInputModule>();
                TopiaForgeLog.Info("Created EventSystem with StandaloneInputModule.");
            }
            else
            {
                go.AddComponent<InputSystemUIInputModule>();
                TopiaForgeLog.Info("Created EventSystem with InputSystemUIInputModule (InputSystem-only mode).");
            }
        }

        internal static void Reset()
        {
            if (ownedEventSystem != null)
            {
                Object.Destroy(ownedEventSystem);
            }

            ownedEventSystem = null;
        }
    }
}
