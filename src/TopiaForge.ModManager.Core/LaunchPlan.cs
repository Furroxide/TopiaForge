using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public sealed class LaunchRequest
    {
        public LaunchRequest(string targetId, string? worldOverride = null, string? transitionOverride = null)
        {
            TargetId = LaunchContractValues.Identifier(targetId, nameof(targetId));
            WorldOverride = worldOverride == null ? null
                : LaunchContractValues.Identifier(worldOverride, nameof(worldOverride));
            TransitionOverride = transitionOverride == null ? null
                : LaunchContractValues.Choice(transitionOverride, nameof(transitionOverride), ModTransitions.ByPrecedence);
        }

        public string TargetId { get; }
        public string? WorldOverride { get; }
        public string? TransitionOverride { get; }
    }

    /// <summary>Immutable, serializable claim about a resolution. Runtime re-resolution establishes authority.</summary>
    public sealed class LaunchPlanDescriptor
    {
        public LaunchPlanDescriptor(string targetId, string gamemodeId, string worldId, string transition,
            LaunchRequest request, IEnumerable<PackageIdentity> packages, string? worldFamilyId = null, string? digest = null)
        {
            TargetId = LaunchContractValues.Identifier(targetId, nameof(targetId));
            GamemodeId = LaunchContractValues.Identifier(gamemodeId, nameof(gamemodeId));
            WorldId = LaunchContractValues.Identifier(worldId, nameof(worldId));
            WorldFamilyId = LaunchContractValues.OptionalIdentifier(worldFamilyId, nameof(worldFamilyId));
            Transition = LaunchContractValues.Choice(transition, nameof(transition), ModTransitions.ByPrecedence);
            Request = new LaunchRequest(request.TargetId, request.WorldOverride, request.TransitionOverride);
            Packages = LaunchContractValues.Packages(packages);
            Digest = PackageSetDigest.Of(Packages);
            if (digest != null && LaunchContractValues.Digest(digest) != Digest)
                throw new ArgumentException("The descriptor digest does not match its package identities.", nameof(digest));
            if (!string.Equals(TargetId, Request.TargetId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The resolved target must match the original request.", nameof(targetId));
            if (WorldFamilyId != null && (!WorldId.StartsWith(WorldFamilyId + ".", StringComparison.OrdinalIgnoreCase)
                || WorldId.Length <= WorldFamilyId.Length + 1))
                throw new ArgumentException("The resolved world must be a concrete instance of its family.");
        }

        public string TargetId { get; }
        public string GamemodeId { get; }
        public string WorldId { get; }
        public string? WorldFamilyId { get; }
        public string Transition { get; }
        public LaunchRequest Request { get; }
        public IReadOnlyList<PackageIdentity> Packages { get; }
        public string Digest { get; }
    }

    /// <summary>Resolved authority plus private declaration snapshots; getters never expose owned mutable DTOs.</summary>
    public sealed class LaunchPlan
    {
        private readonly ModManifest declarations;

        internal LaunchPlan(LaunchPlanDescriptor descriptor, ModLaunchTargetDeclaration target,
            ModGamemodeDeclaration gamemode, ModWorldDeclaration world)
        {
            Descriptor = descriptor;
            declarations = ModManifestJson.CopyForLaunch(new ModManifest
            {
                Contributions = new ModContributions
                {
                    LaunchTargets = new List<ModLaunchTargetDeclaration> { target },
                    Gamemodes = new List<ModGamemodeDeclaration> { gamemode },
                    Worlds = new List<ModWorldDeclaration> { world }
                }
            });
        }

        public LaunchPlanDescriptor Descriptor { get; }
        public string TargetId => Descriptor.TargetId;
        public string GamemodeId => Descriptor.GamemodeId;
        public string WorldId => Descriptor.WorldId;
        public string? WorldFamilyId => Descriptor.WorldFamilyId;
        public string Transition => Descriptor.Transition;
        public LaunchRequest Request => Descriptor.Request;
        public IReadOnlyList<PackageIdentity> Packages => Descriptor.Packages;
        public string Digest => Descriptor.Digest;
        public ModLaunchTargetDeclaration Target => ModManifestJson.CopyForLaunch(declarations).Contributions!.LaunchTargets[0];
        public ModGamemodeDeclaration Gamemode => ModManifestJson.CopyForLaunch(declarations).Contributions!.Gamemodes[0];
        public ModWorldDeclaration World => ModManifestJson.CopyForLaunch(declarations).Contributions!.Worlds[0];
    }

    /// <summary>The established FNV-1a package-set consistency digest, independent of JSON ordering.</summary>
    public static class PackageSetDigest
    {
        public static string Of(IReadOnlyList<ResolvedPackage> packages) => Of(packages.Select(package => package.Identity));
        public static string Of(IEnumerable<PackageIdentity> packages) => OfCanonical(packages
            .Select(package => package.Id + "@" + package.Version).OrderBy(value => value, StringComparer.Ordinal));

        public static string OfCanonical(IEnumerable<string> canonicalEntries)
        {
            var hash = 14695981039346656037UL;
            var first = true;
            foreach (var entry in canonicalEntries)
            {
                if (!first) hash = Mix(hash, (byte)'\n');
                first = false;
                foreach (var character in entry)
                {
                    hash = Mix(hash, (byte)(character & 0xff));
                    hash = Mix(hash, (byte)((character >> 8) & 0xff));
                }
            }

            return hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        private static ulong Mix(ulong hash, byte value) => unchecked((hash ^ value) * 1099511628211UL);
    }
}
