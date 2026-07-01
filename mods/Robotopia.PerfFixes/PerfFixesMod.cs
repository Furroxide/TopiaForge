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

            foreach (var applier in appliers)
            {
                Guard(() => applier.Apply(), applier, "Apply");
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
                Guard(() => applier.Revert(), applier, "Revert");
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
                context?.Logger.Error(ex, $"PerfFixes: applier '{applier.Name}' failed during {phase}.");
            }
        }
    }
}
