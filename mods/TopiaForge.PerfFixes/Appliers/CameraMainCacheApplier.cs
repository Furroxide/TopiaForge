using System.Reflection;
using HarmonyLib;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.PerfFixes.Appliers
{
    /// <summary>
    /// FIX 2 — cache <c>Camera.main</c> per frame. <c>Camera.main</c> is NOT cached by Unity: each get does
    /// a native <c>FindGameObjectsWithTag("MainCamera")</c> scan. The game funnels its per-frame camera
    /// reads through <c>CameraUtils.TryGetMainCamera</c> (LightUpdater, depth-of-field, every LookAtCamera
    /// billboard, the reflection-probe manager). A prefix memoizes the result keyed on
    /// <c>Time.frameCount</c>: the main camera is invariant within a frame (no camera switching anywhere in
    /// the game), so returning the cached reference is behavior-identical — it just collapses many native
    /// tag-scans into one per frame. The Unity fake-null re-check honours a destroyed/swapped camera.
    /// </summary>
    internal sealed class CameraMainCacheApplier : PerfApplierBase
    {
        private static Camera? cachedCamera;
        private static int cachedFrame = -1;
        private static bool active;

        private readonly PerfFixesConfig config;
        private readonly IModLogger logger;
        private readonly Harmony harmony;

        public CameraMainCacheApplier(PerfFixesConfig config, IModLogger logger, Harmony harmony)
        {
            this.config = config;
            this.logger = logger;
            this.harmony = harmony;
        }

        public override string Name => "CameraMainCache";

        public override void Apply()
        {
            // Self-correct the persisted static: a disabled config must force the fix off regardless of any
            // state left by a prior load (the assembly never unloads under Mono).
            active = false;
            cachedFrame = -1;
            cachedCamera = null;

            if (!config.CameraMainCache)
            {
                return;
            }

            var prefix = PatchUtil.Own(typeof(CameraMainCacheApplier), nameof(TryGetMainCameraPrefix));
            if (PatchUtil.TryPatchPrefix(harmony, logger, "CameraUtils", "TryGetMainCamera", null, prefix))
            {
                active = true;
                logger.Info("PerfFixes: Camera.main is now resolved once per frame.");
            }
        }

        public override void OnSceneLoaded(string sceneName)
        {
            // Force a fresh resolve on the next call after a scene change.
            cachedFrame = -1;
        }

        public override void Revert()
        {
            // Patch removal is centralized in the mod's UnpatchSelf; just stop serving the cache.
            active = false;
            cachedFrame = -1;
            cachedCamera = null;
        }

        // Mirrors CameraUtils.TryGetMainCamera: `camera = Camera.main; return camera != null;`
        private static bool TryGetMainCameraPrefix(ref Camera camera, ref bool __result)
        {
            if (!active)
            {
                return true; // run the original
            }

            var frame = Time.frameCount;
            if (cachedFrame != frame || cachedCamera == null)
            {
                cachedCamera = Camera.main;
                cachedFrame = frame;
            }

            camera = cachedCamera!;
            __result = cachedCamera != null;
            return false; // skip the original
        }
    }
}
