using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>One authoritative namespace lookup across enabled, disabled, and known missing package owners.</summary>
    internal sealed class LaunchProfileIndex
    {
        private readonly IReadOnlyList<ResolvedPackage> enabled;
        private readonly IReadOnlyList<ResolvedPackage> disabled;
        private readonly string[] namespaces;

        internal LaunchProfileIndex(EffectiveProfile profile)
        {
            enabled = profile.Packages.OrderBy(package => package.Id, StringComparer.Ordinal).ToArray();
            disabled = profile.DisabledPackages.Where(package => !enabled.Any(selected => Same(selected.Id, package.Id)))
                .OrderBy(package => package.Id, StringComparer.Ordinal).ToArray();
            var all = enabled.Concat(disabled).ToArray();
            namespaces = all.Select(package => package.Id)
                .Concat(all.SelectMany(package => package.Snapshot.Dependencies.Keys))
                .Concat(all.SelectMany(package => package.Snapshot.OptionalDependencies.Keys))
                .OrderBy(id => id, StringComparer.Ordinal).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(id => id.Length).ThenBy(id => id, StringComparer.Ordinal).ToArray();
            Duplicates = all.GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => new LaunchBlock(LaunchBlockCode.DeclarationIdAmbiguous,
                    group.Select(package => package.Id).OrderBy(id => id, StringComparer.Ordinal).First())).ToArray();
        }

        internal IReadOnlyList<LaunchBlock> Duplicates { get; }
        internal IReadOnlyList<ResolvedPackage> Enabled => enabled;

        internal Owner? OwnerOf(string id)
        {
            var name = namespaces.FirstOrDefault(candidate => id.StartsWith(candidate + ".", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            var active = enabled.Where(package => Same(package.Id, name)).ToArray();
            var inactive = disabled.Where(package => Same(package.Id, name)).ToArray();
            return new Owner(name, active.FirstOrDefault() ?? inactive.FirstOrDefault(), active.Length == 0 && inactive.Length > 0,
                active.Length + inactive.Length > 1);
        }

        internal ResolvedPackage? ResolveOwner(string id, LaunchBlockCode missing, LaunchBlockCode disabledCode, List<LaunchBlock> blocks)
        {
            var owner = OwnerOf(id);
            if (owner?.Ambiguous == true)
            {
                blocks.Add(new LaunchBlock(LaunchBlockCode.DeclarationIdAmbiguous, id));
                return null;
            }
            if (owner?.Disabled == true)
            {
                blocks.Add(new LaunchBlock(disabledCode, owner.Package!.Id, owner.Package.Version));
                return null;
            }
            if (owner?.Package == null)
            {
                blocks.Add(new LaunchBlock(missing, id));
                return null;
            }
            return owner.Package;
        }

        internal bool Owns(ResolvedPackage package, string id)
        {
            var owner = OwnerOf(id);
            return owner?.Package != null && !owner.Disabled && !owner.Ambiguous && Same(owner.Package.Id, package.Id);
        }

        internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        internal sealed class Owner
        {
            internal Owner(string id, ResolvedPackage? package, bool disabled, bool ambiguous)
            {
                Id = id;
                Package = package;
                Disabled = disabled;
                Ambiguous = ambiguous;
            }
            internal string Id { get; }
            internal ResolvedPackage? Package { get; }
            internal bool Disabled { get; }
            internal bool Ambiguous { get; }
        }
    }
}
