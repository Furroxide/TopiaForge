using System.Runtime.Serialization;
using TopiaForge.Mods;

namespace TopiaForge.Performance
{
    /// <summary>
    /// Configuration for the performance mod. Every lever is reversible. The serializer
    /// (<c>DataContractJsonSerializer</c>) builds the instance with
    /// <c>FormatterServices.GetUninitializedObject</c>, so it bypasses the constructor AND property
    /// initializers — defaults must be seeded in <see cref="SeedDefaults"/>, invoked from both the
    /// constructor (for the in-memory default passed to LoadConfig) and <c>[OnDeserializing]</c>
    /// (so fields absent from an existing JSON file still get real defaults).
    /// </summary>
    [DataContract]
    public sealed class PerformanceConfig : ISelfNormalizingConfig
    {
        public PerformanceConfig()
        {
            SeedDefaults();
        }

        // ----- (0) MASTER -----
        // "off" | "balanced" | "performance" | "potato". A preset rewrites the per-lever fields below
        // at load unless override_manual is true (advanced users who hand-tune individual fields).
        [DataMember(Name = "performance_mode")] public string PerformanceMode { get; set; } = "balanced";
        [DataMember(Name = "override_manual")] public bool OverrideManual { get; set; }

        // ----- (a) SAFE defaults, on in the "balanced" preset -----
        [DataMember(Name = "motion_blur_off")] public bool MotionBlurOff { get; set; } = true;
        [DataMember(Name = "depth_of_field_off")] public bool DepthOfFieldOff { get; set; } = true;
        [DataMember(Name = "vignette_off")] public bool VignetteOff { get; set; }
        [DataMember(Name = "vsync_count")] public int VSyncCount { get; set; } = 1;            // -1 = leave engine default
        [DataMember(Name = "reuse_collision_callbacks")] public bool ReuseCollisionCallbacks { get; set; } = true;
        [DataMember(Name = "strip_log_stack_traces")] public bool StripLogStackTraces { get; set; } = true;
        [DataMember(Name = "sentry_quiet")] public bool SentryQuiet { get; set; } = true;        // Debug=false, level=Error

        // ----- (b) AGGRESSIVE — on in "performance" / "potato" -----
        [DataMember(Name = "force_quality_level_1")] public bool ForceQualityLevel1 { get; set; }

        [DataMember(Name = "dynamic_resolution_enabled")] public bool DynamicResolutionEnabled { get; set; }
        [DataMember(Name = "dynamic_resolution_percent")] public int DynamicResolutionPercent { get; set; } = 70; // clamp 50..100

        [DataMember(Name = "reflection_probe_pool")] public int ReflectionProbePool { get; set; } = -1;          // -1 = leave; 0 = kill

        // Per-effect overrides via the injected high-priority Volume.
        [DataMember(Name = "ssr_off")] public bool SsrOff { get; set; }
        [DataMember(Name = "ssgi_off")] public bool SsgiOff { get; set; }
        [DataMember(Name = "ssao_off")] public bool SsaoOff { get; set; }
        [DataMember(Name = "volumetric_fog_off")] public bool VolumetricFogOff { get; set; }
        [DataMember(Name = "fog_off")] public bool FogOff { get; set; }
        [DataMember(Name = "contact_shadows_off")] public bool ContactShadowsOff { get; set; }
        [DataMember(Name = "volumetric_clouds_off")] public bool VolumetricCloudsOff { get; set; }
        [DataMember(Name = "lens_flare_off")] public bool LensFlareOff { get; set; }
        [DataMember(Name = "bloom_off")] public bool BloomOff { get; set; }
        [DataMember(Name = "bloom_low_quality")] public bool BloomLowQuality { get; set; }

        // Cheap QualitySettings fine-tune (re-asserted after the game's SetQualityLevel / scene loads).
        [DataMember(Name = "lod_bias")] public float LodBias { get; set; }                       // 0 = leave
        [DataMember(Name = "global_mip_limit")] public int GlobalMipLimit { get; set; }           // 0 = leave; 1 = drop one mip
        [DataMember(Name = "anisotropic_disable")] public bool AnisotropicDisable { get; set; }
        [DataMember(Name = "particle_raycast_budget")] public int ParticleRaycastBudget { get; set; } // 0 = leave

        // Asset-rebuild levers (force a one-time pipeline recreate => brief hitch). Gated.
        [DataMember(Name = "asset_rebuild_allowed")] public bool AssetRebuildAllowed { get; set; }
        [DataMember(Name = "shadow_atlas_resolution")] public int ShadowAtlasResolution { get; set; } // 0 = leave; e.g. 2048/1024
        [DataMember(Name = "disable_volumetrics_ssgi_asset")] public bool DisableVolumetricsSsgiAsset { get; set; }

        // GPU Resident Drawer / GPU occlusion culling (Unity 6 HDRP). When active, MeshRenderers that are
        // fully hidden behind other geometry are culled on the GPU before shading (frustum culling alone
        // does NOT remove things inside the view but behind a wall). Forces a one-time pipeline recreate,
        // so it ALSO requires asset_rebuild_allowed. EXPERIMENTAL, default off, never set by a preset.
        //
        // HARD SAFETY GATE: the drawer needs the build to KEEP the "BatchRendererGroup Variants" shader
        // variants (Graphics > Shader Stripping > "Keep All"). A build that shipped with the drawer off
        // (Robotopia did, and it ships no Entities Graphics) almost certainly STRIPPED them — and routing
        // meshes through the drawer then renders them pink/invisible. That failure is SILENT (no runtime
        // log) and there is NO runtime API to detect the strip setting. So gpu_occlusion_culling alone is
        // treated as intent only and will NOT engage: you must ALSO set gpu_occlusion_allow_unverified =
        // true to confirm you are on a "Keep All" build. Even then a best-effort log watchdog + the
        // engagement check run, but the real guarantee is this gate. Also may not honour every per-renderer
        // MaterialPropertyBlock, and flips the global USE_LEGACY_LIGHTMAPS keyword while active.
        [DataMember(Name = "gpu_occlusion_culling")] public bool GpuOcclusionCulling { get; set; }
        [DataMember(Name = "gpu_small_mesh_screen_percentage")] public float GpuSmallMeshScreenPercentage { get; set; } // 0 = off; also cull meshes smaller than this % of screen (needs the GRD, so implies the rebuild + the allow-unverified gate)
        [DataMember(Name = "gpu_occlusion_allow_unverified")] public bool GpuOcclusionAllowUnverified { get; set; } // REQUIRED to actually enable the GRD: "I am on a build with BatchRendererGroup Variants = Keep All"

        // ----- (c) EXPLICIT RISKY opt-in (never set by a preset) -----
        [DataMember(Name = "target_frame_rate")] public int TargetFrameRate { get; set; } = -1;   // -1 = uncapped
        [DataMember(Name = "render_frame_interval")] public int RenderFrameInterval { get; set; } = 1; // >1 = render every Nth frame

        [DataMember(Name = "fixed_delta_time")] public float FixedDeltaTime { get; set; }         // 0 = leave (0.0333 = 30Hz)
        [DataMember(Name = "solver_iterations")] public int SolverIterations { get; set; }         // 0 = leave; affects new bodies only

        [DataMember(Name = "disable_posthog")] public bool DisablePosthog { get; set; }            // privacy + tiny perf
        [DataMember(Name = "disable_sentry")] public bool DisableSentry { get; set; }              // drops crash reports!
        [DataMember(Name = "stop_perf_logger")] public bool StopPerfLogger { get; set; }

        // AI / nav budgets (behaviour-affecting => opt-in).
        [DataMember(Name = "shadow_refresh_rate")] public int ShadowRefreshRate { get; set; }      // 0 = leave (game default 2); higher spreads cost
        [DataMember(Name = "pathfind_budget_ms")] public float PathfindBudgetMs { get; set; }      // 0 = leave

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SeedDefaults();
        }

        private void SeedDefaults()
        {
            PerformanceMode = "balanced";
            OverrideManual = false;

            MotionBlurOff = true;
            DepthOfFieldOff = true;
            VignetteOff = false;
            VSyncCount = 1;
            ReuseCollisionCallbacks = true;
            StripLogStackTraces = true;
            SentryQuiet = true;

            ForceQualityLevel1 = false;
            DynamicResolutionEnabled = false;
            DynamicResolutionPercent = 70;
            ReflectionProbePool = -1;

            SsrOff = false;
            SsgiOff = false;
            SsaoOff = false;
            VolumetricFogOff = false;
            FogOff = false;
            ContactShadowsOff = false;
            VolumetricCloudsOff = false;
            LensFlareOff = false;
            BloomOff = false;
            BloomLowQuality = false;

            LodBias = 0f;
            GlobalMipLimit = 0;
            AnisotropicDisable = false;
            ParticleRaycastBudget = 0;

            AssetRebuildAllowed = false;
            ShadowAtlasResolution = 0;
            DisableVolumetricsSsgiAsset = false;
            GpuOcclusionCulling = false;
            GpuSmallMeshScreenPercentage = 0f;
            GpuOcclusionAllowUnverified = false;

            TargetFrameRate = -1;
            RenderFrameInterval = 1;
            FixedDeltaTime = 0f;
            SolverIterations = 0;
            DisablePosthog = false;
            DisableSentry = false;
            StopPerfLogger = false;

            ShadowRefreshRate = 0;
            PathfindBudgetMs = 0f;
        }

        /// <summary>Clamp values into safe ranges after load.</summary>
        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(PerformanceMode))
            {
                PerformanceMode = "balanced";
            }

            PerformanceMode = PerformanceMode.Trim().ToLowerInvariant();
            if (PerformanceMode != "off" && PerformanceMode != "balanced" &&
                PerformanceMode != "performance" && PerformanceMode != "potato")
            {
                PerformanceMode = "balanced";
            }

            if (DynamicResolutionPercent < 50) DynamicResolutionPercent = 50;
            if (DynamicResolutionPercent > 100) DynamicResolutionPercent = 100;

            if (VSyncCount < -1) VSyncCount = -1;
            if (VSyncCount > 4) VSyncCount = 4;

            if (RenderFrameInterval < 1) RenderFrameInterval = 1;
            if (RenderFrameInterval > 10) RenderFrameInterval = 10;

            if (GlobalMipLimit < 0) GlobalMipLimit = 0;
            if (GlobalMipLimit > 3) GlobalMipLimit = 3;

            if (ShadowAtlasResolution != 0)
            {
                if (ShadowAtlasResolution < 512) ShadowAtlasResolution = 512;
                if (ShadowAtlasResolution > 8192) ShadowAtlasResolution = 8192;
            }

            // Small-mesh culling: 0 = off. Cap well below the point where it would cull visibly large
            // objects (Unity's own UI tops out around 20%).
            if (!IsFinite(GpuSmallMeshScreenPercentage)) GpuSmallMeshScreenPercentage = 0f;
            if (GpuSmallMeshScreenPercentage < 0f) GpuSmallMeshScreenPercentage = 0f;
            if (GpuSmallMeshScreenPercentage > 20f) GpuSmallMeshScreenPercentage = 20f;

            if (!IsFinite(FixedDeltaTime)) FixedDeltaTime = 0f;
            if (FixedDeltaTime != 0f)
            {
                if (FixedDeltaTime < 0.005f) FixedDeltaTime = 0.005f; // never below 200 Hz
                if (FixedDeltaTime > 0.05f) FixedDeltaTime = 0.05f;   // never below 20 Hz
            }

            if (!IsFinite(LodBias)) LodBias = 0f;
            if (LodBias < 0f) LodBias = 0f;
            if (LodBias > 2f) LodBias = 2f;

            if (ShadowRefreshRate < 0) ShadowRefreshRate = 0;
            if (ShadowRefreshRate > 16) ShadowRefreshRate = 16;

            if (!IsFinite(PathfindBudgetMs)) PathfindBudgetMs = 0f;
            if (PathfindBudgetMs != 0f)
            {
                // The game field is [Range(0.01, 16.666)]; that's editor-only, so clamp reflective writes
                // ourselves — a huge value would let pathfinding burn the whole frame.
                if (PathfindBudgetMs < 0.01f) PathfindBudgetMs = 0.01f;
                if (PathfindBudgetMs > 16.666666f) PathfindBudgetMs = 16.666666f;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
