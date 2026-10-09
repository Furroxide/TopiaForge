using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Pure resolution over exact installed selections; declarations and observations cannot change ownership.</summary>
    public static partial class LaunchResolver
    {
        public static LaunchResolution Resolve(EffectiveProfile profile, LaunchRequest request, RuntimeObservation? observation = null,
            RuntimeBindingSnapshot? bindings = null) =>
            ResolveCore(profile, request, observation, bindings, requireBindings: bindings != null);

        public static IReadOnlyList<LaunchBlock> Revalidate(LaunchPlan plan, IEnumerable<PackageIdentity> loaded) =>
            Revalidate(plan.Descriptor, loaded);

        public static IReadOnlyList<LaunchBlock> Revalidate(LaunchPlanDescriptor plan, IEnumerable<PackageIdentity> loaded)
        {
            var identities = loaded.ToArray();
            return LaunchContractValues.SamePackages(plan.Packages, identities) && PackageSetDigest.Of(identities) == plan.Digest
                ? Array.Empty<LaunchBlock>()
                : new[] { new LaunchBlock(LaunchBlockCode.PlanPackageSetMismatch, plan.TargetId) };
        }

        public static LaunchResolution ResolveAgain(LaunchPlanDescriptor plan, EffectiveProfile loaded,
            RuntimeObservation? observation = null, RuntimeBindingSnapshot? bindings = null)
        {
            var mismatch = Revalidate(plan, loaded.Packages.Select(package => package.Identity));
            if (mismatch.Count > 0) return LaunchResolution.Blocked(mismatch);
            if (bindings != null && !BindingsMatch(bindings, loaded))
                return LaunchResolution.Blocked(new[] { new LaunchBlock(LaunchBlockCode.PlanPackageSetMismatch, plan.TargetId) });
            var resolution = ResolveCore(loaded, plan.Request, observation, bindings, requireBindings: true);
            if (!resolution.Resolved) return resolution;
            var actual = resolution.Plan!.Descriptor;
            return actual.TargetId == plan.TargetId && actual.GamemodeId == plan.GamemodeId && actual.WorldId == plan.WorldId
                && actual.WorldFamilyId == plan.WorldFamilyId && actual.Transition == plan.Transition
                ? resolution : LaunchResolution.Blocked(new[] { new LaunchBlock(LaunchBlockCode.PlanResolutionMismatch, plan.TargetId) });
        }

        private static LaunchResolution ResolveCore(EffectiveProfile profile, LaunchRequest request, RuntimeObservation? observation,
            RuntimeBindingSnapshot? bindings, bool requireBindings)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (bindings != null && !BindingsMatch(bindings, profile))
                return LaunchResolution.Blocked(new[] { new LaunchBlock(LaunchBlockCode.PlanPackageSetMismatch, request.TargetId) });
            var index = new LaunchProfileIndex(profile);
            if (index.Duplicates.Count > 0) return LaunchResolution.Blocked(index.Duplicates);
            var blocks = new List<LaunchBlock>();
            var observed = observation?.ForProfile(profile) ?? RuntimeObservation.None;
            var target = FindTarget(index, request.TargetId, blocks);
            if (target == null) return LaunchResolution.Blocked(blocks);
            if (!SupportsThisInstall(profile.Install, target.Package.Snapshot))
                blocks.Add(new LaunchBlock(LaunchBlockCode.TargetPlatformUnsupported, target.Package.Id, target.Package.Version));
            var mode = FindGamemode(index, target.Declaration.Gamemode, target.Package,
                LaunchBlockCode.GamemodeRefNotADependency, blocks);
            if (mode != null)
            {
                ApplyModeAvailability(mode, observed, bindings, requireBindings, blocks);
                if (!SupportsThisInstall(profile.Install, mode.Package.Snapshot))
                    blocks.Add(new LaunchBlock(LaunchBlockCode.GamemodePlatformUnsupported, mode.Package.Id, mode.Package.Version));
            }
            var policy = target.Declaration.World;
            if (policy == null)
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotDeclared, target.Declaration.Id));
                return LaunchResolution.Blocked(blocks);
            }

            foreach (var reference in new[] { policy.Default }.Concat(policy.Allow).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var owner = index.OwnerOf(reference);
                if (owner?.Package != null && !owner.Disabled && !owner.Ambiguous)
                    CheckReference(target.Package, owner.Package, reference, LaunchBlockCode.WorldRefNotADependency, blocks);
                FindWorld(index, reference, observed, allowInstance: false, blocks);
            }
            var requested = request.WorldOverride ?? policy.Default;
            if (request.WorldOverride != null && (policy.Policy == ModWorldPolicy.FixedPolicy || policy.AllowPlayerOverride != true))
                blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotAdmittedByPolicy, requested));
            var world = FindWorld(index, requested, observed, allowInstance: request.WorldOverride != null, blocks);
            var transition = string.Empty;
            if (world != null)
            {
                foreach (var consent in world.Declaration.OpenTo ?? Enumerable.Empty<string>())
                    FindGamemode(index, consent, world.Package, LaunchBlockCode.WorldConsentRefNotADependency, blocks);
                if (!Admitted(policy, world, target.Declaration.Gamemode))
                    blocks.Add(new LaunchBlock(policy.Policy == ModWorldPolicy.OpenPolicy
                        ? LaunchBlockCode.WorldConsentMissing : LaunchBlockCode.WorldNotAdmittedByPolicy, world.WorldId));
                ApplyWorldAvailability(world, observed, bindings, requireBindings, blocks);
                if (!SupportsThisInstall(profile.Install, world.Package.Snapshot))
                    blocks.Add(new LaunchBlock(LaunchBlockCode.WorldPlatformUnsupported, world.Package.Id, world.Package.Version));
                if (mode != null)
                {
                    transition = ResolveTransition(target.Declaration, mode.Declaration, world, request, blocks);
                    if (blocks.All(block => block.Code == LaunchBlockCode.WorldUnavailable)
                        && observed.ExplicitlyUnavailable(world.WorldId)
                        && EveryCandidateExplicitlyUnavailable(index, profile.Install, target, mode, world, request, observed))
                        blocks.Add(new LaunchBlock(LaunchBlockCode.NoAvailableTarget, target.Declaration.Id));
                }
            }
            if (blocks.Count > 0 || mode == null || world == null) return LaunchResolution.Blocked(blocks);
            var descriptor = new LaunchPlanDescriptor(target.Declaration.Id, mode.Declaration.Id, world.WorldId, transition,
                request, profile.Packages.Select(package => package.Identity), world.FamilyId);
            return LaunchResolution.Success(new LaunchPlan(descriptor, target.Declaration, mode.Declaration, world.Declaration));
        }

        private static TargetMatch? FindTarget(LaunchProfileIndex index, string id, List<LaunchBlock> blocks)
        {
            var owner = index.ResolveOwner(id, LaunchBlockCode.TargetNotDeclared, LaunchBlockCode.TargetPackageDisabled, blocks);
            if (owner == null) return null;
            var matches = owner.Snapshot.Contributions?.LaunchTargets.Where(item => Same(item.Id, id)).ToArray()
                ?? Array.Empty<ModLaunchTargetDeclaration>();
            if (matches.Length != 1)
            {
                blocks.Add(new LaunchBlock(matches.Length > 1 ? LaunchBlockCode.DeclarationIdAmbiguous : LaunchBlockCode.TargetNotDeclared, id));
                return null;
            }
            return new TargetMatch(matches[0], owner);
        }

        private static GamemodeMatch? FindGamemode(LaunchProfileIndex index, string id, ResolvedPackage referrer,
            LaunchBlockCode dependencyCode, List<LaunchBlock> blocks)
        {
            var owner = index.ResolveOwner(id, LaunchBlockCode.GamemodeNotDeclared, LaunchBlockCode.GamemodePackageDisabled, blocks);
            if (owner == null) return null;
            CheckReference(referrer, owner, id, dependencyCode, blocks);
            var matches = owner.Snapshot.Contributions?.Gamemodes.Where(item => Same(item.Id, id)).ToArray()
                ?? Array.Empty<ModGamemodeDeclaration>();
            if (matches.Length != 1)
            {
                blocks.Add(new LaunchBlock(matches.Length > 1 ? LaunchBlockCode.DeclarationIdAmbiguous : LaunchBlockCode.GamemodeNotDeclared, id));
                return null;
            }
            return new GamemodeMatch(matches[0], owner);
        }

        private static bool Same(string? a, string? b) => LaunchProfileIndex.Same(a, b);
        private sealed class TargetMatch
        {
            internal TargetMatch(ModLaunchTargetDeclaration declaration, ResolvedPackage package) { Declaration = declaration; Package = package; }
            internal ModLaunchTargetDeclaration Declaration { get; }
            internal ResolvedPackage Package { get; }
        }
        private sealed class GamemodeMatch
        {
            internal GamemodeMatch(ModGamemodeDeclaration declaration, ResolvedPackage package) { Declaration = declaration; Package = package; }
            internal ModGamemodeDeclaration Declaration { get; }
            internal ResolvedPackage Package { get; }
        }
        private sealed class WorldMatch
        {
            internal WorldMatch(ModWorldDeclaration declaration, ResolvedPackage package, string worldId, string? familyId = null)
            { Declaration = declaration; Package = package; WorldId = worldId; FamilyId = familyId; }
            internal ModWorldDeclaration Declaration { get; }
            internal ResolvedPackage Package { get; }
            internal string WorldId { get; }
            internal string? FamilyId { get; }
        }
    }
}
