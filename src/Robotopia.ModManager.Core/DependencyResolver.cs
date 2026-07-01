using System;
using System.Collections.Generic;
using System.Linq;

namespace Robotopia.ModManager.Core
{
    public sealed class DependencyResolver
    {
        public LoadOrderResult Resolve(IEnumerable<ModPackage> packages)
        {
            var enabled = packages
                .Where(p => p.IsValid && p.IsEnabled)
                .ToDictionary(p => p.Manifest!.Id, p => p, StringComparer.OrdinalIgnoreCase);

            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in enabled.Values)
            {
                var manifest = package.Manifest!;
                foreach (var dependency in RequiredDependencies(manifest))
                {
                    if (!enabled.TryGetValue(dependency.Id, out var dependencyPackage))
                    {
                        AddError(errors, manifest.Id, "Missing or disabled dependency: " + dependency.Id);
                        continue;
                    }

                    if (!DependencySatisfied(dependencyPackage.Manifest!.Version, dependency))
                    {
                        AddError(errors, manifest.Id, "Dependency " + dependency.Id + " must satisfy " + DependencyRangeText(dependency) + ".");
                    }
                }

                foreach (var conflict in manifest.Conflicts ?? new List<ModConflict>())
                {
                    if (!enabled.TryGetValue(conflict.Id, out var conflictingPackage))
                    {
                        continue;
                    }

                    if (!ConflictMatches(conflictingPackage.Manifest!.Version, conflict))
                    {
                        continue;
                    }

                    var suffix = string.IsNullOrWhiteSpace(conflict.Reason) ? string.Empty : ": " + conflict.Reason;
                    AddError(errors, manifest.Id, "Conflicts with " + conflict.Id + suffix);
                    AddError(errors, conflictingPackage.Manifest.Id, "Conflicts with " + manifest.Id + suffix);
                }
            }

            var graph = enabled.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            foreach (var package in enabled.Values)
            {
                var id = package.Manifest!.Id;
                foreach (var dependency in RequiredDependencies(package.Manifest))
                {
                    if (enabled.TryGetValue(dependency.Id, out var dependencyPackage) &&
                        DependencySatisfied(dependencyPackage.Manifest!.Version, dependency))
                    {
                        graph[id].Add(dependency.Id);
                    }
                }

                foreach (var dependency in OptionalDependencies(package.Manifest))
                {
                    if (enabled.TryGetValue(dependency.Id, out var dependencyPackage) &&
                        DependencySatisfied(dependencyPackage.Manifest!.Version, dependency))
                    {
                        graph[id].Add(dependency.Id);
                    }
                }

                foreach (var after in package.Manifest.LoadAfter ?? new List<string>())
                {
                    if (enabled.ContainsKey(after))
                    {
                        graph[id].Add(after);
                    }
                }
            }

            var ordered = new List<ModPackage>();
            var temporary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var permanent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in graph.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                Visit(id, graph, enabled, temporary, permanent, ordered, errors);
            }

            foreach (var package in enabled.Values)
            {
                if (errors.ContainsKey(package.Manifest!.Id))
                {
                    ordered.Remove(package);
                }
            }

            return new LoadOrderResult(ordered, errors.ToDictionary(k => k.Key, v => (IReadOnlyList<string>)v.Value));
        }

        private static void Visit(
            string id,
            Dictionary<string, HashSet<string>> graph,
            Dictionary<string, ModPackage> packages,
            HashSet<string> temporary,
            HashSet<string> permanent,
            List<ModPackage> ordered,
            Dictionary<string, List<string>> errors)
        {
            if (permanent.Contains(id))
            {
                return;
            }

            if (!temporary.Add(id))
            {
                AddError(errors, id, "Dependency/loadAfter cycle detected.");
                return;
            }

            foreach (var dependency in graph[id].OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                Visit(dependency, graph, packages, temporary, permanent, ordered, errors);
            }

            temporary.Remove(id);
            permanent.Add(id);
            ordered.Add(packages[id]);
        }

        private static void AddError(Dictionary<string, List<string>> errors, string id, string error)
        {
            if (!errors.TryGetValue(id, out var list))
            {
                list = new List<string>();
                errors[id] = list;
            }

            list.Add(error);
        }

        private static IEnumerable<ModDependency> RequiredDependencies(ModManifest manifest)
        {
            return (manifest.Dependencies ?? new List<ModDependency>()).Where(dependency => !dependency.Optional);
        }

        private static IEnumerable<ModDependency> OptionalDependencies(ModManifest manifest)
        {
            return (manifest.Dependencies ?? new List<ModDependency>())
                .Where(dependency => dependency.Optional)
                .Concat(manifest.OptionalDependencies ?? new List<ModDependency>());
        }

        private static bool DependencySatisfied(string actualVersion, ModDependency dependency)
        {
            if (!string.IsNullOrWhiteSpace(dependency.VersionRange))
            {
                return VersionUtil.AllowsRange(actualVersion, dependency.VersionRange);
            }

            return VersionUtil.IsAtLeast(actualVersion, dependency.Version);
        }

        private static string DependencyRangeText(ModDependency dependency)
        {
            if (!string.IsNullOrWhiteSpace(dependency.VersionRange))
            {
                return dependency.VersionRange;
            }

            return string.IsNullOrWhiteSpace(dependency.Version) ? "*" : ">=" + dependency.Version;
        }

        private static bool ConflictMatches(string actualVersion, ModConflict conflict)
        {
            if (!string.IsNullOrWhiteSpace(conflict.VersionRange))
            {
                return VersionUtil.AllowsRange(actualVersion, conflict.VersionRange);
            }

            if (!string.IsNullOrWhiteSpace(conflict.Version))
            {
                return VersionUtil.AllowsRange(actualVersion, conflict.Version);
            }

            return true;
        }
    }

    public sealed class LoadOrderResult
    {
        public LoadOrderResult(IReadOnlyList<ModPackage> orderedPackages, IReadOnlyDictionary<string, IReadOnlyList<string>> errors)
        {
            OrderedPackages = orderedPackages;
            Errors = errors;
        }

        public IReadOnlyList<ModPackage> OrderedPackages { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Errors { get; }
    }
}
