using System;
using System.Collections.Generic;
using HarmonyLib;
using Robotopia.Mods;
using Robotopia.Performance.Appliers;

namespace Robotopia.Performance
{
    /// <summary>
    /// Entry point for the Robotopia performance mod. Resolves the configured preset, builds the
    /// applier set, applies each lever (capturing originals), and re-asserts on Update/SceneLoaded.
    /// Everything is reverted on unload (in reverse order), then Harmony patches are removed.
    /// </summary>
    public sealed class PerformanceMod : IRobotopiaMod
    {
        private const string HarmonyId = "robotopia.performance.harmony";

        private IModContext? context;
        private Harmony? harmony;
        private readonly List<IPerfApplier> appliers = new List<IPerfApplier>();

        public void OnLoad(IModContext context)
        {
            this.context = context;
            var logger = context.Logger;

            var config = context.LoadConfig(new PerformanceConfig());
            config.Normalize();                 // sanitize the mode string + clamp raw user values first
            PerformancePreset.Apply(config);    // resolve the preset onto per-lever fields (unless override_manual)
            config.Normalize();                 // clamp anything the preset set
            // Intentionally NOT re-persisting: SaveConfig here would write the preset-resolved object back
            // over the user's file, wiping any per-lever edit they made without override_manual. LoadConfig
            // already seeds the on-disk defaults on first run, so the file is created without this call.

            if (config.PerformanceMode == "off")
            {
                logger.Info("Robotopia Performance loaded but disabled (performance_mode = off).");
                // Still subscribe nothing and keep no appliers; remains a clean no-op.
                return;
            }

            harmony = new Harmony(HarmonyId);

            appliers.Add(new EngineApplier(config, logger));
            appliers.Add(new VolumeApplier(config, logger));
            appliers.Add(new AssetApplier(config, logger));
            appliers.Add(new CameraDynResApplier(config, logger));
            appliers.Add(new PatchApplier(config, logger, harmony));
            appliers.Add(new NavTuningApplier(config, logger));

            foreach (var applier in appliers)
            {
                Guard(() => applier.Apply(), applier, "Apply");
            }

            context.Update += OnUpdate;
            context.SceneLoaded += OnSceneLoaded;
            logger.Info($"Robotopia Performance loaded (mode: {config.PerformanceMode}).");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.Update -= OnUpdate;
                context.SceneLoaded -= OnSceneLoaded;
            }

            // Revert in reverse application order so quality/asset state unwinds cleanly.
            for (var i = appliers.Count - 1; i >= 0; i--)
            {
                var applier = appliers[i];
                Guard(() => applier.Revert(), applier, "Revert");
            }

            try
            {
                harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                context?.Logger.Error(ex, "Performance: failed to unpatch Harmony.");
            }

            appliers.Clear();
            harmony = null;
            context = null;
        }

        private void OnUpdate(float deltaTime)
        {
            for (var i = 0; i < appliers.Count; i++)
            {
                var applier = appliers[i];
                Guard(() => applier.OnUpdate(deltaTime), applier, "Update");
            }
        }

        private void OnSceneLoaded(string sceneName)
        {
            for (var i = 0; i < appliers.Count; i++)
            {
                var applier = appliers[i];
                Guard(() => applier.OnSceneLoaded(sceneName), applier, "SceneLoaded");
            }
        }

        private void Guard(Action action, IPerfApplier applier, string phase)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                context?.Logger.Error(ex, $"Performance: applier '{applier.Name}' failed during {phase}.");
            }
        }
    }
}
