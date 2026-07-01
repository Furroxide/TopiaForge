using System;
using Robotopia.Mods;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Robotopia.Worlds
{
    /// <summary>
    /// Robotopia renders with HDRP, where the entire look (sky, exposure, tonemapping) comes from a global
    /// Volume. A hand-built arena has none, so it renders flat and washed out. This attaches a sensible
    /// global Volume plus a physically-lit sun so the sandbox arena looks correct without a baked scene.
    /// </summary>
    internal static class HdrpEnvironment
    {
        /// <summary>Builds the global Volume and returns its profile so the caller can destroy it on teardown
        /// (ScriptableObjects are NOT destroyed when their GameObject is destroyed).</summary>
        public static VolumeProfile? Apply(GameObject root, IModLogger logger)
        {
            VolumeProfile? profile = null;
            try
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.hideFlags = HideFlags.DontSave;

                var visualEnvironment = profile.Add<VisualEnvironment>(overrides: true);
                visualEnvironment.skyType.value = (int)SkyType.Gradient;
                visualEnvironment.skyAmbientMode.value = SkyAmbientMode.Dynamic;

                var sky = profile.Add<GradientSky>(overrides: true);
                sky.top.value = new Color(0.20f, 0.42f, 0.78f);
                sky.middle.value = new Color(0.55f, 0.62f, 0.72f);
                sky.bottom.value = new Color(0.32f, 0.33f, 0.36f);

                // Automatic exposure adapts to whatever brightness the arena ends up at, so the result is
                // never blown out or black even though we cannot tune a fixed EV against a baked scene.
                var exposure = profile.Add<Exposure>(overrides: true);
                exposure.mode.value = ExposureMode.Automatic;
                exposure.meteringMode.value = MeteringMode.CenterWeighted;

                var tonemapping = profile.Add<Tonemapping>(overrides: true);
                tonemapping.mode.value = TonemappingMode.Neutral;

                var volumeObject = new GameObject("Worlds HDRP Environment");
                volumeObject.transform.SetParent(root.transform, false);
                var volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 50f;
                volume.sharedProfile = profile;

                ConfigureSun(root, logger);
                return profile;
            }
            catch (Exception ex)
            {
                logger.Warn("Worlds could not apply the HDRP environment (colours may look flat): " + ex.Message);

                // Destroy the partially-built profile (and any component ScriptableObjects already added) so a
                // failed Apply does not orphan native objects. ScriptableObjects are not GC'd by Unity, and mod
                // assemblies never unload under Mono, so a leak here would accumulate across every failed build.
                Cleanup(profile);
                return null;
            }
        }

        /// <summary>Destroys a profile (and its volume-component ScriptableObjects) created by Apply.</summary>
        public static void Cleanup(VolumeProfile? profile)
        {
            if (profile == null)
            {
                return;
            }

            try
            {
                foreach (var component in profile.components)
                {
                    if (component != null)
                    {
                        UnityEngine.Object.Destroy(component);
                    }
                }
            }
            catch
            {
                // Best-effort teardown.
            }

            UnityEngine.Object.Destroy(profile);
        }

        private static void ConfigureSun(GameObject root, IModLogger logger)
        {
            try
            {
                var sunObject = new GameObject("Sandbox Sun");
                sunObject.transform.SetParent(root.transform, false);
                sunObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                var light = sunObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = new Color(1f, 0.96f, 0.9f);

                // Ensure HDRP's per-light data exists, then set a physical intensity. HDRP directional lights
                // are in lux; the legacy 1.2 was effectively black. Automatic exposure tolerates the exact value.
                sunObject.AddComponent<HDAdditionalLightData>();
                light.intensity = 15000f;
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not configure the HDRP sun: " + ex.Message);
            }
        }
    }
}
