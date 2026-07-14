using System;
using TopiaForge.GravityGun;
using TopiaForge.ModManager.Core;
using TopiaForge.NoFeedbackUrl;
using TopiaForge.Performance;
using TopiaForge.PerfFixes;

namespace TopiaForge.ModManager.Tests
{
    internal static class FirstPartyConfigTests
    {
        public static void Run()
        {
            GravityGunDefaultsAndNormalization();
            NoFeedbackUrlDefaults();
            PerfFixesDefaults();
            PerformanceDefaultsAndPresets();
            Console.WriteLine("FirstPartyConfigTests passed.");
        }

        private static void GravityGunDefaultsAndNormalization()
        {
            var defaults = new GravityGunConfig();
            Assert(defaults.MaxRange == 20f && defaults.DefaultHoldDistance == 5f
                   && defaults.RequireCursorLocked && defaults.ParticleIntensity == 1f,
                "GravityGun constructor defaults changed unexpectedly");

            var partial = JsonUtil.Deserialize<GravityGunConfig>("{\"maxRange\":42}");
            Assert(partial.MaxRange == 42f && partial.DefaultHoldDistance == 5f
                   && partial.RequireCursorLocked,
                "GravityGun partial config must seed absent fields before deserialization");

            partial.MinHoldDistance = 80f;
            partial.MaxHoldDistance = float.NaN;
            partial.DefaultHoldDistance = float.PositiveInfinity;
            partial.PullStrength = -1f;
            partial.ParticleIntensity = 99f;
            partial.Normalize();
            Assert(partial.MinHoldDistance == 50f && partial.MaxHoldDistance >= partial.MinHoldDistance
                   && partial.DefaultHoldDistance >= partial.MinHoldDistance
                   && partial.PullStrength == 1f && partial.ParticleIntensity == 5f,
                "GravityGun normalization must produce finite internally consistent bounds");
        }

        private static void NoFeedbackUrlDefaults()
        {
            Assert(!new NoFeedbackUrlConfig().HasSeenFirstLaunch,
                "NoFeedbackUrl must allow exactly the first launch by default");
            Assert(JsonUtil.Deserialize<NoFeedbackUrlConfig>("{\"hasSeenFirstLaunch\":true}").HasSeenFirstLaunch,
                "NoFeedbackUrl persisted first-launch state must round-trip");
        }

        private static void PerfFixesDefaults()
        {
            var defaults = new PerfFixesConfig();
            Assert(defaults.Enabled && defaults.ReuseCollisionCallbacks
                   && defaults.CameraMainCache && defaults.CollisionProxyPooled,
                "PerfFixes safe optimizations must default on");

            var partial = JsonUtil.Deserialize<PerfFixesConfig>("{\"enabled\":false}");
            Assert(!partial.Enabled && partial.ReuseCollisionCallbacks
                   && partial.CameraMainCache && partial.CollisionProxyPooled,
                "PerfFixes partial config must retain safe defaults for absent fields");
        }

        private static void PerformanceDefaultsAndPresets()
        {
            var defaults = new PerformanceConfig();
            Assert(defaults.PerformanceMode == "balanced" && defaults.DynamicResolutionPercent == 70
                   && defaults.TargetFrameRate == -1 && defaults.RenderFrameInterval == 1,
                "Performance constructor defaults changed unexpectedly");

            var partial = JsonUtil.Deserialize<PerformanceConfig>("{\"performance_mode\":\"potato\"}");
            Assert(partial.PerformanceMode == "potato" && partial.DynamicResolutionPercent == 70
                   && partial.TargetFrameRate == -1 && partial.RenderFrameInterval == 1,
                "Performance partial config must seed absent fields before deserialization");

            partial.LodBias = float.NaN;
            partial.FixedDeltaTime = float.PositiveInfinity;
            partial.PathfindBudgetMs = float.NegativeInfinity;
            partial.GpuSmallMeshScreenPercentage = float.NaN;
            partial.DynamicResolutionPercent = 1000;
            partial.Normalize();
            Assert(partial.LodBias == 0f && partial.FixedDeltaTime == 0f
                   && partial.PathfindBudgetMs == 0f && partial.GpuSmallMeshScreenPercentage == 0f
                   && partial.DynamicResolutionPercent == 100,
                "Performance normalization must reject non-finite and out-of-range values");

            partial.TargetFrameRate = 144;
            partial.DisableSentry = true;
            PerformancePreset.Apply(partial);
            Assert(partial.ForceQualityLevel1 && partial.DynamicResolutionEnabled
                   && partial.DynamicResolutionPercent == 55 && partial.GlobalMipLimit == 1,
                "potato preset must apply its documented aggressive levers");
            Assert(partial.TargetFrameRate == 144 && partial.DisableSentry,
                "presets must not rewrite risky manual-only settings");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("First-party config: " + message);
            }
        }
    }
}
