using System;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.Rendering;

namespace TopiaForge.Performance.Appliers
{
    /// <summary>
    /// Plain-Unity-API levers: frame pacing (vSync / targetFrameRate / on-demand render interval),
    /// cheap <c>QualitySettings</c> knobs, physics flags, log stack-trace stripping, and the game's
    /// reflection-probe pool (via reflection on its singleton). "Sticky" values are re-asserted per
    /// frame (cheap) because the game's <c>SetQualityLevel</c> reloads them; scene-clobbered knobs are
    /// re-asserted on scene load. Every touched value is captured for a clean revert.
    /// </summary>
    internal sealed class EngineApplier : PerfApplierBase
    {
        private readonly PerformanceConfig config;
        private readonly IModLogger logger;

        // Captured originals.
        private int origVSync;
        private int origTargetFrameRate;
        private int origRenderFrameInterval;
        private float origLodBias;
        private int origMipLimit;
        private AnisotropicFiltering origAniso;
        private int origParticleBudget;
        private bool origReuseCollisionCallbacks;
        private int origSolverIterations;
        private float origFixedDeltaTime;
        private float origMaximumDeltaTime;
        private StackTraceLogType origStackTraceLog;
        private StackTraceLogType origStackTraceWarning;
        private int? origProbePool;
        private bool captured;

        public EngineApplier(PerformanceConfig config, IModLogger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public override string Name => "Engine";

        public override void Apply()
        {
            Capture();

            // One-shot global settings.
            if (config.ReuseCollisionCallbacks)
            {
                TrySet(() => Physics.reuseCollisionCallbacks = true);
            }

            if (config.StripLogStackTraces)
            {
                TrySet(() =>
                {
                    Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
                    Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
                });
            }

            if (config.SolverIterations > 0)
            {
                TrySet(() => Physics.defaultSolverIterations = config.SolverIterations);
            }

            if (config.FixedDeltaTime > 0f)
            {
                TrySet(() =>
                {
                    Time.fixedDeltaTime = config.FixedDeltaTime;
                    // Keep maximumDeltaTime >= fixedDeltaTime so the engine doesn't spiral on stalls.
                    if (Time.maximumDeltaTime < config.FixedDeltaTime)
                    {
                        Time.maximumDeltaTime = config.FixedDeltaTime * 3f;
                    }
                });
            }

            ApplySticky();
            ApplySceneKnobs();
        }

        public override void OnSceneLoaded(string sceneName)
        {
            ApplySticky();
            ApplySceneKnobs();
        }

        public override void OnUpdate(float deltaTime)
        {
            // Re-assert only the cheap sticky frame-pacing values; the game's SetQualityLevel (which can
            // fire from the in-game menu without a scene load) reloads vSync from the target asset.
            ApplySticky();
        }

        public override void Revert()
        {
            if (!captured)
            {
                return;
            }

            TrySet(() => QualitySettings.vSyncCount = origVSync);
            TrySet(() => Application.targetFrameRate = origTargetFrameRate);
            TrySet(() => OnDemandRendering.renderFrameInterval = origRenderFrameInterval);
            TrySet(() => QualitySettings.lodBias = origLodBias);
            TrySet(() => QualitySettings.globalTextureMipmapLimit = origMipLimit);
            TrySet(() => QualitySettings.anisotropicFiltering = origAniso);
            TrySet(() => QualitySettings.particleRaycastBudget = origParticleBudget);
            TrySet(() => Physics.reuseCollisionCallbacks = origReuseCollisionCallbacks);
            TrySet(() => Physics.defaultSolverIterations = origSolverIterations);

            if (config.FixedDeltaTime > 0f)
            {
                TrySet(() =>
                {
                    Time.fixedDeltaTime = origFixedDeltaTime;
                    Time.maximumDeltaTime = origMaximumDeltaTime;
                });
            }

            if (config.StripLogStackTraces)
            {
                TrySet(() =>
                {
                    Application.SetStackTraceLogType(LogType.Log, origStackTraceLog);
                    Application.SetStackTraceLogType(LogType.Warning, origStackTraceWarning);
                });
            }

            if (origProbePool.HasValue)
            {
                SetProbePool(origProbePool.Value);
            }
        }

        private void Capture()
        {
            if (captured)
            {
                return;
            }

            try
            {
                origVSync = QualitySettings.vSyncCount;
                origTargetFrameRate = Application.targetFrameRate;
                origRenderFrameInterval = OnDemandRendering.renderFrameInterval;
                origLodBias = QualitySettings.lodBias;
                origMipLimit = QualitySettings.globalTextureMipmapLimit;
                origAniso = QualitySettings.anisotropicFiltering;
                origParticleBudget = QualitySettings.particleRaycastBudget;
                origReuseCollisionCallbacks = Physics.reuseCollisionCallbacks;
                origSolverIterations = Physics.defaultSolverIterations;
                origFixedDeltaTime = Time.fixedDeltaTime;
                origMaximumDeltaTime = Time.maximumDeltaTime;
                origStackTraceLog = Application.GetStackTraceLogType(LogType.Log);
                origStackTraceWarning = Application.GetStackTraceLogType(LogType.Warning);
            }
            catch (Exception ex)
            {
                logger.Warn("Performance: failed to capture original engine settings: " + ex.Message);
            }

            captured = true;

            // Snapshot the reflection-probe pool now, BEFORE PatchApplier forces the low quality level
            // (this applier runs first). If we deferred to a later scene load, the game's
            // ReflectionProbeManager.Start() would have zeroed the field under forced-low quality and we
            // would capture a poisoned 0 as the "original".
            if (config.ReflectionProbePool >= 0)
            {
                CaptureProbePoolBaseline();
            }
        }

        private void CaptureProbePoolBaseline()
        {
            try
            {
                var type = GameReflectionLite.GameType("ReflectionProbeManager");
                if (type == null)
                {
                    return;
                }

                var manager = GameReflectionLite.FindFirst(type);
                if (manager != null && !origProbePool.HasValue &&
                    GameReflectionLite.TryGetField(manager, "maxAutoProbePoolSize", out var current) &&
                    current is int currentInt)
                {
                    origProbePool = currentInt;
                }
            }
            catch
            {
                // Non-fatal.
            }
        }

        private void ApplySticky()
        {
            if (config.VSyncCount >= 0)
            {
                TrySet(() => QualitySettings.vSyncCount = config.VSyncCount);
            }

            if (config.TargetFrameRate > 0)
            {
                TrySet(() => Application.targetFrameRate = config.TargetFrameRate);
            }

            if (config.RenderFrameInterval > 1)
            {
                TrySet(() => OnDemandRendering.renderFrameInterval = config.RenderFrameInterval);
            }
        }

        private void ApplySceneKnobs()
        {
            if (config.LodBias > 0f)
            {
                TrySet(() => QualitySettings.lodBias = config.LodBias);
            }

            if (config.GlobalMipLimit > 0)
            {
                TrySet(() => QualitySettings.globalTextureMipmapLimit = config.GlobalMipLimit);
            }

            if (config.AnisotropicDisable)
            {
                TrySet(() => QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable);
            }

            if (config.ParticleRaycastBudget > 0)
            {
                TrySet(() => QualitySettings.particleRaycastBudget = config.ParticleRaycastBudget);
            }

            if (config.ReflectionProbePool >= 0)
            {
                SetProbePool(config.ReflectionProbePool);
            }
        }

        private void SetProbePool(int size)
        {
            try
            {
                var type = GameReflectionLite.GameType("ReflectionProbeManager");
                if (type == null)
                {
                    return;
                }

                var manager = GameReflectionLite.FindFirst(type);
                if (manager == null)
                {
                    return;
                }

                if (!origProbePool.HasValue &&
                    GameReflectionLite.TryGetField(manager, "maxAutoProbePoolSize", out var current) &&
                    current is int currentInt)
                {
                    // If we are forcing low quality, a read-back of 0 is almost certainly the value the
                    // game's own Start() wrote because of that forcing, not the true baseline. Fall back
                    // to the engine default (3) so revert restores working reflection probes.
                    origProbePool = (config.ForceQualityLevel1 && currentInt == 0) ? 3 : currentInt;
                }

                GameReflectionLite.CallInstanceVoid(manager, "SetMaxAutoProbePoolSize", size);
            }
            catch
            {
                // Non-fatal.
            }
        }

        private void TrySet(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                logger.Debug("Performance: engine set failed: " + ex.Message);
            }
        }
    }
}
