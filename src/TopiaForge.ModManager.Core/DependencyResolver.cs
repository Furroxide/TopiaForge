using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public sealed class DependencyResolver
    {
        public LoadOrderResult Resolve(IEnumerable<ModPackage> packages)
        {
            var candidates = packages
                .Where(p => p.IsValid && p.IsEnabled)
                .ToList();

            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var enabled = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in candidates
                .GroupBy(package => package.Manifest!.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var duplicates = group.OrderBy(package => package.PackagePath, StringComparer.OrdinalIgnoreCase).ToList();
                if (duplicates.Count > 1)
                {
                    var diagnosticId = duplicates
                        .Select(package => package.Manifest!.Id)
                        .OrderBy(id => id, StringComparer.Ordinal)
                        .First();
                    AddError(
                        errors,
                        diagnosticId,
                        "Multiple enabled packages declare the same mod id '" + diagnosticId + "': "
                            + string.Join(", ", duplicates.Select(package => package.PackagePath)) + ".");
                    continue;
                }

                enabled.Add(group.Key, duplicates[0]);
            }

            foreach (var package in enabled.Values)
            {
                var manifest = package.Manifest!;
                foreach (var dependency in GetRequiredDependencies(manifest))
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

            // Only hard required dependencies participate in validity/cycle diagnostics. Optional
            // dependencies and loadAfter are ordering hints; a contradictory hint must never stop a mod.
            var hardGraph = enabled.Keys.ToDictionary(
                key => key,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            foreach (var package in enabled.Values)
            {
                var id = package.Manifest!.Id;
                foreach (var dependency in GetRequiredDependencies(package.Manifest))
                {
                    if (enabled.TryGetValue(dependency.Id, out var dependencyPackage) &&
                        DependencySatisfied(dependencyPackage.Manifest!.Version, dependency))
                    {
                        hardGraph[id].Add(dependency.Id);
                    }
                }
            }

            // Detect every member of every required-dependency cycle before ordering. The old recursive sorter
            // marked only the repeated id (A in A -> B -> A), allowing B and its dependents to load half-alive.
            var visitState = hardGraph.Keys.ToDictionary(
                id => id,
                _ => 0,
                StringComparer.OrdinalIgnoreCase);
            var stack = new List<string>();
            var stackIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in hardGraph.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                DetectCycles(id, hardGraph, visitState, stack, stackIndexes, errors);
            }

            // A manifest-blocked hard dependency is just as unavailable as a missing one. Propagate that failure
            // to required dependents to a fixed point; optional dependencies and loadAfter hints stay non-blocking.
            bool changed;
            do
            {
                changed = false;
                foreach (var package in enabled.Values)
                {
                    var manifest = package.Manifest!;
                    foreach (var dependency in GetRequiredDependencies(manifest))
                    {
                        if (enabled.TryGetValue(dependency.Id, out var dependencyPackage)
                            && errors.ContainsKey(dependencyPackage.Manifest!.Id))
                        {
                            changed |= AddError(
                                errors,
                                manifest.Id,
                                "Required dependency cannot load: " + dependency.Id + ".");
                        }
                    }
                }
            }
            while (changed);

            // Start with the acyclic hard edges between loadable packages, then apply soft edges in a stable
            // order. Optional dependency hints win over loadAfter hints; within each class, lexical owner/target
            // order determines which edge survives a contradictory pair. An edge is simply skipped if it would
            // close a cycle, preserving the documented non-blocking semantics.
            var graph = enabled.Keys
                .Where(id => !errors.ContainsKey(id))
                .ToDictionary(
                    id => id,
                    id => new HashSet<string>(
                        hardGraph[id].Where(dependency => !errors.ContainsKey(dependency)),
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            var softEdges = new List<(int Priority, string Owner, string Dependency)>();
            foreach (var package in enabled.Values.OrderBy(
                package => package.Manifest!.Id,
                StringComparer.OrdinalIgnoreCase))
            {
                var manifest = package.Manifest!;
                if (!graph.ContainsKey(manifest.Id))
                {
                    continue;
                }

                foreach (var dependency in OptionalDependencies(manifest))
                {
                    if (graph.ContainsKey(dependency.Id) &&
                        enabled.TryGetValue(dependency.Id, out var dependencyPackage) &&
                        DependencySatisfied(dependencyPackage.Manifest!.Version, dependency))
                    {
                        softEdges.Add((0, manifest.Id, dependency.Id));
                    }
                }

                foreach (var after in manifest.LoadAfter ?? new List<string>())
                {
                    if (graph.ContainsKey(after))
                    {
                        softEdges.Add((1, manifest.Id, after));
                    }
                }
            }

            foreach (var edge in softEdges
                .OrderBy(edge => edge.Priority)
                .ThenBy(edge => edge.Owner, StringComparer.OrdinalIgnoreCase)
                .ThenBy(edge => edge.Dependency, StringComparer.OrdinalIgnoreCase))
            {
                if (!graph[edge.Owner].Contains(edge.Dependency) &&
                    !WouldCreateCycle(graph, edge.Owner, edge.Dependency))
                {
                    graph[edge.Owner].Add(edge.Dependency);
                }
            }

            var ordered = new List<ModPackage>();
            var permanent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var id in graph.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                VisitLoadable(id, graph, enabled, permanent, ordered);
            }

            return new LoadOrderResult(ordered, errors.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase));
        }

        private static void DetectCycles(
            string id,
            Dictionary<string, HashSet<string>> graph,
            Dictionary<string, int> visitState,
            List<string> stack,
            Dictionary<string, int> stackIndexes,
            Dictionary<string, List<string>> errors)
        {
            if (visitState[id] != 0)
            {
                return;
            }

            visitState[id] = 1;
            stackIndexes[id] = stack.Count;
            stack.Add(id);
            foreach (var dependency in graph[id].OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                if (visitState[dependency] == 0)
                {
                    DetectCycles(dependency, graph, visitState, stack, stackIndexes, errors);
                }
                else if (visitState[dependency] == 1 && stackIndexes.TryGetValue(dependency, out var cycleStart))
                {
                    var cycle = stack.Skip(cycleStart).Concat(new[] { dependency }).ToArray();
                    var message = "Required dependency cycle detected: " + string.Join(" -> ", cycle) + ".";
                    for (var index = cycleStart; index < stack.Count; index++)
                    {
                        AddError(errors, stack[index], message);
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            stackIndexes.Remove(id);
            visitState[id] = 2;
        }

        private static void VisitLoadable(
            string id,
            Dictionary<string, HashSet<string>> graph,
            Dictionary<string, ModPackage> packages,
            HashSet<string> permanent,
            List<ModPackage> ordered)
        {
            if (!permanent.Add(id))
            {
                return;
            }

            foreach (var dependency in graph[id].OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                VisitLoadable(dependency, graph, packages, permanent, ordered);
            }

            ordered.Add(packages[id]);
        }

        private static bool WouldCreateCycle(
            Dictionary<string, HashSet<string>> graph,
            string owner,
            string dependency)
        {
            if (string.Equals(owner, dependency, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var pending = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(dependency);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (string.Equals(current, owner, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                foreach (var next in graph[current])
                {
                    pending.Push(next);
                }
            }

            return false;
        }

        private static bool AddError(Dictionary<string, List<string>> errors, string id, string error)
        {
            if (!errors.TryGetValue(id, out var list))
            {
                list = new List<string>();
                errors[id] = list;
            }

            if (list.Contains(error, StringComparer.Ordinal))
            {
                return false;
            }

            list.Add(error);
            return true;
        }

        /// <summary>All hard dependencies of a manifest: vpmDependencies plus non-optional dependencies.</summary>
        public static IEnumerable<ModDependency> GetRequiredDependencies(ModManifest manifest)
        {
            return VpmDependencies(manifest)
                .Concat((manifest.Dependencies ?? new List<ModDependency>()).Where(dependency => !dependency.Optional));
        }

        /// <summary>
        /// The id of the first required dependency that appears in <paramref name="failedModIds"/>, or null.
        /// Used by the runtime to skip a mod whose dependency passed manifest validation but then failed to
        /// actually load (e.g. a TypeLoadException from a binary-stale package).
        /// </summary>
        public static string? FindFailedRequiredDependency(ModManifest manifest, ICollection<string> failedModIds)
        {
            foreach (var dependency in GetRequiredDependencies(manifest))
            {
                if (failedModIds.Any(failedId =>
                        string.Equals(failedId, dependency.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    return dependency.Id;
                }
            }

            return null;
        }

        private static IEnumerable<ModDependency> OptionalDependencies(ModManifest manifest)
        {
            return (manifest.Dependencies ?? new List<ModDependency>())
                .Where(dependency => dependency.Optional)
                .Concat(manifest.OptionalDependencies ?? new List<ModDependency>());
        }

        private static IEnumerable<ModDependency> VpmDependencies(ModManifest manifest)
        {
            foreach (var entry in manifest.VpmDependencies ?? new Dictionary<string, string>())
            {
                yield return new ModDependency
                {
                    Id = entry.Key,
                    VersionRange = entry.Value
                };
            }
        }

        private static bool DependencySatisfied(string actualVersion, ModDependency dependency)
        {
            return VersionUtil.AllowsRange(actualVersion, dependency.VersionRange);
        }

        private static string DependencyRangeText(ModDependency dependency)
        {
            return string.IsNullOrWhiteSpace(dependency.VersionRange) ? "*" : dependency.VersionRange;
        }

        private static bool ConflictMatches(string actualVersion, ModConflict conflict)
        {
            return string.IsNullOrWhiteSpace(conflict.VersionRange)
                || VersionUtil.AllowsRange(actualVersion, conflict.VersionRange);
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
