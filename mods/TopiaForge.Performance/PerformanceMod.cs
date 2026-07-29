using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;
using TopiaForge.Performance.Appliers;

namespace TopiaForge.Performance
{
    /// <summary>
    /// Entry point for the TopiaForge performance mod. Resolves the configured preset, builds the
    /// applier set, applies each lever (capturing originals), and re-asserts on Update/SceneLoaded.
    /// Everything is reverted on unload (in reverse order), then Harmony patches are removed.
    /// </summary>
    public sealed class PerformanceMod : TopiaForgeMod
    {
        // PerformanceConfig is ISelfNormalizingConfig: the config service bounds a stored document before it
        // reaches any applier. Presets are resolved separately in OnLoad and re-normalized there.
        private static readonly ConfigDefinition<PerformanceConfig> Config =
            new ConfigDefinition<PerformanceConfig>(1, () => new PerformanceConfig());

        private IHarmonyLease? harmonyLease;
        private readonly List<IPerfApplier> appliers = new List<IPerfApplier>();

        /// <inheritdoc/>
        protected override void OnLoad()
        {
            var logger = Context.Logger;

            var configResult = Context.Config.Load(Config);
            if (!configResult.TryGetValue(out var config))
            {
                logger.Error($"Performance configuration could not be loaded ({configResult.ErrorCode}): {configResult.ErrorMessage}");
                return;
            }

            // The config contract's validator already sanitized the mode string and clamped raw user values.
            PerformancePreset.Apply(config);    // resolve the preset onto per-lever fields (unless override_manual)
            config.Normalize();                 // clamp anything the preset set
            // Intentionally NOT re-persisting: SaveConfig here would write the preset-resolved object back
            // over the user's file, wiping any per-lever edit they made without override_manual. LoadConfig
            // already seeds the on-disk defaults on first run, so the file is created without this call.

            if (config.PerformanceMode == "off")
            {
                logger.Info("TopiaForge Performance loaded but disabled (performance_mode = off).");
                // Still subscribe nothing and keep no appliers; remains a clean no-op.
                return;
            }

            harmonyLease = Context.CreateHarmonyLease("performance");

            appliers.Add(new EngineApplier(config, logger));
            appliers.Add(new VolumeApplier(config, logger));
            appliers.Add(new AssetApplier(config, logger));
            appliers.Add(new CameraDynResApplier(config, logger));
            appliers.Add(new PatchApplier(config, logger, harmonyLease));
            appliers.Add(new NavTuningApplier(config, logger));

            for (var i = 0; i < appliers.Count;)
            {
                var applier = appliers[i];
                if (Guard(applier.Apply, applier, "Apply"))
                {
                    i++;
                    continue;
                }

                DisableApplier(i, applier, "Apply");
            }

            Context.Events.SubscribeUpdate(OnUpdate);
            Context.Events.SubscribeSceneLoaded(OnSceneLoaded);
            logger.Info($"TopiaForge Performance loaded (mode: {config.PerformanceMode}).");
        }

        /// <inheritdoc/>
        protected override void OnUnload()
        {
            // Revert in reverse application order so quality/asset state unwinds cleanly.
            for (var i = appliers.Count - 1; i >= 0; i--)
            {
                var applier = appliers[i];
                Guard(applier.Revert, applier, "Revert");
            }

            try
            {
                harmonyLease?.Dispose();
            }
            finally
            {
                appliers.Clear();
                harmonyLease = null;
            }
        }

        private void OnUpdate(float deltaTime)
        {
            for (var i = appliers.Count - 1; i >= 0; i--)
            {
                var applier = appliers[i];
                if (!GuardUpdate(applier, deltaTime))
                {
                    DisableApplier(i, applier, "Update");
                }
            }
        }

        private void OnSceneLoaded(string sceneName)
        {
            for (var i = appliers.Count - 1; i >= 0; i--)
            {
                var applier = appliers[i];
                if (!GuardSceneLoaded(applier, sceneName))
                {
                    DisableApplier(i, applier, "SceneLoaded");
                }
            }
        }

        private bool Guard(Action action, IPerfApplier applier, string phase)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                Context.Logger.Error(ex, $"Performance: applier '{applier.Name}' failed during {phase}.");
                return false;
            }
        }

        private bool GuardUpdate(IPerfApplier applier, float deltaTime)
        {
            try
            {
                applier.OnUpdate(deltaTime);
                return true;
            }
            catch (Exception ex)
            {
                Context.Logger.Error(ex, $"Performance: applier '{applier.Name}' failed during Update.");
                return false;
            }
        }

        private bool GuardSceneLoaded(IPerfApplier applier, string sceneName)
        {
            try
            {
                applier.OnSceneLoaded(sceneName);
                return true;
            }
            catch (Exception ex)
            {
                Context.Logger.Error(ex, $"Performance: applier '{applier.Name}' failed during SceneLoaded.");
                return false;
            }
        }

        private void DisableApplier(int index, IPerfApplier applier, string failedPhase)
        {
            Guard(applier.Revert, applier, "failure cleanup");
            appliers.RemoveAt(index);
            Context.Logger.Warn($"Performance: disabled applier '{applier.Name}' after {failedPhase} failure.");
        }
    }
}
