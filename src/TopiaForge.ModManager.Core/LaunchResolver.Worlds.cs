using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public static partial class LaunchResolver
    {
        private static WorldMatch? FindWorld(LaunchProfileIndex index, string id, RuntimeObservation observed,
            bool allowInstance, List<LaunchBlock> blocks)
        {
            var owner = index.ResolveOwner(id, LaunchBlockCode.WorldNotDeclared, LaunchBlockCode.WorldPackageDisabled, blocks);
            if (owner == null) return null;
            var declarations = owner.Snapshot.Contributions?.Worlds ?? new List<ModWorldDeclaration>();
            var exact = declarations.Where(item => Same(item.Id, id)).ToArray();
            if (exact.Length > 1)
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.DeclarationIdAmbiguous, id));
                return null;
            }
            if (exact.Length == 1)
            {
                if (IsDiscovered(exact[0])) blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotStaticallyDeclared, id));
                return new WorldMatch(exact[0], owner, exact[0].Id);
            }

            var families = declarations.Where(item => IsDiscovered(item) && id.StartsWith(item.Id + ".", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Id.Length).ToArray();
            if (families.Length == 0)
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotDeclared, id));
                return null;
            }
            if (families.Length > 1 && families[0].Id.Length == families[1].Id.Length)
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.DeclarationIdAmbiguous, id));
                return null;
            }
            var instance = observed.DiscoveredWorlds.FirstOrDefault(world => Same(world.Id, id) && Same(world.FamilyId, families[0].Id));
            if (!allowInstance)
                blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotStaticallyDeclared, id));
            else if (instance == null)
                blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotDeclared, id));
            return new WorldMatch(families[0], owner, instance?.Id ?? id, families[0].Id);
        }

        private static bool Admitted(ModWorldPolicy policy, WorldMatch world, string modeId)
        {
            if (policy.Policy == ModWorldPolicy.OpenPolicy) return Consents(world.Declaration, modeId);
            return Same(world.WorldId, policy.Default) || (policy.Policy == ModWorldPolicy.ListPolicy && policy.Allow.Any(id => Same(id, world.WorldId)));
        }

        private static bool Consents(ModWorldDeclaration world, string modeId) => world.OpenToAnyCompatible == true
            || world.OpenTo?.Any(id => Same(id, modeId)) == true;

        private static bool IsDiscovered(ModWorldDeclaration world) => world.Content?.Kind == ModWorldContent.DiscoveredKind;

        private static bool EveryCandidateExplicitlyUnavailable(LaunchProfileIndex index, InstallFacts install, TargetMatch target, GamemodeMatch mode,
            WorldMatch selected, LaunchRequest request, RuntimeObservation observed)
        {
            var policy = target.Declaration.World!;
            if (request.WorldOverride != null || policy.AllowPlayerOverride != true || policy.Policy == ModWorldPolicy.FixedPolicy)
                return observed.ExplicitlyUnavailable(selected.WorldId);
            var candidates = new List<string> { policy.Default };
            if (policy.Policy == ModWorldPolicy.ListPolicy) candidates.AddRange(policy.Allow);
            else if (policy.Policy == ModWorldPolicy.OpenPolicy)
            {
                var possible = index.Enabled.SelectMany(package => (package.Snapshot.Contributions?.Worlds ?? new List<ModWorldDeclaration>())
                    .Where(world => !IsDiscovered(world)).Select(world => world.Id))
                    .Concat(observed.DiscoveredWorlds.Select(world => world.Id));
                foreach (var id in possible.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var ignored = new List<LaunchBlock>();
                    var world = FindWorld(index, id, observed, allowInstance: true, ignored);
                    if (world != null)
                        foreach (var consent in world.Declaration.OpenTo ?? Enumerable.Empty<string>())
                            FindGamemode(index, consent, world.Package, LaunchBlockCode.WorldConsentRefNotADependency, ignored);
                    if (world != null && ignored.Count == 0 && Consents(world.Declaration, mode.Declaration.Id)
                        && SupportsThisInstall(install, world.Package.Snapshot)
                        && PairCompatible(mode.Declaration, world.Declaration)) candidates.Add(world.WorldId);
                }
            }
            return candidates.Distinct(StringComparer.OrdinalIgnoreCase).All(observed.ExplicitlyUnavailable);
        }

        private static bool PairCompatible(ModGamemodeDeclaration mode, ModWorldDeclaration world)
        {
            var requirements = mode.WorldRequirements;
            return (requirements?.Spawn != ModSpawnPolicy.AuthoredMarkerKind || world.Spawn?.Kind == ModSpawnPolicy.AuthoredMarkerKind)
                && world.Transitions.Any(transition => requirements == null || requirements.Transitions.Count == 0 || requirements.Transitions.Contains(transition));
        }

        private static string ResolveTransition(ModLaunchTargetDeclaration target, ModGamemodeDeclaration mode, WorldMatch world,
            LaunchRequest request, List<LaunchBlock> blocks)
        {
            var required = mode.WorldRequirements?.Transitions;
            var offered = world.Declaration.Transitions.Where(value => required == null || required.Count == 0 || required.Contains(value, StringComparer.Ordinal)).ToArray();
            if (mode.WorldRequirements?.Spawn == ModSpawnPolicy.AuthoredMarkerKind && world.Declaration.Spawn?.Kind != ModSpawnPolicy.AuthoredMarkerKind)
                blocks.Add(new LaunchBlock(LaunchBlockCode.SpawnRequirementUnsatisfied, world.WorldId));
            if (offered.Length == 0) blocks.Add(new LaunchBlock(LaunchBlockCode.TransitionUnsatisfiable, world.WorldId));
            if (request.TransitionOverride != null)
            {
                if (target.Transition != ModLaunchTargetDeclaration.PlayerChoiceTransition || !offered.Contains(request.TransitionOverride, StringComparer.Ordinal))
                    blocks.Add(new LaunchBlock(LaunchBlockCode.TransitionNotOffered, request.TransitionOverride));
                return request.TransitionOverride;
            }
            if (target.Transition != null && target.Transition != ModLaunchTargetDeclaration.AutoTransition
                && target.Transition != ModLaunchTargetDeclaration.PlayerChoiceTransition)
            {
                if (!offered.Contains(target.Transition, StringComparer.Ordinal))
                    blocks.Add(new LaunchBlock(LaunchBlockCode.TransitionNotOffered, target.Transition));
                return target.Transition;
            }
            return ModTransitions.ByPrecedence.FirstOrDefault(value => offered.Contains(value, StringComparer.Ordinal)) ?? string.Empty;
        }

        private static bool SupportsThisInstall(InstallFacts install, ModManifest manifest) =>
            (install.Platform.Length == 0 || manifest.Platforms.Count == 0 || manifest.Platforms.Contains(install.Platform, StringComparer.OrdinalIgnoreCase))
            && (install.Architecture.Length == 0 || manifest.Architectures.Count == 0 || manifest.Architectures.Contains(install.Architecture, StringComparer.OrdinalIgnoreCase))
            && (install.ContentTarget.Length == 0 || manifest.ContentTargets.Count == 0 || manifest.ContentTargets.Contains(install.ContentTarget, StringComparer.OrdinalIgnoreCase))
            && (install.GameVersion.Length == 0 || VersionUtil.AllowsRange(install.GameVersion, manifest.SupportedGameVersionRange));
    }
}
