using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public static partial class LaunchResolver
    {
        private static void CheckReference(ResolvedPackage referrer, ResolvedPackage owner, string reference,
            LaunchBlockCode missingDependency, List<LaunchBlock> blocks)
        {
            if (Same(referrer.Id, owner.Id)) return;
            var dependency = referrer.Snapshot.Dependencies.FirstOrDefault(pair => Same(pair.Key, owner.Id));
            if (dependency.Key == null)
                blocks.Add(new LaunchBlock(missingDependency, reference, owner.Version));
            else if (!VersionUtil.AllowsRange(owner.Version, dependency.Value))
                blocks.Add(new LaunchBlock(LaunchBlockCode.TargetPackageVersionUnsatisfied, reference, owner.Version));
        }

        private static bool BindingsMatch(RuntimeBindingSnapshot bindings, EffectiveProfile profile) =>
            bindings.ProfileId == profile.ProfileId && bindings.ProfileRevision == profile.Revision
                && bindings.PackageSetDigest == PackageSetDigest.Of(profile.Packages);

        private static void ApplyModeAvailability(GamemodeMatch mode, RuntimeObservation observed,
            RuntimeBindingSnapshot? bindings, bool requireBindings, List<LaunchBlock> blocks)
        {
            foreach (var block in observed.Failures("gamemode", mode.Declaration.Id))
                if (!requireBindings || block.Code != LaunchBlockCode.GamemodeUnbound) blocks.Add(block);
            if (!requireBindings) return;
            if (bindings == null || !bindings.BoundGamemodeIds.Any(id => Same(id, mode.Declaration.Id)))
                blocks.Add(new LaunchBlock(LaunchBlockCode.GamemodeUnbound, mode.Declaration.Id, mode.Package.Version));
            if (bindings != null)
                blocks.AddRange(bindings.Availability.Where(item => item.Kind == "gamemode" && Same(item.Id, mode.Declaration.Id)).SelectMany(item => item.Blocks));
        }

        private static void ApplyWorldAvailability(WorldMatch world, RuntimeObservation observed,
            RuntimeBindingSnapshot? bindings, bool requireBindings, List<LaunchBlock> blocks)
        {
            foreach (var block in observed.Failures("world", world.WorldId, world.FamilyId))
            {
                if (block.Code == LaunchBlockCode.WorldUnbound)
                {
                    if (!requireBindings) blocks.Add(new LaunchBlock(LaunchBlockCode.WorldUnbound, world.WorldId, world.Package.Version));
                }
                else blocks.Add(block);
            }
            if (!requireBindings) return;
            if (bindings == null || !bindings.BoundWorldIds.Any(id => Same(id, world.FamilyId ?? world.Declaration.Id)))
                blocks.Add(new LaunchBlock(LaunchBlockCode.WorldUnbound, world.WorldId, world.Package.Version));
            if (bindings != null)
                blocks.AddRange(bindings.Availability.Where(item => item.Kind == "world"
                    && (Same(item.Id, world.WorldId) || Same(item.Id, world.FamilyId))).SelectMany(item => item.Blocks));
        }
    }
}
