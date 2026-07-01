import 'models.dart';
import 'versioning.dart';

class DependencyResolutionResult {
  const DependencyResolutionResult({
    required this.orderedMods,
    required this.issues,
    required this.graph,
  });

  final List<InstalledMod> orderedMods;
  final List<LauncherIssue> issues;
  final Map<String, List<String>> graph;

  bool get hasBlockingIssues => issues.any((issue) => issue.isBlocking);
}

class PackageInstallPlan {
  const PackageInstallPlan({
    required this.manifest,
    required this.issues,
    required this.dependenciesToInstall,
    required this.optionalDependenciesMissing,
    required this.conflictingMods,
    required this.packageSha256,
    this.installActions = const [],
  });

  final ModManifest manifest;
  final List<LauncherIssue> issues;
  final List<ModDependency> dependenciesToInstall;
  final List<ModDependency> optionalDependenciesMissing;
  final List<InstalledMod> conflictingMods;
  final String packageSha256;
  final List<PackageInstallAction> installActions;

  bool get hasBlockingIssues => issues.any((issue) => issue.isBlocking);
}

class DependencyPlanner {
  const DependencyPlanner();

  DependencyResolutionResult resolveInstalled(List<InstalledMod> mods) {
    final enabled = <String, InstalledMod>{};
    for (final mod in mods) {
      if (mod.enabled && !mod.uninstallPending && mod.manifest != null) {
        enabled[mod.id.toLowerCase()] = mod;
      }
    }

    final issues = <LauncherIssue>[];
    final graph = <String, List<String>>{
      for (final mod in enabled.values) mod.id: <String>[],
    };

    for (final mod in enabled.values) {
      final manifest = mod.manifest!;
      for (final dependency in manifest.dependencies) {
        final dependencyMod = enabled[dependency.id.toLowerCase()];
        if (dependencyMod == null) {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.error,
              subjectId: mod.id,
              message:
                  '${manifest.name} is missing dependency ${dependency.id}.',
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
                  '${manifest.name} requires ${dependency.id} ${dependency.versionRange}, but ${dependencyMod.version} is installed.',
            ),
          );
        }

        _addEdge(graph[mod.id]!, dependencyMod.id);
      }

      // loadAfter edges are soft ordering hints and commonly mirror the hard dependencies (a mod
      // both depends on X and loads after X). De-duplicate so the graph lists each id once.
      for (final after in manifest.loadAfter) {
        final afterMod = enabled[after.toLowerCase()];
        if (afterMod != null) {
          _addEdge(graph[mod.id]!, afterMod.id);
        }
      }

      for (final conflict in manifest.conflicts) {
        final conflictingMod = enabled[conflict.id.toLowerCase()];
        if (conflictingMod != null &&
            conflict.versionRange.allows(conflictingMod.version)) {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.error,
              subjectId: mod.id,
              message:
                  '${manifest.name} conflicts with ${conflictingMod.name}${conflict.reason.isEmpty ? '' : ': ${conflict.reason}'}',
            ),
          );
        }
      }
    }

    final ordered = <InstalledMod>[];
    final temporary = <String>{};
    final permanent = <String>{};

    for (final id in graph.keys.toList()..sort()) {
      _visit(id, graph, enabled, temporary, permanent, ordered, issues);
    }

    final blockedIds = issues
        .where((issue) => issue.isBlocking && issue.subjectId != null)
        .map((issue) => issue.subjectId!.toLowerCase())
        .toSet();
    ordered.removeWhere((mod) => blockedIds.contains(mod.id.toLowerCase()));

    return DependencyResolutionResult(
      orderedMods: ordered,
      issues: issues,
      graph: graph,
    );
  }

  PackageInstallPlan previewInstall(
    ModManifest candidate,
    List<InstalledMod> installedMods, {
    String packageSha256 = '',
    String packageUrl = '',
    String sourceId = '',
    String sourceName = '',
    List<RegistryMod> availableMods = const [],
    String? gameVersion,
    String? loaderVersion,
  }) {
    final issues = [...candidate.validate()];
    final installed = {
      for (final mod in installedMods) mod.id.toLowerCase(): mod,
    };
    final dependenciesToInstall = <ModDependency>[];
    final optionalMissing = <ModDependency>[];
    final conflictingMods = <InstalledMod>[];

    if (gameVersion != null &&
        !candidate.gameVersionRange.isAny &&
        !candidate.gameVersionRange.allows(gameVersion)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: candidate.id,
          message:
              '${candidate.name} supports game ${candidate.gameVersionRange}, not $gameVersion.',
        ),
      );
    }

    if (loaderVersion != null &&
        !candidate.loaderVersionRange.isAny &&
        !candidate.loaderVersionRange.allows(loaderVersion)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: candidate.id,
          message:
              '${candidate.name} supports loader ${candidate.loaderVersionRange}, not $loaderVersion.',
        ),
      );
    }

    final installActions = <PackageInstallAction>[];
    final planned = <String, RegistryMod>{};

    for (final dependency in candidate.dependencies) {
      final installedDependency = installed[dependency.id.toLowerCase()];
      if (installedDependency == null) {
        final selected = _selectDependency(
          dependency,
          installed,
          availableMods,
        );
        if (selected == null) {
          dependenciesToInstall.add(dependency);
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.error,
              subjectId: candidate.id,
              message: 'Required dependency ${dependency.id} is not installed.',
            ),
          );
        } else {
          _collectInstallActions(
            selected,
            installed,
            availableMods,
            planned,
            installActions,
            issues,
            candidate.id,
          );
        }
      } else if (!dependency.versionRange.allows(installedDependency.version)) {
        final selected = _selectDependency(
          dependency,
          installed,
          availableMods,
        );
        if (selected == null) {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.error,
              subjectId: candidate.id,
              message:
                  'Dependency ${dependency.id} must satisfy ${dependency.versionRange}, but ${installedDependency.version} is installed.',
            ),
          );
        } else {
          _collectInstallActions(
            selected,
            installed,
            availableMods,
            planned,
            installActions,
            issues,
            candidate.id,
          );
        }
      }
    }

    installActions.add(
      PackageInstallAction(
        modId: candidate.id,
        name: candidate.name,
        version: candidate.version,
        packageUrl: packageUrl,
        packageSha256: packageSha256,
        sourceId: sourceId,
        sourceName: sourceName,
        root: true,
      ),
    );

    for (final action in installActions) {
      if (action.isRemote && action.packageSha256.trim().isEmpty) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: action.modId,
            message:
                '${action.name} is remote and must include a SHA-256 hash before install.',
          ),
        );
      }
    }

    for (final dependency in candidate.optionalDependencies) {
      final installedDependency = installed[dependency.id.toLowerCase()];
      if (installedDependency == null) {
        optionalMissing.add(dependency);
      } else if (!dependency.versionRange.allows(installedDependency.version)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.warning,
            subjectId: candidate.id,
            message:
                'Optional dependency ${dependency.id} does not satisfy ${dependency.versionRange}.',
          ),
        );
      }
    }

    for (final conflict in candidate.conflicts) {
      final installedConflict = installed[conflict.id.toLowerCase()];
      if (installedConflict != null &&
          conflict.versionRange.allows(installedConflict.version)) {
        conflictingMods.add(installedConflict);
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: candidate.id,
            message:
                'Conflicts with ${installedConflict.name}${conflict.reason.isEmpty ? '' : ': ${conflict.reason}'}.',
          ),
        );
      }
    }

    final existing = installed[candidate.id.toLowerCase()];
    if (existing != null) {
      final installedVersion = SemanticVersion.tryParse(existing.version);
      final candidateVersion = SemanticVersion.tryParse(candidate.version);
      if (installedVersion != null && candidateVersion != null) {
        final relation = candidateVersion.compareTo(installedVersion);
        if (relation < 0) {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.warning,
              subjectId: candidate.id,
              message:
                  'This will roll back ${candidate.name} from ${existing.version} to ${candidate.version}.',
            ),
          );
        } else if (relation == 0) {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.info,
              subjectId: candidate.id,
              message:
                  '${candidate.name} ${candidate.version} is already installed.',
            ),
          );
        }
      }
    }

    return PackageInstallPlan(
      manifest: candidate,
      issues: issues,
      dependenciesToInstall: dependenciesToInstall,
      optionalDependenciesMissing: optionalMissing,
      conflictingMods: conflictingMods,
      packageSha256: packageSha256,
      installActions: installActions,
    );
  }

  RegistryMod? _selectDependency(
    ModDependency dependency,
    Map<String, InstalledMod> installed,
    List<RegistryMod> availableMods,
  ) {
    final options = availableMods
        .where(
          (mod) =>
              mod.manifest.id.toLowerCase() == dependency.id.toLowerCase() &&
              dependency.versionRange.allows(mod.manifest.version),
        )
        .toList();
    if (options.isEmpty) {
      return null;
    }

    options.sort((a, b) {
      final aVersion = SemanticVersion.tryParse(a.manifest.version);
      final bVersion = SemanticVersion.tryParse(b.manifest.version);
      if (aVersion == null || bVersion == null) {
        return b.manifest.version.compareTo(a.manifest.version);
      }
      return bVersion.compareTo(aVersion);
    });
    return options.first;
  }

  void _collectInstallActions(
    RegistryMod mod,
    Map<String, InstalledMod> installed,
    List<RegistryMod> availableMods,
    Map<String, RegistryMod> planned,
    List<PackageInstallAction> actions,
    List<LauncherIssue> issues,
    String rootId,
  ) {
    final key = mod.manifest.id.toLowerCase();
    final installedMod = installed[key];
    if (installedMod != null &&
        mod.manifest.version == installedMod.version &&
        installedMod.enabled) {
      return;
    }
    if (planned.containsKey(key)) {
      return;
    }

    planned[key] = mod;
    for (final dependency in mod.manifest.dependencies) {
      final installedDependency = installed[dependency.id.toLowerCase()];
      if (installedDependency != null &&
          dependency.versionRange.allows(installedDependency.version)) {
        continue;
      }
      final selected = _selectDependency(dependency, installed, availableMods);
      if (selected == null) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: rootId,
            message:
                '${mod.manifest.name} requires ${dependency.id} ${dependency.versionRange}, but no source package satisfies it.',
          ),
        );
        continue;
      }
      _collectInstallActions(
        selected,
        installed,
        availableMods,
        planned,
        actions,
        issues,
        rootId,
      );
    }

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

  // Appends an edge unless it is already present, so a mod listed in both `dependencies` and
  // `loadAfter` produces a single graph edge (and the diagnostics view shows it once).
  void _addEdge(List<String> edges, String id) {
    if (!edges.contains(id)) {
      edges.add(id);
    }
  }

  void _visit(
    String id,
    Map<String, List<String>> graph,
    Map<String, InstalledMod> mods,
    Set<String> temporary,
    Set<String> permanent,
    List<InstalledMod> ordered,
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
          message: 'Dependency/loadAfter cycle detected at $id.',
        ),
      );
      return;
    }

    for (final dependency in graph[id] ?? const <String>[]) {
      _visit(dependency, graph, mods, temporary, permanent, ordered, issues);
    }

    temporary.remove(key);
    permanent.add(key);
    final mod = mods[key];
    if (mod != null && !ordered.any((item) => item.id == mod.id)) {
      ordered.add(mod);
    }
  }
}
