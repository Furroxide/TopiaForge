using System;
using System.Collections.Generic;
using Robotopia.Mods;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Robotopia.Performance.Appliers
{
    /// <summary>
    /// Mutates the active <see cref="HDRenderPipelineAsset"/>'s <c>RenderPipelineSettings</c> struct
    /// (public get/set; the setter runs <c>OnValidate</c>). Dynamic resolution is read live by HDRP
    /// each frame once enabled — but it ALSO requires the camera flag set by
    /// <see cref="CameraDynResApplier"/>. The shadow-atlas, volumetrics/SSGI and GPU-occlusion-culling
    /// (GPU Resident Drawer) levers only take effect after the pipeline is recreated (the
    /// <c>OnValidate</c> on assignment does that) and cause a one-time hitch, so they are gated behind
    /// <c>asset_rebuild_allowed</c>. The GPU Resident Drawer is self-gating in HDRP for the PLATFORM
    /// case (unsupported GPU/pipeline => the drawer isn't created and the normal render path is untouched),
    /// but NOT for the build case: if the build stripped the "BatchRendererGroup Variants" shader variants
    /// (undetectable at runtime, and SILENT at render time), the drawer constructs but meshes render with
    /// the error shader. So GRD requires an explicit <c>gpu_occlusion_allow_unverified</c> confirmation to
    /// engage at all — that gate is the real safety mechanism; the log watchdog (<see cref="StartGrdWatchdog"/>)
    /// is a best-effort secondary that cannot see the silent case.
    /// </summary>
    internal sealed class AssetApplier : PerfApplierBase
    {
        private readonly PerformanceConfig config;
        private readonly IModLogger logger;
        private readonly Dictionary<HDRenderPipelineAsset, RenderPipelineSettings> originals =
            new Dictionary<HDRenderPipelineAsset, RenderPipelineSettings>();

        // GRD engagement is verified a few frames after Apply (the pipeline recreates lazily on the next
        // render), then this disarms. Diagnostic only.
        private bool grdVerifyPending;
        private int grdVerifyFrames;
        private bool grdApplied;

        // Best-effort secondary watchdog (the allow_unverified gate is the real guarantee). For a bounded
        // window after enabling GRD, watch Unity's log for instancing/BRG errors and auto-revert JUST the
        // GRD settings (other asset levers intact) if any appear. NOTE: the dominant failure — the build
        // stripped the DOTS-instancing shader variants — is SILENT (no runtime log), so this watchdog
        // cannot catch it; it only covers cases Unity actually logs an error for.
        private bool grdWatching;
        private bool grdBreakageDetected;
        private string? grdBreakageSample;
        private int grdWatchFrames;
        private const int GrdStableFrames = 180; // ~3s @ 60fps watch window, then stop (avoid false positives)

        public AssetApplier(PerformanceConfig config, IModLogger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public override string Name => "Asset";

        // GRD is *requested* if occlusion or small-mesh culling is asked for; both need InstancedDrawing.
        private bool GrdRequested => config.GpuOcclusionCulling || config.GpuSmallMeshScreenPercentage > 0f;

        // GRD only *engages* when the user has ALSO confirmed a "Keep All" build. There is no runtime way
        // to verify the required DOTS-instancing shader variants are present, and a build without them
        // renders meshes pink — silently. So this confirmation gate is the real safety mechanism; the
        // log watchdog below is only a best-effort secondary signal (it cannot see the silent case).
        private bool GrdEffective => GrdRequested && config.GpuOcclusionAllowUnverified;

        private bool RebuildLeversActive =>
            config.AssetRebuildAllowed &&
            (config.ShadowAtlasResolution > 0 || config.DisableVolumetricsSsgiAsset || GrdEffective);

        private bool AnyEnabled => config.DynamicResolutionEnabled || RebuildLeversActive;

        public override void Apply()
        {
            // GRD forces a pipeline recreate, so it shares the asset_rebuild_allowed gate.
            if (GrdRequested && !config.AssetRebuildAllowed)
            {
                logger.Warn("Performance: gpu_occlusion_culling / gpu_small_mesh_screen_percentage require " +
                            "asset_rebuild_allowed = true (they force a one-time pipeline recreate). Skipping.");
            }
            else if (GrdRequested && !config.GpuOcclusionAllowUnverified)
            {
                logger.Warn("Performance: gpu_occlusion_culling requested but NOT enabled (safe default). The " +
                            "GPU Resident Drawer needs the build to keep \"BatchRendererGroup Variants\" shader " +
                            "variants; this build ships without Entities Graphics, so they were almost certainly " +
                            "stripped — enabling would render geometry pink/invisible, and that failure is silent " +
                            "with no reliable runtime detection. To enable on a build compiled with Graphics > " +
                            "Shader Stripping > \"BatchRendererGroup Variants\" = \"Keep All\", also set " +
                            "gpu_occlusion_allow_unverified = true.");
            }

            if (!AnyEnabled)
            {
                return;
            }

            ApplyToAssets(includeRebuild: true);
        }

        public override void OnSceneLoaded(string sceneName)
        {
            // Only re-assert the live (no-rebuild) dynamic-resolution value if it drifted, to avoid
            // repeatedly triggering OnValidate (a pipeline recreate hitch). Rebuild levers persist.
            if (!config.DynamicResolutionEnabled)
            {
                return;
            }

            foreach (var asset in CollectAssets())
            {
                try
                {
                    if (!asset.currentPlatformRenderPipelineSettings.dynamicResolutionSettings.enabled)
                    {
                        ApplyToAssets(includeRebuild: false);
                        return;
                    }
                }
                catch
                {
                    // Skip a bad asset.
                }
            }
        }

        public override void Revert()
        {
            // Restoring the captured RenderPipelineSettings struct rolls back the GPU Resident Drawer
            // too (mode/occlusion/small-mesh are fields of that struct); the recreate disposes the drawer.
            StopGrdWatchdog();           // unsubscribe the log handler so we don't leak it
            grdVerifyPending = false;
            grdVerifyFrames = 0;

            foreach (var pair in originals)
            {
                try
                {
                    if (pair.Key != null)
                    {
                        pair.Key.currentPlatformRenderPipelineSettings = pair.Value;
                    }
                }
                catch
                {
                    // Best-effort restore.
                }
            }

            originals.Clear();

            // The restored struct sets the drawer back to Disabled; force the same reinit path so the
            // drawer is actually disposed now rather than waiting on a lazy pipeline recreate.
            if (grdApplied)
            {
                ForceGrdReinitialize();
                grdApplied = false;
            }
        }

        private void ApplyToAssets(bool includeRebuild)
        {
            var pct = Mathf.Clamp(config.DynamicResolutionPercent, 50, 100);
            var touchedRebuild = false;

            foreach (var asset in CollectAssets())
            {
                try
                {
                    if (!originals.ContainsKey(asset))
                    {
                        originals[asset] = asset.currentPlatformRenderPipelineSettings;
                    }

                    var s = asset.currentPlatformRenderPipelineSettings;
                    var changed = false;

                    if (config.DynamicResolutionEnabled)
                    {
                        s.dynamicResolutionSettings.enabled = true;
                        s.dynamicResolutionSettings.forceResolution = true;
                        s.dynamicResolutionSettings.forcedPercentage = pct;
                        s.dynamicResolutionSettings.minPercentage = pct;
                        s.dynamicResolutionSettings.maxPercentage = 100f;
                        s.dynamicResolutionSettings.dynResType = DynamicResolutionType.Hardware;
                        changed = true;
                    }

                    if (includeRebuild && config.AssetRebuildAllowed)
                    {
                        if (config.ShadowAtlasResolution > 0)
                        {
                            s.hdShadowInitParams.punctualLightShadowAtlas.shadowAtlasResolution = config.ShadowAtlasResolution;
                            changed = true;
                            touchedRebuild = true;
                        }

                        if (config.DisableVolumetricsSsgiAsset)
                        {
                            s.supportVolumetrics = false;
                            s.supportSSGI = false;
                            changed = true;
                            touchedRebuild = true;
                        }

                        if (GrdEffective)
                        {
                            // Turn on the GPU Resident Drawer (required for any GPU-driven culling) and,
                            // within it, the per-camera GPU occlusion culling and/or small-mesh culling.
                            // HDRP wires the occlusion passes into its render graph automatically once the
                            // drawer is enabled — no per-camera flag is needed (unlike dynamic resolution).
                            s.gpuResidentDrawerSettings.mode = GPUResidentDrawerMode.InstancedDrawing;
                            s.gpuResidentDrawerSettings.enableOcclusionCullingInCameras = config.GpuOcclusionCulling;
                            if (config.GpuSmallMeshScreenPercentage > 0f)
                            {
                                s.gpuResidentDrawerSettings.smallMeshScreenPercentage = config.GpuSmallMeshScreenPercentage;
                            }
                            // Leave useDepthPrepassForOccluders at the HDRP default (true).
                            changed = true;
                            touchedRebuild = true;
                            grdApplied = true;
                        }
                    }

                    if (changed)
                    {
                        asset.currentPlatformRenderPipelineSettings = s; // setter runs OnValidate
                    }
                }
                catch (Exception ex)
                {
                    logger.Warn("Performance: failed to tune an HDRP asset: " + ex.Message);
                }
            }

            if (config.DynamicResolutionEnabled)
            {
                logger.Info($"Performance: dynamic resolution enabled at {pct}% (needs camera flag; verify live).");
            }

            if (touchedRebuild)
            {
                logger.Info("Performance: HDRP asset rebuild levers applied (one-time pipeline recreate).");
            }

            if (includeRebuild && config.AssetRebuildAllowed && GrdEffective)
            {
                // Force the drawer to re-read the asset settings now. The struct setter's OnValidate
                // recreates the pipeline (which also reinitialises the drawer), but the recreate is lazy
                // at runtime, so we trigger the same path HDRP uses (CreatePipeline calls this) to make
                // engagement deterministic instead of relying on recreate timing.
                ForceGrdReinitialize();

                logger.Info("Performance: GPU Resident Drawer enabled (allow_unverified)" +
                            (config.GpuOcclusionCulling ? " with GPU occlusion culling" : "") +
                            (config.GpuSmallMeshScreenPercentage > 0f
                                ? $" + small-mesh culling @ {config.GpuSmallMeshScreenPercentage}%"
                                : "") + ". If you see pink/missing geometry, set gpu_occlusion_culling = false.");

                grdVerifyPending = true;   // verify engagement (occlusion- or drawer-level, see OnUpdate)
                grdVerifyFrames = 0;
                StartGrdWatchdog();        // best-effort: catches logged instancing errors (not the silent case)
            }
        }

        // Re-reads the GPU Resident Drawer settings from the active render-pipeline asset and rebuilds
        // (or disposes) the drawer. Safe to call any time: GetGlobalSettingsFromRPAsset returns a
        // Disabled default when the pipeline isn't GRD-capable, and Recreate self-gates on support.
        private void ForceGrdReinitialize()
        {
            try
            {
                IGPUResidentRenderPipeline.ReinitializeGPUResidentDrawer();
            }
            catch (Exception ex)
            {
                logger.Warn("Performance: GPU Resident Drawer reinitialise failed: " + ex.Message);
            }
        }

        public override void OnUpdate(float deltaTime)
        {
            // (1) Safety watchdog takes priority: if the drawer caused instancing/shader errors, revert it.
            if (grdWatching)
            {
                if (grdBreakageDetected)
                {
                    HandleGrdBreakage();
                    return; // GRD reverted; verify below is moot
                }

                if (++grdWatchFrames >= GrdStableFrames)
                {
                    // Bounded window: stop watching so an unrelated later error can't trip a spurious revert.
                    // IMPORTANT: "no logged errors" does NOT prove the drawer renders correctly — a
                    // stripped-variant failure is silent (Unity logs nothing). That is what the
                    // allow_unverified gate and the docs are for, not this watchdog.
                    StopGrdWatchdog();
                    logger.Info("Performance: GPU Resident Drawer watchdog window elapsed with no logged " +
                                "instancing errors (this does not prove correctness — the failure mode is " +
                                "silent; if you see pink/missing geometry, set gpu_occlusion_culling = false).");
                }
            }

            // (2) Engagement verify (diagnostic only — confirms the drawer turned on; the allow_unverified
            // gate is what protects against breakage, not this check).
            if (!grdVerifyPending)
            {
                return;
            }

            // OnValidate marks the pipeline dirty; it recreates (and (re)builds the drawer) on the next
            // render, so give it a few frames before reading the live state.
            if (grdVerifyFrames++ < 3)
            {
                return;
            }

            grdVerifyPending = false;
            try
            {
                // For the occlusion lever, check the occlusion-specific flag; for small-mesh-only, the
                // occlusion flag is off by design, so fall back to "is the drawer enabled at all".
                var engaged = config.GpuOcclusionCulling
                    ? GPUResidentDrawer.IsInstanceOcclusionCullingEnabled()
                    : IGPUResidentRenderPipeline.IsGPUResidentDrawerEnabled();

                if (engaged)
                {
                    logger.Info("Performance: GPU Resident Drawer engaged" +
                                (config.GpuOcclusionCulling ? " (GPU occlusion culling active)" : "") +
                                ". Still monitoring for shader errors.");
                }
                else
                {
                    logger.Warn("Performance: GPU Resident Drawer did NOT engage — this platform/pipeline " +
                                "does not support it, so rendering is unchanged (no breakage). " +
                                "See the HDRP message logged at apply time for the reason.");
                    StopGrdWatchdog(); // nothing to watch if it never engaged
                }
            }
            catch (Exception ex)
            {
                logger.Warn("Performance: GPU Resident Drawer verification failed: " + ex.Message);
            }
        }

        // --- GPU Resident Drawer safety watchdog -----------------------------------------------------

        private void StartGrdWatchdog()
        {
            if (grdWatching)
            {
                return;
            }

            grdBreakageDetected = false;
            grdBreakageSample = null;
            grdWatchFrames = 0;
            grdWatching = true;
            Application.logMessageReceived += OnGrdLogMessage;
        }

        private void StopGrdWatchdog()
        {
            if (!grdWatching)
            {
                return;
            }

            grdWatching = false;
            try
            {
                Application.logMessageReceived -= OnGrdLogMessage;
            }
            catch
            {
                // Best-effort unsubscribe.
            }
        }

        // Fires on the main thread for every engine/script log. We only flag errors whose text clearly
        // points at the GPU-driven / DOTS-instancing path, to avoid reverting on unrelated game errors.
        private void OnGrdLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            {
                return;
            }

            if (string.IsNullOrEmpty(condition) || !IsGrdRelated(condition))
            {
                return;
            }

            grdBreakageDetected = true;
            if (grdBreakageSample == null)
            {
                grdBreakageSample = condition.Length > 200 ? condition.Substring(0, 200) : condition;
            }
        }

        // Specific enough to avoid reverting on unrelated game errors. Deliberately drops broad phrases
        // like "has no variant"; the real stripped-variant failure is silent anyway (see watchdog note).
        private static bool IsGrdRelated(string msg)
        {
            return msg.IndexOf("DOTS_INSTANCING", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("DOTS instancing", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("BatchRendererGroup", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("GPUResidentDrawer", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("GPUDriven", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void HandleGrdBreakage()
        {
            StopGrdWatchdog();
            grdVerifyPending = false;
            RevertGrdOnly();
            logger.Error("Performance: GPU occlusion culling produced rendering errors on this build and was " +
                         "AUTO-REVERTED (other levers kept). Cause: the GPU Resident Drawer's DOTS-instancing " +
                         "shader variants are not in this build — it was built with Graphics > Shader Stripping > " +
                         "\"BatchRendererGroup Variants\" not set to \"Keep All\". To use this lever, rebuild the " +
                         "game with that setting. Sample error: " + (grdBreakageSample ?? "(none)"));
        }

        // Restores ONLY the GPU Resident Drawer fields from each captured baseline, leaving any other
        // asset levers (dynamic resolution, shadow atlas, volumetrics) in place, then disposes the drawer.
        private void RevertGrdOnly()
        {
            foreach (var pair in originals)
            {
                try
                {
                    if (pair.Key != null)
                    {
                        var s = pair.Key.currentPlatformRenderPipelineSettings;
                        s.gpuResidentDrawerSettings = pair.Value.gpuResidentDrawerSettings;
                        pair.Key.currentPlatformRenderPipelineSettings = s; // setter runs OnValidate
                    }
                }
                catch
                {
                    // Best-effort.
                }
            }

            ForceGrdReinitialize();
            grdApplied = false;
        }

        private IEnumerable<HDRenderPipelineAsset> CollectAssets()
        {
            var seen = new HashSet<HDRenderPipelineAsset>();

            HDRenderPipelineAsset? Add(RenderPipelineAsset? raw)
            {
                if (raw is HDRenderPipelineAsset hd && seen.Add(hd))
                {
                    return hd;
                }

                return null;
            }

            var list = new List<HDRenderPipelineAsset>();

            try
            {
                var current = Add(QualitySettings.renderPipeline ?? GraphicsSettings.currentRenderPipeline);
                if (current != null)
                {
                    list.Add(current);
                }
            }
            catch
            {
                // Ignore.
            }

            try
            {
                var count = QualitySettings.names?.Length ?? 0;
                for (var i = 0; i < count; i++)
                {
                    var hd = Add(QualitySettings.GetRenderPipelineAssetAt(i));
                    if (hd != null)
                    {
                        list.Add(hd);
                    }
                }
            }
            catch
            {
                // Ignore.
            }

            return list;
        }
    }
}
