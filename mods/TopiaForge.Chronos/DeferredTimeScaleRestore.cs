using System;
using UnityEngine;

namespace TopiaForge.Chronos
{
    /// <summary>
    /// Completes Chronos' clock handoff when the mod is unloaded behind Robotopia's native pause. The native pause
    /// remembers the pre-pause scale, so restoring immediately would lift the menu while doing nothing would let it
    /// restore a stale Chronos slow/freeze later. This tiny process-lifetime watcher waits for pause release and only
    /// writes the baseline if the clock still contains the exact scale Chronos owned.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class DeferredTimeScaleRestore : MonoBehaviour
    {
        private static DeferredTimeScaleRestore? active;
        private static bool lifecycleInitialized;
        private static bool applicationQuitting;

        private Component? exactPauseRoot;
        private bool hasExactPauseRoot;
        private float ownedScale;
        private bool completing;

        public static void InitializeLifecycle()
        {
            if (lifecycleInitialized)
            {
                return;
            }

            lifecycleInitialized = true;
            Application.quitting += OnApplicationQuitting;
        }

        public static bool Begin(Component? exactPauseRoot, float ownedScale)
        {
            if (applicationQuitting)
            {
                return false;
            }

            // There can be only one Chronos provider. If a previous unload is already waiting on the same native
            // pause, its earlier pre-pause scale is the value Robotopia will restore; retain that handoff.
            if (active != null)
            {
                return true;
            }

            GameObject? host = null;
            try
            {
                host = new GameObject("TopiaForge.Chronos.DeferredTimeScaleRestore")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(host);
                var watcher = host.AddComponent<DeferredTimeScaleRestore>();
                watcher.exactPauseRoot = exactPauseRoot;
                watcher.hasExactPauseRoot = exactPauseRoot != null;
                watcher.ownedScale = ownedScale;
                active = watcher;
                return true;
            }
            catch
            {
                if (host != null)
                {
                    Destroy(host);
                }

                throw;
            }
        }

        /// <summary>A newly loaded Chronos instance has taken clock ownership, superseding the pending baseline.</summary>
        public static void CancelForActiveOwner()
        {
            if (active != null)
            {
                active.Complete(restoreBaseline: false);
            }
        }

        private void Update()
        {
            if (completing)
            {
                return;
            }

            var exactPauseActive = exactPauseRoot != null && exactPauseRoot.gameObject.activeInHierarchy;
            var action = TimeScaleOwnership.PlanDeferredRestore(
                hasExactPauseRoot,
                exactPauseActive,
                ownedScale,
                Time.timeScale);
            if (action == DeferredScaleRestoreAction.Wait)
            {
                return;
            }

            if (action == DeferredScaleRestoreAction.Abandon)
            {
                Complete(restoreBaseline: false);
                return;
            }

            // Robotopia's SetActive/OnDisable restore is synchronous before this Update. A newly loaded Chronos
            // instance cancels this watcher before either of its direct native writes.
            Complete(restoreBaseline: true);
        }

        private void Complete(bool restoreBaseline)
        {
            if (completing)
            {
                return;
            }

            completing = true;
            enabled = false;
            if (restoreBaseline)
            {
                var action = TimeScaleOwnership.PlanDeferredRestore(
                    hasExactPauseRoot,
                    exactNativePauseActive: false,
                    ownedScale,
                    Time.timeScale);
                if (action == DeferredScaleRestoreAction.RestoreBaseline)
                {
                    Time.timeScale = 1f;
                }
            }

            exactPauseRoot = null;
            if (ReferenceEquals(active, this))
            {
                active = null;
            }

            Destroy(gameObject);
        }

        private void OnApplicationQuit()
        {
            Complete(restoreBaseline: false);
        }

        private static void OnApplicationQuitting()
        {
            applicationQuitting = true;
            if (active != null)
            {
                active.Complete(restoreBaseline: false);
            }
        }

        private void OnDestroy()
        {
            exactPauseRoot = null;
            if (ReferenceEquals(active, this))
            {
                active = null;
            }
        }
    }
}
