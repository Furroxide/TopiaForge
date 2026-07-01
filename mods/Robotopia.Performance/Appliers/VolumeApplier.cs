using System;
using System.Reflection;
using Robotopia.Mods;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Robotopia.Performance.Appliers
{
    /// <summary>
    /// Injects ONE high-priority (1000) global HDRP override Volume that disables the expensive
    /// post-process / screen-space effects requested by config. Built like the Worlds mod's
    /// <c>HdrpEnvironment</c> but it overrides ONLY the specific parameters it intends to change
    /// (overrides:false + per-parameter <c>overrideState</c>), so it never replaces unrelated game
    /// values such as the level's fog colour or bloom intensity curve. The host GameObject is
    /// <c>DontDestroyOnLoad</c>, so the global volume persists across scene loads.
    /// </summary>
    internal sealed class VolumeApplier : PerfApplierBase
    {
        private const BindingFlags FieldFlags = BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly PerformanceConfig config;
        private readonly IModLogger logger;

        private GameObject? host;
        private VolumeProfile? profile;

        public VolumeApplier(PerformanceConfig config, IModLogger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public override string Name => "Volume";

        private bool AnyEnabled =>
            config.MotionBlurOff || config.DepthOfFieldOff || config.VignetteOff ||
            config.SsrOff || config.SsgiOff || config.SsaoOff || config.VolumetricFogOff ||
            config.FogOff || config.ContactShadowsOff || config.VolumetricCloudsOff ||
            config.LensFlareOff || config.BloomOff || config.BloomLowQuality;

        public override void Apply()
        {
            if (!AnyEnabled)
            {
                return;
            }

            Build();
        }

        public override void OnSceneLoaded(string sceneName)
        {
            // The host is DontDestroyOnLoad so it should survive, but re-create defensively if a scene
            // teardown removed it (e.g. a hard reload).
            if (AnyEnabled && (host == null || profile == null))
            {
                Build();
            }
        }

        public override void Revert()
        {
            try
            {
                if (host != null)
                {
                    UnityEngine.Object.Destroy(host);
                }

                // VolumeProfile + its component ScriptableObjects are not GC'd by Unity; destroy explicitly.
                Cleanup(profile);
            }
            catch
            {
                // Best-effort teardown.
            }
            finally
            {
                host = null;
                profile = null;
            }
        }

        private void Build()
        {
            try
            {
                Cleanup(profile);
                if (host != null)
                {
                    UnityEngine.Object.Destroy(host);
                    host = null;
                }

                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.hideFlags = HideFlags.DontSave;

                if (config.MotionBlurOff)
                {
                    var mb = profile.Add<MotionBlur>();
                    Set(mb.intensity, 0f);
                }

                if (config.DepthOfFieldOff)
                {
                    var dof = profile.Add<DepthOfField>();
                    Set(dof.focusMode, DepthOfFieldMode.Off);
                }

                if (config.VignetteOff)
                {
                    var vignette = profile.Add<Vignette>();
                    Set(vignette.intensity, 0f);
                }

                if (config.SsrOff)
                {
                    var ssr = profile.Add<ScreenSpaceReflection>();
                    Set(ssr.enabled, false);
                    Set(ssr.enabledTransparent, false);
                }

                if (config.SsgiOff)
                {
                    var ssgi = profile.Add<GlobalIllumination>();
                    Set(ssgi.enable, false);
                }

                if (config.SsaoOff)
                {
                    var ssao = profile.Add<ScreenSpaceAmbientOcclusion>();
                    Set(ssao.intensity, 0f);
                }

                if (config.VolumetricFogOff || config.FogOff)
                {
                    var fog = profile.Add<Fog>();
                    if (config.VolumetricFogOff || config.FogOff)
                    {
                        Set(fog.enableVolumetricFog, false);
                    }

                    if (config.FogOff)
                    {
                        Set(fog.enabled, false);
                    }
                }

                if (config.ContactShadowsOff)
                {
                    var cs = profile.Add<ContactShadows>();
                    Set(cs.enable, false);
                }

                if (config.VolumetricCloudsOff)
                {
                    var clouds = profile.Add<VolumetricClouds>();
                    Set(clouds.enable, false);
                }

                if (config.LensFlareOff)
                {
                    var flare = profile.Add<ScreenSpaceLensFlare>();
                    Set(flare.intensity, 0f);
                }

                if (config.BloomOff || config.BloomLowQuality)
                {
                    var bloom = profile.Add<Bloom>();
                    if (config.BloomOff)
                    {
                        Set(bloom.intensity, 0f);
                    }
                    else if (config.BloomLowQuality)
                    {
                        // Keep bloom, just make it cheaper. Bloom is a VolumeComponentWithQuality: its
                        // resolution/HQ getters only read the custom backing fields when the component is
                        // NOT in "use quality settings" mode, i.e. when quality.levelAndOverride.useOverride
                        // is true (which encodes value==3). So switch it to custom override first, mark
                        // quality overridden so the blend copies it, then set the cheaper values and flip
                        // their backing parameters' overrideState so the override actually wins the stack.
                        bloom.quality.levelAndOverride = (0, true);
                        bloom.quality.overrideState = true;
                        bloom.resolution = BloomResolution.Quarter;
                        bloom.highQualityFiltering = false;
                        bloom.highQualityPrefiltering = false;
                        EnableOverride(bloom, "m_Resolution");
                        EnableOverride(bloom, "m_HighQualityFiltering");
                        EnableOverride(bloom, "m_HighQualityPrefiltering");
                    }
                }

                host = new GameObject("Robotopia Performance Volume");
                UnityEngine.Object.DontDestroyOnLoad(host);
                var volume = host.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 1000f; // above game volumes (~0) and the Worlds mod (50)
                volume.sharedProfile = profile;

                logger.Info("Performance override volume active (post-FX kills applied).");
            }
            catch (Exception ex)
            {
                logger.Warn("Performance volume could not be built: " + ex.Message);
                Cleanup(profile);
                if (host != null)
                {
                    UnityEngine.Object.Destroy(host);
                }

                host = null;
                profile = null;
            }
        }

        private static void Set<T>(VolumeParameter<T> parameter, T value)
        {
            parameter.overrideState = true;
            parameter.value = value;
        }

        private static void EnableOverride(VolumeComponent component, string backingFieldName)
        {
            try
            {
                var field = component.GetType().GetField(backingFieldName, FieldFlags);
                if (field?.GetValue(component) is VolumeParameter parameter)
                {
                    parameter.overrideState = true;
                }
            }
            catch
            {
                // Non-fatal: the cheaper-bloom hint just won't apply.
            }
        }

        private static void Cleanup(VolumeProfile? toClean)
        {
            if (toClean == null)
            {
                return;
            }

            try
            {
                foreach (var component in toClean.components)
                {
                    if (component != null)
                    {
                        UnityEngine.Object.Destroy(component);
                    }
                }
            }
            catch
            {
                // Best-effort.
            }

            UnityEngine.Object.Destroy(toClean);
        }
    }
}
