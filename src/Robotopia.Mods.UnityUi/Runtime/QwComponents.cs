using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>Unity-null-safe component lookup shared by all kit widgets.</summary>
    internal static class QwComponents
    {
        public static T GetOrAdd<T>(GameObject owner) where T : Component
        {
            var existing = owner.GetComponent<T>();
            return existing != null ? existing : owner.AddComponent<T>();
        }
    }
}
