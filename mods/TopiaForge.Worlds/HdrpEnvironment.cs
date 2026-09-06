using System;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace TopiaForge.Worlds
{
    internal static class HdrpEnvironment
    {
        // Track each native object before configuration. Destroying a GameObject does not destroy profile assets.
        public static void Apply(GameObject root, WorldResourceScope resources)
        {
            resources.ThrowIfStopping();
            var profile = UnityWorldResources.Own(resources, ScriptableObject.CreateInstance<VolumeProfile>());
            profile.hideFlags = HideFlags.DontSave;
            var environment = UnityWorldResources.Own(resources, profile.Add<VisualEnvironment>(overrides: true));
            environment.skyType.value = (int)SkyType.Gradient;
            environment.skyAmbientMode.value = SkyAmbientMode.Dynamic;
            var sky = UnityWorldResources.Own(resources, profile.Add<GradientSky>(overrides: true));
            sky.top.value = new Color(0.20f, 0.42f, 0.78f);
            sky.middle.value = new Color(0.55f, 0.62f, 0.72f);
            sky.bottom.value = new Color(0.32f, 0.33f, 0.36f);
            sky.exposure.value = 13.5f;
            var exposure = UnityWorldResources.Own(resources, profile.Add<Exposure>(overrides: true));
            exposure.mode.value = ExposureMode.Fixed;
            exposure.fixedExposure.value = 14.5f;
            var tonemapping = UnityWorldResources.Own(resources, profile.Add<Tonemapping>(overrides: true));
            tonemapping.mode.value = TonemappingMode.Neutral;
            var volumeObject = UnityWorldResources.Own(resources, new GameObject("Worlds HDRP Environment"));
            volumeObject.transform.SetParent(root.transform, false);
            var volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 50f;
            volume.sharedProfile = profile;
            ConfigureSun(root, resources);
        }
        public static void ApplyLegacy(GameObject root, WorldResourceScope resources, IModLogger logger)
        {
            try { Apply(root, resources); }
            catch (Exception error) { logger.Warn("Worlds could not apply the HDRP environment: " + error.Message); }
        }
        private static void ConfigureSun(GameObject root, WorldResourceScope resources)
        {
            var sun = UnityWorldResources.Own(resources, new GameObject("Sandbox Sun"));
            sun.transform.SetParent(root.transform, false);
            sun.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 1f;
            var hdData = sun.AddComponent<HDAdditionalLightData>();
            light.intensity = 100000f;
            hdData.SetShadowResolution(2048);
        }
    }
}
