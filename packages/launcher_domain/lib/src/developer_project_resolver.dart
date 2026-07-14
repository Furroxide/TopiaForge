import 'models.dart';
import 'versioning.dart';

class DeveloperProjectResolution {
  const DeveloperProjectResolution({
    required this.lock,
    required this.issues,
    required this.installActions,
  });

  final DeveloperLock lock;
  final List<LauncherIssue> issues;
  final List<PackageInstallAction> installActions;

  bool get hasBlockingIssues => issues.any((issue) => issue.isBlocking);
}

class DeveloperProjectResolver {
  const DeveloperProjectResolver();

  DeveloperProjectResolution resolve(
    DeveloperProject project,
    List<RegistryMod> availableMods, {
    bool includePrerelease = false,
    DateTime? now,
  }) {
    final issues = <LauncherIssue>[];
    final selected = <String, RegistryMod>{};
    final graph = <String, List<String>>{};
    final actions = <PackageInstallAction>[];

    for (final dependency in project.dependencies) {
      _collect(
        dependency,
        availableMods,
        selected,
        graph,
        issues,
        project.id,
        includePrerelease,
      );
    }

    for (final dependency in project.optionalDependencies) {
      final candidate = _select(dependency, availableMods, includePrerelease);
      if (candidate != null) {
        _collect(
          dependency,
          availableMods,
          selected,
          graph,
          issues,
          project.id,
          includePrerelease,
        );
      }
    }

    _checkConflicts(selected.values, issues);
    final ordered = _ordered(selected, graph, issues);

    for (final mod in ordered) {
      actions.add(
        PackageInstallAction(
          modId: mod.manifest.id,
          name: mod.manifest.name,
          version: mod.manifest.version,
          packageUrl: mod.downloadUrl,
          packageSha256: mod.packageSha256,
          sourceId: mod.sourceId,
          sourceName: mod.sourceName,
        ),
      );
    }

    final lock = DeveloperLock(
      schemaVersion: 1,
      projectId: project.id,
      resolvedAtUtc: (now ?? DateTime.now().toUtc()).toIso8601String(),
      packages: [
        for (final mod in ordered)
          LockedPackage(
            id: mod.manifest.id,
            name: mod.manifest.name,
            version: mod.manifest.version,
            packageUrl: mod.downloadUrl,
            packageSha256: mod.packageSha256,
            sourceId: mod.sourceId,
            sourceName: mod.sourceName,
            dependencies: _graphDependencies(graph, mod.manifest.id),
            apiAssemblies: mod.manifest.apiAssemblies,
          ),
      ],
      dependencyGraph: graph,
    );

    return DeveloperProjectResolution(
      lock: lock,
      issues: issues,
      installActions: actions,
    );
  }

  void _collect(
    ModDependency dependency,
    List<RegistryMod> availableMods,
    Map<String, RegistryMod> selected,
    Map<String, List<String>> graph,
    List<LauncherIssue> issues,
    String rootId,
    bool includePrerelease,
  ) {
    final key = dependency.id.toLowerCase();
    final existing = selected[key];
    if (existing != null) {
      if (!dependency.versionRange.allows(existing.manifest.version)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: rootId,
            message:
                '${dependency.id} is already resolved to ${existing.manifest.version}, which does not satisfy ${dependency.versionRange}.',
          ),
        );
      }
      return;
    }

    final mod = _select(dependency, availableMods, includePrerelease);
    if (mod == null) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: rootId,
          message:
              'No package source satisfies ${dependency.id} ${dependency.versionRange}.',
        ),
      );
      return;
    }

    selected[key] = mod;
    graph[mod.manifest.id] = [
      for (final item in mod.manifest.dependencies) item.id,
    ];
    for (final child in mod.manifest.dependencies) {
      _collect(
        child,
        availableMods,
        selected,
        graph,
        issues,
        mod.manifest.id,
        includePrerelease,
      );
    }
  }

  RegistryMod? _select(
    ModDependency dependency,
    List<RegistryMod> availableMods,
    bool includePrerelease,
  ) {
    final options = availableMods
        .where(
          (mod) =>
              mod.manifest.id.toLowerCase() == dependency.id.toLowerCase() &&
              dependency.versionRange.allows(mod.manifest.version) &&
              (includePrerelease || !_isPrerelease(mod.manifest.version)),
        )
        .toList();
    if (options.isEmpty) {
      return null;
    }
    options.sort(_compareRegistryModsDescending);
    return options.first;
  }

  int _compareRegistryModsDescending(RegistryMod a, RegistryMod b) {
    final aVersion = SemanticVersion.tryParse(a.manifest.version);
    final bVersion = SemanticVersion.tryParse(b.manifest.version);
    if (aVersion != null && bVersion != null) {
      final versionCompare = bVersion.compareTo(aVersion);
      if (versionCompare != 0) {
        return versionCompare;
      }
    }
    final versionTextCompare = b.manifest.version.compareTo(a.manifest.version);
    if (versionTextCompare != 0) {
      return versionTextCompare;
    }
    return a.manifest.id.compareTo(b.manifest.id);
  }

  bool _isPrerelease(String version) =>
      SemanticVersion.tryParse(version)?.isPrerelease ?? false;

  void _checkConflicts(Iterable<RegistryMod> mods, List<LauncherIssue> issues) {
    final byId = {for (final mod in mods) mod.manifest.id.toLowerCase(): mod};
    for (final mod in mods) {
      for (final conflict in mod.manifest.conflicts) {
        final other = byId[conflict.id.toLowerCase()];
        if (other != null &&
            conflict.versionRange.allows(other.manifest.version)) {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.error,
              subjectId: mod.manifest.id,
              message:
                  '${mod.manifest.name} conflicts with ${other.manifest.name}${conflict.reason.isEmpty ? '' : ': ${conflict.reason}'}.',
            ),
          );
        }
      }
    }
  }

  List<RegistryMod> _ordered(
    Map<String, RegistryMod> selected,
    Map<String, List<String>> graph,
    List<LauncherIssue> issues,
  ) {
    final ordered = <RegistryMod>[];
    final temporary = <String>{};
    final permanent = <String>{};
    for (final id in graph.keys.toList()..sort()) {
      _visit(id, selected, graph, temporary, permanent, ordered, issues);
    }
    return ordered;
  }

  void _visit(
    String id,
    Map<String, RegistryMod> selected,
    Map<String, List<String>> graph,
    Set<String> temporary,
    Set<String> permanent,
    List<RegistryMod> ordered,
    List<LauncherIssue> issues,
  ) {
    final key = id.toLowerCase();
    if (permanent.contains(key)) {
      return;
    }
    if (!temporary.add(key)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: id,
          message: 'Dependency cycle detected at $id.',
        ),
      );
      return;
    }
    for (final dependency in _graphDependencies(graph, id)) {
      _visit(
        dependency,
        selected,
        graph,
        temporary,
        permanent,
        ordered,
        issues,
      );
    }
    temporary.remove(key);
    permanent.add(key);
    final mod = selected[key];
    if (mod != null &&
        !ordered.any((item) => item.manifest.id == mod.manifest.id)) {
      ordered.add(mod);
    }
  }

  List<String> _graphDependencies(Map<String, List<String>> graph, String id) {
    final exact = graph[id];
    if (exact != null) {
      return exact;
    }
    final key = id.toLowerCase();
    for (final entry in graph.entries) {
      if (entry.key.toLowerCase() == key) {
        return entry.value;
      }
    }
    return const [];
  }
}
