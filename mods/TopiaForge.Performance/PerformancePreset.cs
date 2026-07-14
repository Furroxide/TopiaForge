namespace TopiaForge.Performance
{
    /// <summary>
    /// Resolves the <c>performance_mode</c> preset onto the per-lever fields of a
    /// <see cref="PerformanceConfig"/>. Presets only touch the SAFE (a) and AGGRESSIVE (b) tiers;
    /// the RISKY (c) tier (fixed timestep, telemetry disable, AI budgets, numeric fps cap, render
    /// interval) is NEVER set by a preset — only by an explicit field edit, and is left untouched here.
    /// </summary>
    internal static class PerformancePreset
    {
        public static void Apply(PerformanceConfig c)
        {
            // override_manual lets advanced users hand-tune every field; the preset is then ignored.
            if (c.OverrideManual)
            {
                return;
            }

            switch (c.PerformanceMode)
            {
                case "off":
                    Off(c);
                    break;
                case "performance":
                    Performance(c);
                    break;
                case "potato":
                    Potato(c);
                    break;
                default: // "balanced"
                    Balanced(c);
                    break;
            }
        }

        // Everything neutral. The mod loads and applies nothing (still a clean no-op, still reversible).
        private static void Off(PerformanceConfig c)
        {
            Neutral(c);
        }

        // Free / near-free, fully reversible, no fidelity loss beyond cosmetic blur + DoF.
        private static void Balanced(PerformanceConfig c)
        {
            Neutral(c);
            c.MotionBlurOff = true;
            c.DepthOfFieldOff = true;
            c.VSyncCount = 1;
            c.ReuseCollisionCallbacks = true;
            c.StripLogStackTraces = true;
            c.SentryQuiet = true;
        }

        // Balanced + the screen-space passes + dynamic resolution + reflection probes. Keeps the high
        // quality level so geometry/textures stay sharp; the expensive per-pixel passes die.
        private static void Performance(PerformanceConfig c)
        {
            Balanced(c);
            c.SsrOff = true;
            c.SsaoOff = true;
            c.VolumetricFogOff = true;
            c.ContactShadowsOff = true;
            c.SsgiOff = true;            // guards (default-off in HDRP; harmless if absent)
            c.VolumetricCloudsOff = true;
            c.LensFlareOff = true;
            c.ReflectionProbePool = 0;
            c.DynamicResolutionEnabled = true;
            c.DynamicResolutionPercent = 70;
            c.LodBias = 0.8f;
        }

        // Performance + force the low quality level + harder downscale + mips + fog off + bloom off.
        private static void Potato(PerformanceConfig c)
        {
            Performance(c);
            c.ForceQualityLevel1 = true;
            c.DynamicResolutionPercent = 55;
            c.FogOff = true;
            c.BloomOff = true;
            c.BloomLowQuality = true;
            c.VignetteOff = true;
            c.GlobalMipLimit = 1;
            c.AnisotropicDisable = true;
            c.LodBias = 0.6f;
        }

        // Reset only the preset-owned (a + b) levers; the risky (c) tier is untouched.
        private static void Neutral(PerformanceConfig c)
        {
            c.MotionBlurOff = false;
            c.DepthOfFieldOff = false;
            c.VignetteOff = false;
            c.VSyncCount = -1;
            c.ReuseCollisionCallbacks = false;
            c.StripLogStackTraces = false;
            c.SentryQuiet = false;

            c.ForceQualityLevel1 = false;
            c.DynamicResolutionEnabled = false;
            c.ReflectionProbePool = -1;

            c.SsrOff = false;
            c.SsgiOff = false;
            c.SsaoOff = false;
            c.VolumetricFogOff = false;
            c.FogOff = false;
            c.ContactShadowsOff = false;
            c.VolumetricCloudsOff = false;
            c.LensFlareOff = false;
            c.BloomOff = false;
            c.BloomLowQuality = false;

            c.LodBias = 0f;
            c.GlobalMipLimit = 0;
            c.AnisotropicDisable = false;
            c.ParticleRaycastBudget = 0;

            c.AssetRebuildAllowed = false;
            c.ShadowAtlasResolution = 0;
            c.DisableVolumetricsSsgiAsset = false;

            // NOTE: gpu_occlusion_culling / gpu_small_mesh_screen_percentage are intentionally NOT reset
            // here. Although they physically sit in the asset-rebuild config block, they are treated as
            // (c)-tier risky/manual-only levers (like target_frame_rate / fixed_delta_time): no preset ever
            // sets them and SeedDefaults seeds them off, so a preset can never auto-enable the GPU Resident
            // Drawer. Leave them under explicit user control only.
        }
    }
}
