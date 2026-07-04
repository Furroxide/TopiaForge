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
                // Lift the sky into physical daylight luminance so it holds its own against the 15k-lux sun;
                // at the default exposure of 0 the gradient is effectively black next to sunlit geometry.
                sky.exposure.value = 13.5f;

                // Fixed daylight exposure. Automatic metering sits between the sunlit ground and the (much
                // dimmer, pre-lift) sky and settles on a value that blows the ground out while crushing the
                // sky; with the sun and sky both tuned to real daylight levels a fixed EV is stable.
                var exposure = profile.Add<Exposure>(overrides: true);
                exposure.mode.value = ExposureMode.Fixed;
                // EV 14.5 ≈ bright daylight, matched to the 100k-lux sun so sunlit ground sits above
                // mid-grey without clipping while sky-lit shadows stay several stops darker.
                exposure.fixedExposure.value = 14.5f;

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
                // A scripted Light defaults to no shadows; without them (and with sky ambient as strong as
                // the sun) every face reads identically and the arena looks completely flat.
                light.shadows = LightShadows.Soft;
                light.shadowStrength = 1f;

                // Ensure HDRP's per-light data exists, then set a physical intensity. HDRP directional lights
                // are in lux. Real daylight (~100k lux) keeps the sun well above the sky-driven ambient
                // (~18k lux at the sky's 13.5 EV), so lit vs shadowed surfaces separate instead of merging.
                var hdData = sunObject.AddComponent<HDAdditionalLightData>();
                light.intensity = 100000f;
                hdData.SetShadowResolution(2048);
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not configure the HDRP sun: " + ex.Message);
            }
        }
    }
}
