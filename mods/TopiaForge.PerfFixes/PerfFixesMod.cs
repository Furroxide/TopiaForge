using System;
using System.Collections.Generic;
using HarmonyLib;
using TopiaForge.Mods;
using TopiaForge.PerfFixes.Appliers;

namespace TopiaForge.PerfFixes
{
    /// <summary>
    /// Entry point for the behavior-identical performance-fix mod. Applies only fixes that leave the game's
    /// visuals and gameplay unchanged and just make existing work cheaper (Camera.main caching, collision
    /// GC removal). Each fix is captured/reverted; Harmony patches are removed on unload.
    /// </summary>
    public sealed class PerfFixesMod : TopiaForgeMod
    {
        private const string HarmonyId = "io.github.furroxide.topiaforge.perffixes.harmony";
        private static readonly ConfigDefinition<PerfFixesConfig> Config =
            new ConfigDefinition<PerfFixesConfig>(1, () => new PerfFixesConfig());

        private Harmony? harmony;
        private readonly List<IPerfApplier> appliers = new List<IPerfApplier>();

        /// <inheritdoc/>
        protected override void OnLoad()
        {
            var logger = Context.Logger;

            var configResult = Context.Config.Load(Config);
            if (!configResult.TryGetValue(out var config))
            {
                logger.Error($"PerfFixes configuration could not be loaded ({configResult.ErrorCode}): {configResult.ErrorMessage}");
                return;
            }

            if (!config.Enabled)
            {
                logger.Info("TopiaForge Performance Fixes loaded but disabled (enabled = false).");
                return;
            }

            harmony = new Harmony(HarmonyId);

            appliers.Add(new ReuseCollisionCallbacksApplier(config, logger));
            appliers.Add(new CameraMainCacheApplier(config, logger, harmony));
            appliers.Add(new CollisionProxyApplier(config, logger, harmony));

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
            logger.Info("TopiaForge Performance Fixes loaded (behavior-identical optimizations active).");
        }

        /// <inheritdoc/>
        protected override void OnUnload()
        {
            for (var i = appliers.Count - 1; i >= 0; i--)
            {
                var applier = appliers[i];
                Guard(applier.Revert, applier, "Revert");
            }

            try
            {
                harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                Context.Logger.Error(ex, "PerfFixes: failed to unpatch Harmony.");
            }

            appliers.Clear();
            harmony = null;
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
                Context.Logger.Error(ex, $"PerfFixes: applier '{applier.Name}' failed during {phase}.");
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
                Context.Logger.Error(ex, $"PerfFixes: applier '{applier.Name}' failed during Update.");
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
                Context.Logger.Error(ex, $"PerfFixes: applier '{applier.Name}' failed during SceneLoaded.");
                return false;
            }
        }

        private void DisableApplier(int index, IPerfApplier applier, string failedPhase)
        {
            Guard(applier.Revert, applier, "failure cleanup");
            appliers.RemoveAt(index);
            Context.Logger.Warn($"PerfFixes: disabled applier '{applier.Name}' after {failedPhase} failure.");
        }
    }
}
