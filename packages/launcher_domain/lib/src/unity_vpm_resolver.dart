import 'models.dart';
import 'versioning.dart';

/// Resolves a Unity project's VPM dependencies against one or more repository listings — the launcher-side twin
/// of VRChat's resolver. Pure logic (no IO): reuses [SemanticVersion]/[VersionRange] plus VPM caret/tilde range
/// handling, walks transitive `vpmDependencies`, and returns the packages in dependency order (deps first).
class UnityVpmResolver {
  const UnityVpmResolver();

  UnityVpmResolution resolve({
    required VpmManifest manifest,
    required VpmListing catalog,
  }) {
    final resolved = <String, VpmResolvedPackage>{};
    final graph = <String, List<String>>{};
    final issues = <LauncherIssue>[];
    // Every range constraint seen for a package id, accumulated so the chosen version satisfies ALL of them
    // (not just the latest) — otherwise a later dependent could bump a shared dep to a version that violates an
    // earlier dependent's range.
    final ranges = <String, List<String>>{};
    final conflicted = <String>{};

    final queue = <MapEntry<String, String>>[...manifest.dependencies.entries];
    while (queue.isNotEmpty) {
      final entry = queue.removeAt(0);
      final id = entry.key;
      final range = entry.value;

      final versions = catalog.packages[id];
      if (versions == null || versions.isEmpty) {
        _addIssue(
          issues,
          'Unity package "$id" was not found in any package listing.',
        );
        continue;
      }

      final idRanges = ranges.putIfAbsent(id, () => <String>[]);
      if (!idRanges.contains(range)) {
        idRanges.add(range);
      }

      final best = _bestSatisfyingAll(versions, idRanges);
      if (best == null) {
        if (conflicted.add(id)) {
          _addIssue(
            issues,
            'No version of "$id" satisfies all constraints: ${idRanges.join(', ')}.',
          );
        }
        resolved.remove(id);
        graph.remove(id);
        continue;
      }

      final existing = resolved[id];
      if (existing != null && existing.version == best.version) {
        continue; // already at the version satisfying every accumulated constraint
      }

      resolved[id] = VpmResolvedPackage(
        id: id,
        version: best.version,
        url: best.url,
        zipSha256: best.zipSha256,
        displayName: best.displayName,
        dependencies: best.vpmDependencies.keys.toList(),
      );
      graph[id] = best.vpmDependencies.keys.toList();
      // The chosen version may have different deps than a previously-chosen one — re-walk them.
      best.vpmDependencies.forEach((depId, depRange) {
        queue.add(MapEntry(depId, depRange));
      });
    }

    return UnityVpmResolution(
      packages: _topoSort(resolved, graph, issues),
      issues: issues,
    );
  }

  // Highest version satisfying EVERY accumulated range (the constraint intersection).
  VpmPackageInfo? _bestSatisfyingAll(
    Map<String, VpmPackageInfo> versions,
    List<String> ranges,
  ) {
    VpmPackageInfo? best;
    for (final info in versions.values) {
      if (SemanticVersion.tryParse(info.version) == null) {
        continue;
      }
      if (!ranges.every((range) => vpmRangeAllows(range, info.version))) {
        continue;
      }
      if (best == null || _compare(info.version, best.version) > 0) {
        best = info;
      }
    }
    return best;
  }

  // Deterministic dependency-first order (Kahn-ish via DFS); records a cycle issue but still returns all nodes.
  List<VpmResolvedPackage> _topoSort(
    Map<String, VpmResolvedPackage> resolved,
    Map<String, List<String>> graph,
    List<LauncherIssue> issues,
  ) {
    final ordered = <VpmResolvedPackage>[];
    final visited = <String>{};
    final inStack = <String>{};

    void visit(String id) {
      if (visited.contains(id)) {
        return;
      }
      if (inStack.contains(id)) {
        _addIssue(issues, 'Dependency cycle detected involving "$id".');
        return;
      }
      inStack.add(id);
      for (final dep in graph[id] ?? const <String>[]) {
        if (resolved.containsKey(dep)) {
          visit(dep);
        }
      }
      inStack.remove(id);
      visited.add(id);
      final package = resolved[id];
      if (package != null) {
        ordered.add(package);
      }
    }

    for (final id in resolved.keys) {
      visit(id);
    }
    return ordered;
  }

  static int _compare(String a, String b) {
    final va = SemanticVersion.tryParse(a);
    final vb = SemanticVersion.tryParse(b);
    if (va == null || vb == null) {
      return a.compareTo(b);
    }
    return va.compareTo(vb);
  }

  static void _addIssue(List<LauncherIssue> issues, String message) {
    issues.add(LauncherIssue(severity: IssueSeverity.error, message: message));
  }
}

/// Whether [version] satisfies a VPM range. Adds caret (`^`) and tilde (`~`) on top of the comparators/wildcards
/// [VersionRange] already understands (`>=`, `1.2.*`, exact, `*`).
bool vpmRangeAllows(String range, String version) {
  final value = SemanticVersion.tryParse(version);
  if (value == null) {
    return false;
  }
  final text = range.trim();
  if (text.startsWith('^') || text.startsWith('~')) {
    final base = SemanticVersion.tryParse(text.substring(1));
    if (base == null) {
      return false;
    }
    if (value.compareTo(base) < 0) {
      return false;
    }
    final max = text.startsWith('^') ? _caretMax(base) : _tildeMax(base);
    return value.compareTo(max) < 0;
  }
  return VersionRange.parse(text).allows(version);
}

SemanticVersion _caretMax(SemanticVersion base) {
  if (base.majorNumber.isPositive) {
    return base.incrementMajor();
  }
  if (base.minorNumber.isPositive) {
    return base.incrementMinor();
  }
  return base.incrementPatch();
}

SemanticVersion _tildeMax(SemanticVersion base) => base.incrementMinor();
