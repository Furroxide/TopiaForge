using System;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // A fresh builder parent per actor. Native owners remain in the subtree that created their assets.
    // Never attach this to a map root, prefab asset, or an existing runtime asset owner.
    internal sealed class RobotComposedLifetime : MonoBehaviour, IDisposable
    {
        internal GameObject? Actor;
        internal Func<bool>? Current;
        private Type? ownerType;
        private System.Reflection.MethodInfo? destroyAssets;
        private bool released;

        internal void Initialize(Type type, System.Reflection.MethodInfo destroy)
        { ownerType = type; destroyAssets = destroy; }

        internal static void Activate(GameObject actor, Transform parent)
        {
            var lease = actor.transform.parent?.GetComponent<RobotComposedLifetime>();
            if (lease == null)
            {
                actor.transform.SetParent(parent, true);
                actor.SetActive(true);
                return;
            }
            lease.transform.SetParent(parent, true);
            actor.SetActive(true);
            lease.gameObject.SetActive(true);
        }

        internal static void DestroyActor(GameObject actor)
        {
            var lease = actor.transform.parent?.GetComponent<RobotComposedLifetime>();
            if (lease != null && lease.Actor == actor) lease.Dispose();
            else UnityEngine.Object.Destroy(actor);
        }

        private void Update()
        {
            try { if (Actor == null || Current?.Invoke() == false) Dispose(); }
            catch (Exception) { Dispose(); }
        }
        private void OnDestroy() { ReleaseAssets(); }

        public void Dispose()
        {
            if (released) return;
            gameObject.SetActive(false);
            ReleaseAssets();
            UnityEngine.Object.Destroy(gameObject);
        }

        private void ReleaseAssets()
        {
            if (released) return;
            released = true;
            if (ownerType == null || destroyAssets == null) return;
            foreach (var owner in GetComponentsInChildren(ownerType, true))
            {
                try { destroyAssets.Invoke(owner, Array.Empty<object>()); }
                catch (Exception ex) { RobotKitDiagnostics.ReportOnce("composed robot asset cleanup", ex); }
            }
        }
    }
}
