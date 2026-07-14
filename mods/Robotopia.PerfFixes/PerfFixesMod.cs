using System;
using System.Collections.Generic;
using HarmonyLib;
using Robotopia.Mods;
using Robotopia.PerfFixes.Appliers;

namespace Robotopia.PerfFixes
{
    /// <summary>
    /// Entry point for the behavior-identical performance-fix mod. Applies only fixes that leave the game's
    /// visuals and gameplay unchanged and just make existing work cheaper (Camera.main caching, collision
    /// GC removal). Each fix is captured/reverted; Harmony patches are removed on unload.
    /// </summary>
    public sealed class PerfFixesMod : IRobotopiaMod
    {
        private const string HarmonyId = "robotopia.perffixes.harmony";

        private IModContext? context;
        private Harmony? harmony;
        private readonly List<IPerfApplier> appliers = new List<IPerfApplier>();

        public void OnLoad(IModContext context)
        {
            this.context = context;
            var logger = context.Logger;

            var config = context.LoadConfig(new PerfFixesConfig());
            // Do not re-persist: LoadConfig already seeds the on-disk defaults on first run, and re-saving
            // would clobber a user's hand edits.

            if (!config.Enabled)
            {
                logger.Info("Robotopia Performance Fixes loaded but disabled (enabled = false).");
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

            context.Update += OnUpdate;
            context.SceneLoaded += OnSceneLoaded;
            logger.Info("Robotopia Performance Fixes loaded (behavior-identical optimizations active).");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.Update -= OnUpdate;
                context.SceneLoaded -= OnSceneLoaded;
            }

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
                context?.Logger.Error(ex, "PerfFixes: failed to unpatch Harmony.");
            }

            appliers.Clear();
            harmony = null;
            context = null;
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
                context?.Logger.Error(ex, $"PerfFixes: applier '{applier.Name}' failed during {phase}.");
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
                context?.Logger.Error(ex, $"PerfFixes: applier '{applier.Name}' failed during Update.");
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
                context?.Logger.Error(ex, $"PerfFixes: applier '{applier.Name}' failed during SceneLoaded.");
                return false;
            }
        }

        private void DisableApplier(int index, IPerfApplier applier, string failedPhase)
        {
            Guard(applier.Revert, applier, "failure cleanup");
            appliers.RemoveAt(index);
            context?.Logger.Warn($"PerfFixes: disabled applier '{applier.Name}' after {failedPhase} failure.");
        }
    }
}
