using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Unity-null-safe component lookup shared by all kit widgets.</summary>
    internal static class TopiaForgeComponents
    {
        public static T GetOrAdd<T>(GameObject owner) where T : Component
        {
            var existing = owner.GetComponent<T>();
            return existing != null ? existing : owner.AddComponent<T>();
        }
    }
}
