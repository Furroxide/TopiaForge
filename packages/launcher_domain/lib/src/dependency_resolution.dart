part of 'dependency_planner.dart';

DependencyResolutionResult _resolveInstalled(
  List<InstalledMod> mods, {
  required String? gameVersion,
  required bool requireKnownGameVersion,
  required String loaderVersion,
  required String sdkVersion,
}) {
  final issues = <LauncherIssue>[];
  final candidatesById = <String, List<InstalledMod>>{};
  for (final mod in mods) {
    if (!mod.enabled || mod.uninstallPending) {
      continue;
    }

    final manifest = mod.manifest;
    final validationErrors = <String>{
      ...mod.errors,
      if (manifest == null) 'Installed package manifest is missing.',
      ...?manifest
          ?.validate()
          .where((issue) => issue.isBlocking)
          .map((issue) => issue.message),
    };
    if (validationErrors.isNotEmpty) {
      issues.addAll(
        validationErrors.map(
          (message) => LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: mod.id,
            message: message,
          ),
        ),
      );
      continue;
    }

    final validManifest = manifest!;
    final compatibilityIssues = _runtimeCompatibilityIssues(
      validManifest,
      gameVersion: gameVersion,
      requireKnownGameVersion: requireKnownGameVersion,
      loaderVersion: loaderVersion,
      sdkVersion: sdkVersion,
    );
    if (compatibilityIssues.isNotEmpty) {
      issues.addAll(compatibilityIssues);
      continue;
    }

    candidatesById
        .putIfAbsent(validManifest.id.toLowerCase(), () => <InstalledMod>[])
        .add(mod);
  }

  final enabled = <String, InstalledMod>{};
  final candidateIds = candidatesById.keys.toList()..sort();
  for (final id in candidateIds) {
    final candidates = candidatesById[id]!
      ..sort((left, right) {
        final insensitive = left.packagePath.toLowerCase().compareTo(
          right.packagePath.toLowerCase(),
        );
        return insensitive != 0
            ? insensitive
            : left.packagePath.compareTo(right.packagePath);
      });
    if (candidates.length > 1) {
      final declaredIds = candidates.map((mod) => mod.manifest!.id).toList()
        ..sort();
      final diagnosticId = declaredIds.first;
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: diagnosticId,
          message:
              "Multiple enabled packages declare the same mod id '$diagnosticId': "
              '${candidates.map((mod) => mod.packagePath).join(', ')}.',
        ),
      );
      continue;
    }
    enabled[id] = candidates.single;
  }

  final graph = <String, List<String>>{
    for (final mod in enabled.values) mod.id: <String>[],
  };
  final softEdges = <({int priority, String from, String to})>[];

  for (final mod in enabled.values) {
    final manifest = mod.manifest!;
    for (final dependency in manifest.dependencies.where(
      (dependency) => !dependency.optional,
    )) {
      final dependencyMod = enabled[dependency.id.toLowerCase()];
      if (dependencyMod == null) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: mod.id,
            message: '${manifest.name} is missing dependency ${dependency.id}.',
          ),
        );
        continue;
      }

      if (!dependency.versionRange.allows(dependencyMod.version)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: mod.id,
            message:
                '${manifest.name} requires ${dependency.id} ${dependency.versionRange}, '
                'but ${dependencyMod.version} is installed.',
          ),
        );
      } else {
        _addResolutionEdge(graph[mod.id]!, dependencyMod.id);
      }
    }

    for (final dependency in [
      ...manifest.dependencies.where((dependency) => dependency.optional),
      ...manifest.optionalDependencies,
    ]) {
      final dependencyMod = enabled[dependency.id.toLowerCase()];
      if (dependencyMod != null &&
          dependency.versionRange.allows(dependencyMod.version)) {
        softEdges.add((priority: 0, from: mod.id, to: dependencyMod.id));
      }
    }

    for (final after in manifest.loadAfter) {
      final afterMod = enabled[after.toLowerCase()];
      if (afterMod != null) {
        softEdges.add((priority: 1, from: mod.id, to: afterMod.id));
      }
    }

    for (final conflict in manifest.conflicts) {
      final conflictingMod = enabled[conflict.id.toLowerCase()];
      if (conflictingMod != null &&
          conflict.versionRange.allows(conflictingMod.version)) {
        _addConflictIssue(
          issues,
          subject: mod,
          conflicting: conflictingMod,
          reason: conflict.reason,
        );
        _addConflictIssue(
          issues,
          subject: conflictingMod,
          conflicting: mod,
          reason: conflict.reason,
        );
      }
    }
  }

  // Only hard dependency cycles are blocking. Optional dependencies and
  // loadAfter are best-effort ordering hints; folding them into cycle
  // detection would turn a mutual hint into a load failure.
  final hardOrdered = <InstalledMod>[];
  final permanent = <String>{};
  final visiting = <String>[];
  final cycleBlocked = <String>{};
  for (final id in graph.keys.toList()..sort()) {
    _visitResolutionNode(
      id,
      graph,
      enabled,
      visiting,
      permanent,
      cycleBlocked,
      hardOrdered,
      issues,
    );
  }

  final blockedIds = <String>{
    ...cycleBlocked,
    ...issues
        .where((issue) => issue.isBlocking && issue.subjectId != null)
        .map((issue) => issue.subjectId!.toLowerCase()),
  };
  var changed = true;
  while (changed) {
    changed = false;
    for (final mod in enabled.values) {
      final key = mod.id.toLowerCase();
      if (blockedIds.contains(key)) {
        continue;
      }
      final dependsOnBlocked = mod.manifest!.dependencies
          .where((dependency) => !dependency.optional)
          .any((dependency) {
            final dependencyMod = enabled[dependency.id.toLowerCase()];
            return dependencyMod != null &&
                blockedIds.contains(dependencyMod.id.toLowerCase());
          });
      if (dependsOnBlocked) {
        blockedIds.add(key);
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: mod.id,
            message: '${mod.name} depends on a blocked mod.',
          ),
        );
        changed = true;
      }
    }
  }

  softEdges.sort((left, right) {
    final priority = left.priority.compareTo(right.priority);
    if (priority != 0) return priority;
    final owner = _compareResolutionIds(left.from, right.from);
    return owner != 0 ? owner : _compareResolutionIds(left.to, right.to);
  });
  for (final edge in softEdges) {
    if (blockedIds.contains(edge.from.toLowerCase()) ||
        blockedIds.contains(edge.to.toLowerCase()) ||
        graph[edge.from]!.contains(edge.to) ||
        _wouldCreateResolutionCycle(graph, edge.from, edge.to)) {
      continue;
    }
    graph[edge.from]!.add(edge.to);
  }

  final ordered = <InstalledMod>[];
  final orderedIds = <String>{};
  for (final id in graph.keys.toList()..sort(_compareResolutionIds)) {
    _visitResolutionOrder(id, graph, enabled, blockedIds, orderedIds, ordered);
  }
  return DependencyResolutionResult(
    orderedMods: ordered,
    issues: issues,
    graph: graph,
  );
}

bool _wouldCreateResolutionCycle(
  Map<String, List<String>> graph,
  String from,
  String to,
) {
  if (from.toLowerCase() == to.toLowerCase()) {
    return true;
  }

  bool reaches(String current, Set<String> visited) {
    final key = current.toLowerCase();
    if (!visited.add(key)) return false;
    for (final dependency in graph[current] ?? const <String>[]) {
      if (dependency.toLowerCase() == from.toLowerCase() ||
          reaches(dependency, visited)) {
        return true;
      }
    }
    return false;
  }

  return reaches(to, <String>{});
}

void _visitResolutionOrder(
  String id,
  Map<String, List<String>> graph,
  Map<String, InstalledMod> mods,
  Set<String> blockedIds,
  Set<String> permanent,
  List<InstalledMod> ordered,
) {
  final key = id.toLowerCase();
  if (blockedIds.contains(key) || !permanent.add(key)) return;
  final dependencies = [...graph[id] ?? const <String>[]]
    ..sort(_compareResolutionIds);
  for (final dependency in dependencies) {
    _visitResolutionOrder(
      dependency,
      graph,
      mods,
      blockedIds,
      permanent,
      ordered,
    );
  }
  final mod = mods[key];
  if (mod != null) ordered.add(mod);
}

int _compareResolutionIds(String left, String right) {
  final insensitive = left.toLowerCase().compareTo(right.toLowerCase());
  return insensitive != 0 ? insensitive : left.compareTo(right);
}

void _addConflictIssue(
  List<LauncherIssue> issues, {
  required InstalledMod subject,
  required InstalledMod conflicting,
  required String reason,
}) {
  final message =
      '${subject.name} conflicts with ${conflicting.name}'
      "${reason.isEmpty ? '' : ': $reason'}";
  if (issues.any(
    (issue) =>
        issue.subjectId?.toLowerCase() == subject.id.toLowerCase() &&
        issue.message == message,
  )) {
    return;
  }
  issues.add(
    LauncherIssue(
      severity: IssueSeverity.error,
      subjectId: subject.id,
      message: message,
    ),
  );
}

void _addResolutionEdge(List<String> edges, String id) {
  if (!edges.contains(id)) {
    edges.add(id);
  }
}

void _visitResolutionNode(
  String id,
  Map<String, List<String>> graph,
  Map<String, InstalledMod> mods,
  List<String> visiting,
  Set<String> permanent,
  Set<String> cycleBlocked,
  List<InstalledMod> ordered,
  List<LauncherIssue> issues,
) {
  final key = id.toLowerCase();
  if (permanent.contains(key)) {
    return;
  }
  final cycleStart = visiting.indexOf(key);
  if (cycleStart >= 0) {
    final cycle = [...visiting.sublist(cycleStart), key];
    cycleBlocked.addAll(cycle);
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: id,
        message: 'Dependency cycle detected: ${cycle.join(' -> ')}.',
      ),
    );
    return;
  }

  visiting.add(key);
  for (final dependency in graph[id] ?? const <String>[]) {
    _visitResolutionNode(
      dependency,
      graph,
      mods,
      visiting,
      permanent,
      cycleBlocked,
      ordered,
      issues,
    );
  }

  visiting.removeLast();
  permanent.add(key);
  final mod = mods[key];
  if (mod != null && !ordered.any((item) => item.id == mod.id)) {
    ordered.add(mod);
  }
}
