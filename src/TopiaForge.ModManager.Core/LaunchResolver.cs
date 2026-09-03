using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Turns a request to launch a target into one decided plan, or into every reason there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure data in, pure data out. No filesystem, no registry, no catalog file. That constraint is
    /// the point: the launcher's preflight and the manager's own view of a launch used to be two
    /// separate pieces of code reading two different sources, and they disagreed. A launch could pass
    /// a check against a catalog written by a previous run and then fail against the profile that is
    /// actually enabled.
    /// </para>
    /// <para>
    /// Every applicable reason is reported, not the first. An author who fixes one blocking reason and
    /// is immediately handed the next has been made to discover their own manifest one error at a
    /// time.
    /// </para>
    /// <para>
    /// Mirrored in <c>packages/launcher_domain/lib/src/launch_resolution.dart</c>, and the shared
    /// fixtures under <c>tests/fixtures/gamemode-v6/resolution</c> are what hold the two together.
    /// </para>
    /// </remarks>
    public static class LaunchResolver
    {
        /// <summary>Resolves one launch request against one effective profile.</summary>
        /// <param name="profile">The enabled package set at pinned versions.</param>
        /// <param name="request">What the player asked to launch.</param>
        /// <param name="observation">What the game reported, or null for a profile that has never run.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public static LaunchResolution Resolve(
            EffectiveProfile profile,
            LaunchRequest request,
            RuntimeObservation? observation = null)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var observed = observation ?? RuntimeObservation.None;
            var blocks = new List<LaunchBlock>();
            var index = new ProfileIndex(profile);

            var target = index.FindTarget(request.TargetId, blocks);
            if (target == null)
            {
                return LaunchResolution.Blocked(blocks);
            }

            var gamemode = ResolveGamemode(index, target, observed, blocks);
            var world = ResolveWorld(index, profile, target, request, observed, blocks);
            if (gamemode == null || world == null)
            {
                return LaunchResolution.Blocked(blocks);
            }

            var transition = ResolveTransition(
                target.Value, gamemode.Value, world.Value, request, blocks);
            if (blocks.Count > 0)
            {
                return LaunchResolution.Blocked(blocks);
            }

            return LaunchResolution.Success(new LaunchPlan(
                target.Value.Declaration.Id,
                gamemode.Value.Declaration.Id,
                world.Value.Declaration.Id,
                transition,
                index.OrderedPackages,
                world.Value.InstanceId));
        }

        /// <summary>
        /// Revalidates a plan against the set that actually loaded, before any scene work begins.
        /// </summary>
        /// <remarks>
        /// This is what makes the preflight an invariant instead of a promise. A plan resolved against
        /// one package set and executed against another is exactly the disagreement the resolver exists
        /// to end, and it is invisible without this comparison.
        /// </remarks>
        /// <param name="plan">The plan about to be prepared.</param>
        /// <param name="loaded">The packages the manager actually loaded.</param>
        /// <returns>Empty when the sets agree, or a single mismatch reason naming the plan's target.</returns>
        public static IReadOnlyList<LaunchBlock> Revalidate(
            LaunchPlan plan,
            IReadOnlyList<ResolvedPackage> loaded)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (loaded == null)
            {
                throw new ArgumentNullException(nameof(loaded));
            }

            return string.Equals(PackageSetDigest.Of(loaded), plan.Digest, StringComparison.Ordinal)
                ? Array.Empty<LaunchBlock>()
                : new[] { new LaunchBlock(LaunchBlockCode.PlanPackageSetMismatch, plan.LaunchTargetId) };
        }

        private static GamemodeMatch? ResolveGamemode(
            ProfileIndex index,
            TargetMatch? target,
            RuntimeObservation observed,
            List<LaunchBlock> blocks)
        {
            var declaration = target!.Value.Declaration;
            var owner = index.OwnerOf(declaration.Gamemode, blocks, LaunchBlockCode.DeclarationIdAmbiguous);
            if (owner == null)
            {
                var off = index.DeclaringDisabledPackage(candidate =>
                    declaration.Gamemode.StartsWith(
                        candidate.Id + ".", StringComparison.OrdinalIgnoreCase));
                blocks.Add(off == null
                    ? new LaunchBlock(LaunchBlockCode.GamemodeNotDeclared, declaration.Gamemode)
                    : new LaunchBlock(LaunchBlockCode.GamemodePackageDisabled, off.Id, off.Version));
                return null;
            }

            // A reference out of the declaring package has to resolve through a dependency that package
            // requires. An optional one would make the launch work only where the optional package
            // happens to be installed, which is a failure the author never sees.
            var ownership = ReferenceIsOwned(target.Value.Package, owner);
            if (ownership != ReferenceOwnership.Owned)
            {
                blocks.Add(new LaunchBlock(
                    ownership == ReferenceOwnership.VersionUnsatisfied
                        ? LaunchBlockCode.TargetPackageVersionUnsatisfied
                        : LaunchBlockCode.GamemodeRefNotADependency,
                    declaration.Gamemode,
                    owner.Version));
                return null;
            }

            var found = owner.Manifest.Contributions?.Gamemodes
                .FirstOrDefault(item => IdEquals(item.Id, declaration.Gamemode));
            if (found == null)
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.GamemodeNotDeclared, declaration.Gamemode));
                return null;
            }

            if (observed.UnboundGamemodeIds.Any(id => IdEquals(id, found.Id)))
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.GamemodeUnbound, found.Id, owner.Version));
                return null;
            }

            return new GamemodeMatch(found, owner);
        }

        private static WorldMatch? ResolveWorld(
            ProfileIndex index,
            EffectiveProfile profile,
            TargetMatch? target,
            LaunchRequest request,
            RuntimeObservation observed,
            List<LaunchBlock> blocks)
        {
            var policy = target!.Value.Declaration.World;
            if (policy == null)
            {
                blocks.Add(new LaunchBlock(
                    LaunchBlockCode.WorldNotDeclared, target.Value.Declaration.Id));
                return null;
            }

            var requested = request.WorldOverride.Length > 0 ? request.WorldOverride : policy.Default;
            if (request.WorldOverride.Length > 0 && !AdmitsChoice(policy))
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotAdmittedByPolicy, requested));
                return null;
            }

            var owner = index.OwnerOf(requested, blocks, LaunchBlockCode.DeclarationIdAmbiguous);
            if (owner == null)
            {
                var off = index.DeclaringDisabledPackage(candidate =>
                    requested.StartsWith(candidate.Id + ".", StringComparison.OrdinalIgnoreCase));
                blocks.Add(off == null
                    ? new LaunchBlock(LaunchBlockCode.WorldNotDeclared, requested)
                    : new LaunchBlock(LaunchBlockCode.WorldPackageDisabled, off.Id, off.Version));
                return null;
            }

            var ownership = ReferenceIsOwned(target.Value.Package, owner);
            if (ownership != ReferenceOwnership.Owned)
            {
                blocks.Add(new LaunchBlock(
                    ownership == ReferenceOwnership.VersionUnsatisfied
                        ? LaunchBlockCode.TargetPackageVersionUnsatisfied
                        : LaunchBlockCode.WorldRefNotADependency,
                    requested,
                    owner.Version));
                return null;
            }

            var instanceId = string.Empty;
            var found = owner.Manifest.Contributions?.Worlds
                .FirstOrDefault(item => IdEquals(item.Id, requested));
            if (found == null)
            {
                // Not a declared world, but it may be an observed member of a declared family. A
                // family id is a prefix; only its members are launchable.
                var family = owner.Manifest.Contributions?.Worlds.FirstOrDefault(item =>
                    IsDiscovered(item)
                    && requested.StartsWith(item.Id + ".", StringComparison.OrdinalIgnoreCase));
                if (family == null)
                {
                    blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotDeclared, requested));
                    return null;
                }

                if (!observed.DiscoveredWorldIds.Any(id => IdEquals(id, requested)))
                {
                    blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotStaticallyDeclared, requested));
                    return null;
                }

                instanceId = requested;
                found = family;
            }
            else if (IsDiscovered(found))
            {
                // The family itself, not a member of it. There is nothing to load.
                blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotStaticallyDeclared, requested));
                return null;
            }

            if (!AdmittedByPolicy(policy, found, target.Value.Declaration.Gamemode, requested, blocks))
            {
                return null;
            }

            if (observed.UnavailableWorldIds.Any(id => IdEquals(id, requested)))
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.NoAvailableTarget, target.Value.Declaration.Id));
                return null;
            }

            if (!SupportsThisInstall(profile.Install, owner.Manifest))
            {
                blocks.Add(new LaunchBlock(
                    LaunchBlockCode.WorldPlatformUnsupported, owner.Id, owner.Version));
                return null;
            }

            return new WorldMatch(found, owner, instanceId);
        }

        private static bool AdmittedByPolicy(
            ModWorldPolicy policy,
            ModWorldDeclaration world,
            string gamemodeId,
            string requested,
            List<LaunchBlock> blocks)
        {
            if (string.Equals(policy.Policy, ModWorldPolicy.OpenPolicy, StringComparison.Ordinal))
            {
                // Consent is scoped to the open policy alone. Requiring it everywhere would make a
                // world's package depend on the gamemodes that use it, and the first-party graph
                // already runs the other way.
                if (IdEquals(requested, policy.Default)
                    || world.OpenToAnyCompatible == true
                    || world.OpenTo.Any(id => IdEquals(id, gamemodeId)))
                {
                    return true;
                }

                blocks.Add(new LaunchBlock(LaunchBlockCode.WorldConsentMissing, requested));
                return false;
            }

            var admitted = IdEquals(requested, policy.Default)
                || (string.Equals(policy.Policy, ModWorldPolicy.ListPolicy, StringComparison.Ordinal)
                    && policy.Allow.Any(id => IdEquals(id, requested)));
            if (admitted)
            {
                return true;
            }

            blocks.Add(new LaunchBlock(LaunchBlockCode.WorldNotAdmittedByPolicy, requested));
            return false;
        }

        /// <summary>
        /// Chooses the transition, deterministically.
        /// </summary>
        /// <remarks>
        /// Scene replacement outranks the additive arena, and the order is fixed rather than
        /// discovered. A world that supports both ships today, so "auto" without a stated precedence
        /// would mean a launch whose behaviour depends on declaration order.
        /// </remarks>
        private static string ResolveTransition(
            TargetMatch target,
            GamemodeMatch gamemode,
            WorldMatch world,
            LaunchRequest request,
            List<LaunchBlock> blocks)
        {
            var required = gamemode.Declaration.WorldRequirements?.Transitions;
            var offered = world.Declaration.Transitions
                .Where(item => required == null || required.Count == 0 || required.Contains(item, StringComparer.Ordinal))
                .ToList();
            if (offered.Count == 0)
            {
                blocks.Add(new LaunchBlock(
                    LaunchBlockCode.TransitionUnsatisfiable, world.Declaration.Id));
                return string.Empty;
            }

            if (string.Equals(
                    gamemode.Declaration.WorldRequirements?.Spawn,
                    ModSpawnPolicy.AuthoredMarkerKind,
                    StringComparison.Ordinal)
                && !string.Equals(
                    world.Declaration.Spawn?.Kind,
                    ModSpawnPolicy.AuthoredMarkerKind,
                    StringComparison.Ordinal))
            {
                blocks.Add(new LaunchBlock(
                    LaunchBlockCode.SpawnRequirementUnsatisfied, world.Declaration.Id));
                return string.Empty;
            }

            var declared = target.Declaration.Transition;
            var offersChoice = string.Equals(
                declared, ModLaunchTargetDeclaration.PlayerChoiceTransition, StringComparison.Ordinal);
            if (request.TransitionOverride.Length > 0)
            {
                if (!offersChoice || !offered.Contains(request.TransitionOverride, StringComparer.Ordinal))
                {
                    blocks.Add(new LaunchBlock(
                        LaunchBlockCode.TransitionNotOffered, request.TransitionOverride));
                    return string.Empty;
                }

                return request.TransitionOverride;
            }

            if (declared.Length == 0
                || offersChoice
                || string.Equals(declared, ModLaunchTargetDeclaration.AutoTransition, StringComparison.Ordinal))
            {
                return ModTransitions.ByPrecedence.First(item =>
                    offered.Contains(item, StringComparer.Ordinal));
            }

            if (!offered.Contains(declared, StringComparer.Ordinal))
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.TransitionNotOffered, declared));
                return string.Empty;
            }

            return declared;
        }

        private static bool SupportsThisInstall(InstallFacts install, ModManifest manifest)
        {
            if (install.Platform.Length > 0
                && manifest.Platforms.Count > 0
                && !manifest.Platforms.Contains(install.Platform, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            if (install.Architecture.Length > 0
                && manifest.Architectures.Count > 0
                && !manifest.Architectures.Contains(install.Architecture, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            if (install.ContentTarget.Length > 0
                && manifest.ContentTargets.Count > 0
                && !manifest.ContentTargets.Contains(install.ContentTarget, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            return install.GameVersion.Length == 0
                || VersionUtil.AllowsRange(install.GameVersion, manifest.SupportedGameVersionRange);
        }

        /// <summary>
        /// A reference is owned when the referencing package declares it, or requires the package that
        /// does at a version the pin satisfies.
        /// </summary>
        private static ReferenceOwnership ReferenceIsOwned(
            ResolvedPackage referrer,
            ResolvedPackage owner)
        {
            if (string.Equals(referrer.Id, owner.Id, StringComparison.OrdinalIgnoreCase))
            {
                return ReferenceOwnership.Owned;
            }

            foreach (var dependency in referrer.Manifest.Dependencies)
            {
                if (!string.Equals(dependency.Key, owner.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // A dependency that is declared but pins the wrong version is a different fix from
                // one that was never declared at all, and saying so saves the author guessing which.
                return string.IsNullOrWhiteSpace(dependency.Value)
                    || VersionUtil.AllowsRange(owner.Version, dependency.Value)
                        ? ReferenceOwnership.Owned
                        : ReferenceOwnership.VersionUnsatisfied;
            }

            return ReferenceOwnership.NotADependency;
        }

        /// <summary>How a cross-package reference resolves, or fails to.</summary>
        private enum ReferenceOwnership
        {
            Owned,
            NotADependency,
            VersionUnsatisfied
        }

        private static bool IsDiscovered(ModWorldDeclaration world) =>
            string.Equals(world.Content?.Kind, ModWorldContent.DiscoveredKind, StringComparison.Ordinal);

        private static bool AdmitsChoice(ModWorldPolicy policy) =>
            policy.AllowPlayerOverride == true
            || string.Equals(policy.Policy, ModWorldPolicy.OpenPolicy, StringComparison.Ordinal)
            || string.Equals(policy.Policy, ModWorldPolicy.ListPolicy, StringComparison.Ordinal);

        private static bool IdEquals(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private readonly struct TargetMatch
        {
            public TargetMatch(ModLaunchTargetDeclaration declaration, ResolvedPackage package)
            {
                Declaration = declaration;
                Package = package;
            }

            public ModLaunchTargetDeclaration Declaration { get; }

            public ResolvedPackage Package { get; }
        }

        private readonly struct GamemodeMatch
        {
            public GamemodeMatch(ModGamemodeDeclaration declaration, ResolvedPackage package)
            {
                Declaration = declaration;
                Package = package;
            }

            public ModGamemodeDeclaration Declaration { get; }

            public ResolvedPackage Package { get; }
        }

        private readonly struct WorldMatch
        {
            public WorldMatch(ModWorldDeclaration declaration, ResolvedPackage package, string instanceId)
            {
                Declaration = declaration;
                Package = package;
                InstanceId = instanceId;
            }

            public ModWorldDeclaration Declaration { get; }

            public ResolvedPackage Package { get; }

            public string InstanceId { get; }
        }

        /// <summary>Ownership lookup over the enabled set.</summary>
        private sealed class ProfileIndex
        {
            private readonly List<ResolvedPackage> packages;
            private readonly List<ResolvedPackage> disabled;

            public ProfileIndex(EffectiveProfile profile)
            {
                packages = profile.Packages.OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
                disabled = profile.DisabledPackages
                    .OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
                OrderedPackages = packages;
            }

            public IReadOnlyList<ResolvedPackage> OrderedPackages { get; }

            public TargetMatch? FindTarget(string targetId, List<LaunchBlock> blocks)
            {
                foreach (var package in packages)
                {
                    var found = package.Manifest.Contributions?.LaunchTargets
                        .FirstOrDefault(item => IdEquals(item.Id, targetId));
                    if (found != null)
                    {
                        return new TargetMatch(found, package);
                    }
                }

                // Installed but switched off is a different answer from no such thing, and only one
                // of them is a click away from working.
                var off = DeclaringDisabledPackage(candidate =>
                    candidate.Manifest.Contributions?.LaunchTargets
                        .Any(item => IdEquals(item.Id, targetId)) == true);
                blocks.Add(off == null
                    ? new LaunchBlock(LaunchBlockCode.TargetNotDeclared, targetId)
                    : new LaunchBlock(LaunchBlockCode.TargetPackageDisabled, off.Id, off.Version));
                return null;
            }

            /// <summary>Finds a disabled package that would have answered, or null.</summary>
            public ResolvedPackage? DeclaringDisabledPackage(
                Func<ResolvedPackage, bool> declares) => disabled.FirstOrDefault(declares);

            /// <summary>
            /// Finds the package that owns an id, by longest matching name.
            /// </summary>
            /// <remarks>
            /// Longest wins because a package id may contain dots, so a package named
            /// <c>…topiaforge.worlds.mine</c> is a legal name that sits inside
            /// <c>…topiaforge.worlds</c>'s namespace. Falling through to the shorter owner would let
            /// one package answer for ids another package's name covers.
            /// </remarks>
            public ResolvedPackage? OwnerOf(
                string declarationId,
                List<LaunchBlock> blocks,
                LaunchBlockCode ambiguity)
            {
                var candidates = packages
                    .Where(package => declarationId.StartsWith(
                        package.Id + ".", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(package => package.Id.Length)
                    .ToList();
                if (candidates.Count == 0)
                {
                    return null;
                }

                if (candidates.Count > 1 && candidates[0].Id.Length == candidates[1].Id.Length)
                {
                    blocks.Add(new LaunchBlock(ambiguity, declarationId));
                    return null;
                }

                return candidates[0];
            }
        }
    }
}
