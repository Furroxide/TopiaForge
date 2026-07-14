using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// EventSystem management: reuse the game's if one exists, otherwise create one
    /// with the input module matching the active backend (StandaloneInputModule under
    /// legacy/both; InputSystemUIInputModule when legacy input is disabled).
    /// </summary>
    public static class QwEventSystems
    {
        private static GameObject? ownedEventSystem;

        public static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null)
            {
                return;
            }

            var go = new GameObject("QuantumWorksEventSystem");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
            ownedEventSystem = go;

            if (QwInput.LegacyAvailable)
            {
                go.AddComponent<StandaloneInputModule>();
                QwLog.Info("Created EventSystem with StandaloneInputModule.");
            }
            else
            {
                go.AddComponent<InputSystemUIInputModule>();
                QwLog.Info("Created EventSystem with InputSystemUIInputModule (InputSystem-only mode).");
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
