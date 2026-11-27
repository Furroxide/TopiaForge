using System;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    internal static class UnityWorldScene
    {
        public static bool TryGetLoaded(WorldSceneIdentity identity, out Scene scene)
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var candidate = SceneManager.GetSceneAt(index);
                if (candidate.IsValid() && candidate.isLoaded && candidate.handle == identity.InstanceId
                    && string.Equals(candidate.name, identity.Name, StringComparison.Ordinal))
                {
                    scene = candidate;
                    return true;
                }
            }
            scene = default;
            return false;
        }

        public static bool ContainsRoot(WorldSceneIdentity identity, GameObject root)
        {
            return root != null && TryGetLoaded(identity, out var scene)
                && root.scene.handle == scene.handle;
        }
    }
}
