using System;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
{
    internal enum RobotBodyCompositionStage
    {
        PrefabLookup, MissingPrefab, Roots, Hierarchy, InactiveClone, DormantBrain, Quieten, Appearance,
        AnchorSetup, Renderers, Mesh, Bake, VertexBudget, Materials, NoVisibleMeshes, AnchorActivation
    }

    internal sealed class RobotBodyCompositionDiagnostics
    {
        private readonly object gate = new object();
        private uint reported;

        internal void Report(IModLogger? hostLogger, RobotBodyCompositionStage stage)
        {
            if (hostLogger == null || (uint)stage >= 32) return;
            var label = stage switch
            {
                RobotBodyCompositionStage.PrefabLookup => "prefab-lookup",
                RobotBodyCompositionStage.MissingPrefab => "missing-prefab",
                RobotBodyCompositionStage.Roots => "roots",
                RobotBodyCompositionStage.Hierarchy => "hierarchy",
                RobotBodyCompositionStage.InactiveClone => "inactive-clone",
                RobotBodyCompositionStage.DormantBrain => "dormant-brain",
                RobotBodyCompositionStage.Quieten => "quieten",
                RobotBodyCompositionStage.Appearance => "appearance",
                RobotBodyCompositionStage.AnchorSetup => "anchor-setup",
                RobotBodyCompositionStage.Renderers => "renderers",
                RobotBodyCompositionStage.Mesh => "mesh",
                RobotBodyCompositionStage.Bake => "bake",
                RobotBodyCompositionStage.VertexBudget => "vertex-budget",
                RobotBodyCompositionStage.Materials => "materials",
                RobotBodyCompositionStage.NoVisibleMeshes => "no-visible-meshes",
                RobotBodyCompositionStage.AnchorActivation => "anchor-activation",
                _ => null,
            };
            if (label == null) return;

            var bit = 1u << (int)stage;
            lock (gate)
            {
                if ((reported & bit) != 0) return;
                reported |= bit;
            }

            try { hostLogger.Warn("RobotKit body composition refused stage=" + label); }
            catch (Exception) { /* Logging is best effort and must not replace the composition result. */ }
        }
    }
}
