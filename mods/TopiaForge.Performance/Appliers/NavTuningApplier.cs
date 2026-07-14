using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Performance.Appliers
{
    /// <summary>
    /// Opt-in, behaviour-adjacent levers set by reflection on live game instances:
    /// <list type="bullet">
    /// <item><c>LightUpdater.shadowRefreshRate</c> — spread managed-light shadow refreshes over more
    /// frames (cheaper; slightly staler shadows for distant lights).</item>
    /// <item><c>RoboPath.Pathfinder.pathfindingBudgetPerFrameMS</c> — cap per-frame pathfinding time
    /// to reduce spikes when many robots repath (paths resolve over more frames).</item>
    /// </list>
    /// Both capture per-instance originals and restore on revert. Re-applied on scene load because the
    /// instances are scene-scoped (the Pathfinder singleton only exists in scenes that have robots).
    /// </summary>
    internal sealed class NavTuningApplier : PerfApplierBase
    {
        private readonly PerformanceConfig config;
        private readonly IModLogger logger;
        private readonly Dictionary<Object, int> lightOriginals = new Dictionary<Object, int>();
        private readonly Dictionary<Object, float> pathfindOriginals = new Dictionary<Object, float>();

        public NavTuningApplier(PerformanceConfig config, IModLogger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public override string Name => "NavTuning";

        private bool AnyEnabled => config.ShadowRefreshRate > 0 || config.PathfindBudgetMs > 0f;

        public override void Apply()
        {
            if (AnyEnabled)
            {
                ApplyAll();
            }
        }

        public override void OnSceneLoaded(string sceneName)
        {
            if (AnyEnabled)
            {
                ApplyAll();
            }
        }

        public override void Revert()
        {
            foreach (var pair in lightOriginals)
            {
                if (pair.Key != null)
                {
                    GameReflectionLite.SetField(pair.Key, "shadowRefreshRate", pair.Value);
                }
            }

            foreach (var pair in pathfindOriginals)
            {
                if (pair.Key != null)
                {
                    GameReflectionLite.SetField(pair.Key, "pathfindingBudgetPerFrameMS", pair.Value);
                }
            }

            lightOriginals.Clear();
            pathfindOriginals.Clear();
        }

        private void ApplyAll()
        {
            GameReflectionLite.PruneDestroyed(lightOriginals);
            GameReflectionLite.PruneDestroyed(pathfindOriginals);

            if (config.ShadowRefreshRate > 0)
            {
                var type = GameReflectionLite.GameType("LightUpdater");
                if (type != null)
                {
                    foreach (var instance in GameReflectionLite.FindAll(type))
                    {
                        if (instance == null)
                        {
                            continue;
                        }

                        // Only write once we have the original captured, so the change is always revertable.
                        if (!lightOriginals.ContainsKey(instance))
                        {
                            if (GameReflectionLite.TryGetField(instance, "shadowRefreshRate", out var current) &&
                                current is int currentInt)
                            {
                                lightOriginals[instance] = currentInt;
                            }
                            else
                            {
                                continue;
                            }
                        }

                        GameReflectionLite.SetField(instance, "shadowRefreshRate", config.ShadowRefreshRate);
                    }
                }
            }

            if (config.PathfindBudgetMs > 0f)
            {
                var type = GameReflectionLite.GameType("RoboPath.Pathfinder");
                if (type != null)
                {
                    var instance = GameReflectionLite.FindFirst(type);
                    if (instance != null)
                    {
                        if (!pathfindOriginals.ContainsKey(instance))
                        {
                            if (GameReflectionLite.TryGetField(instance, "pathfindingBudgetPerFrameMS", out var current) &&
                                current is float currentFloat)
                            {
                                pathfindOriginals[instance] = currentFloat;
                            }
                            else
                            {
                                return;
                            }
                        }

                        GameReflectionLite.SetField(instance, "pathfindingBudgetPerFrameMS", config.PathfindBudgetMs);
                    }
                }
            }
        }
    }
}
