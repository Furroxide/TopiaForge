using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace TopiaForge.Performance.Appliers
{
    /// <summary>
    /// Dynamic resolution on the main camera is gated by HDRP's per-frame camera request, which is
    /// driven by <c>Camera.allowDynamicResolution</c> and <c>HDAdditionalCameraData.allowDynamicResolution</c>.
    /// The game never sets either, so the asset-level <see cref="AssetApplier"/> change is inert without
    /// this. We set both flags on Game cameras (and keep them set as cameras are created), capturing the
    /// originals for a clean revert.
    /// </summary>
    internal sealed class CameraDynResApplier : PerfApplierBase
    {
        private const int CheckEveryFrames = 30;

        private readonly PerformanceConfig config;
        private readonly IModLogger logger;
        private readonly Dictionary<Camera, bool> cameraOriginals = new Dictionary<Camera, bool>();
        private readonly Dictionary<HDAdditionalCameraData, bool> hdOriginals = new Dictionary<HDAdditionalCameraData, bool>();

        private int frameCounter;

        public CameraDynResApplier(PerformanceConfig config, IModLogger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public override string Name => "CameraDynRes";

        public override void Apply()
        {
            if (config.DynamicResolutionEnabled)
            {
                EnsureFlags();
            }
        }

        public override void OnSceneLoaded(string sceneName)
        {
            if (config.DynamicResolutionEnabled)
            {
                EnsureFlags();
            }
        }

        public override void OnUpdate(float deltaTime)
        {
            if (!config.DynamicResolutionEnabled)
            {
                return;
            }

            // Cheap throttle: cameras can be created mid-session, so re-assert periodically.
            if (++frameCounter < CheckEveryFrames)
            {
                return;
            }

            frameCounter = 0;
            EnsureFlags();
        }

        public override void Revert()
        {
            foreach (var pair in cameraOriginals)
            {
                try
                {
                    if (pair.Key != null)
                    {
                        pair.Key.allowDynamicResolution = pair.Value;
                    }
                }
                catch
                {
                    // Best-effort.
                }
            }

            foreach (var pair in hdOriginals)
            {
                try
                {
                    if (pair.Key != null)
                    {
                        pair.Key.allowDynamicResolution = pair.Value;
                    }
                }
                catch
                {
                    // Best-effort.
                }
            }

            cameraOriginals.Clear();
            hdOriginals.Clear();
        }

        private void EnsureFlags()
        {
            try
            {
                // Drop entries for cameras destroyed since we captured them (slow leak otherwise).
                GameReflectionLite.PruneDestroyed(cameraOriginals);
                GameReflectionLite.PruneDestroyed(hdOriginals);

                foreach (var camera in Camera.allCameras)
                {
                    if (camera == null || camera.cameraType != CameraType.Game)
                    {
                        continue;
                    }

                    if (!cameraOriginals.ContainsKey(camera))
                    {
                        cameraOriginals[camera] = camera.allowDynamicResolution;
                    }

                    camera.allowDynamicResolution = true;

                    var hd = camera.GetComponent<HDAdditionalCameraData>();
                    if (hd != null)
                    {
                        if (!hdOriginals.ContainsKey(hd))
                        {
                            hdOriginals[hd] = hd.allowDynamicResolution;
                        }

                        hd.allowDynamicResolution = true;
                    }
                }
            }
            catch
            {
                // Non-fatal: dynamic resolution just won't engage if cameras are unavailable.
            }
        }
    }
}
