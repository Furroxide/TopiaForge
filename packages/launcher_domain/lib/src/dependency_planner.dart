import 'models.dart';
import 'versioning.dart';

part 'dependency_resolution.dart';
part 'dependency_install_resolution.dart';

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
    this.requiredPermissions = const [],
  });

  final ModManifest manifest;
  final List<LauncherIssue> issues;
  final List<ModDependency> dependenciesToInstall;
  final List<ModDependency> optionalDependenciesMissing;
  final List<InstalledMod> conflictingMods;
  final String packageSha256;
  final List<PackageInstallAction> installActions;
  final List<String> requiredPermissions;

  bool get hasBlockingIssues => issues.any((issue) => issue.isBlocking);
}

class DependencyPlanner {
  const DependencyPlanner();

  DependencyResolutionResult resolveInstalled(
    List<InstalledMod> mods, {
    String? gameVersion,
    bool requireKnownGameVersion = false,
    String loaderVersion = TopiaForgeRuntimeVersions.loaderVersion,
    String sdkVersion = TopiaForgeRuntimeVersions.sdkVersion,
  }) => _resolveInstalled(
    mods,
    gameVersion: gameVersion,
    requireKnownGameVersion: requireKnownGameVersion,
    loaderVersion: loaderVersion,
    sdkVersion: sdkVersion,
  );

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
    String? sdkVersion,
    bool requireKnownGameVersion = false,
  }) {
    final issues = [...candidate.validate()];
    final installed = {
      for (final mod in installedMods) mod.id.toLowerCase(): mod,
    };
    final dependenciesToInstall = <ModDependency>[];
    final optionalMissing = <ModDependency>[];
    final conflictingMods = <InstalledMod>[];

    issues.addAll(
      _runtimeCompatibilityIssues(
        candidate,
        gameVersion: gameVersion,
        requireKnownGameVersion: requireKnownGameVersion,
        loaderVersion: loaderVersion,
        sdkVersion: sdkVersion,
      ),
    );

    final dependencyPlan = _resolveInstallDependencies(
      candidate,
      installed,
      availableMods,
      gameVersion: gameVersion,
      requireKnownGameVersion: requireKnownGameVersion,
      loaderVersion: loaderVersion,
      sdkVersion: sdkVersion,
    );
    issues.addAll(dependencyPlan.issues);
    final installActions = [...dependencyPlan.actions];
    for (final dependency in candidate.dependencies) {
      final key = dependency.id.toLowerCase();
      if (dependencyPlan.unresolvedIds.contains(key) &&
          installed[key] == null) {
        dependenciesToInstall.add(dependency);
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
      if (action.isRemote && !_isSha256(action.packageSha256)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: action.modId,
            message:
                '${action.name} is remote and must include a valid 64-digit SHA-256 hash before install.',
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

    _appendProspectiveConflicts(
      candidate,
      dependencyPlan.selectedManifests,
      installedMods,
      issues,
      conflictingMods,
      gameVersion: gameVersion,
      requireKnownGameVersion: requireKnownGameVersion,
      loaderVersion: loaderVersion,
      sdkVersion: sdkVersion,
    );

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
      requiredPermissions: List.unmodifiable(
        {
          ...candidate.permissions,
          for (final manifest in dependencyPlan.selectedManifests.values)
            ...manifest.permissions,
        }.toList()..sort(),
      ),
    );
  }
}

void _appendProspectiveConflicts(
  ModManifest root,
  Map<String, ModManifest> dependencies,
  List<InstalledMod> installedMods,
  List<LauncherIssue> issues,
  List<InstalledMod> conflictingMods, {
  required String? gameVersion,
  required bool requireKnownGameVersion,
  required String? loaderVersion,
  required String? sdkVersion,
}) {
  final planned = <String, ModManifest>{
    ...dependencies,
    root.id.toLowerCase(): root,
  };
  final installedById = <String, InstalledMod>{
    for (final mod in installedMods) mod.id.toLowerCase(): mod,
  };
  final prospective = <String, ModManifest>{
    for (final mod in installedMods)
      if (mod.enabled &&
          !mod.uninstallPending &&
          mod.manifest != null &&
          mod.errors.isEmpty &&
          !planned.containsKey(mod.id.toLowerCase()) &&
          _supportsRuntime(
            mod.manifest!,
            gameVersion: gameVersion,
            requireKnownGameVersion: requireKnownGameVersion,
            loaderVersion: loaderVersion,
            sdkVersion: sdkVersion,
          ))
        mod.id.toLowerCase(): mod.manifest!,
    ...planned,
  };
  final entries = prospective.entries.toList()
    ..sort((left, right) => left.key.compareTo(right.key));
  final conflictingIds = <String>{};
  for (var leftIndex = 0; leftIndex < entries.length; leftIndex++) {
    for (
      var rightIndex = leftIndex + 1;
      rightIndex < entries.length;
      rightIndex++
    ) {
      final left = entries[leftIndex];
      final right = entries[rightIndex];
      if (!planned.containsKey(left.key) && !planned.containsKey(right.key)) {
        continue;
      }
      final leftConflict = _declaredConflict(left.value, right.value);
      final rightConflict = _declaredConflict(right.value, left.value);
      if (leftConflict == null && rightConflict == null) {
        continue;
      }
      final reasons = {
        if (leftConflict?.reason.trim().isNotEmpty ?? false)
          leftConflict!.reason.trim(),
        if (rightConflict?.reason.trim().isNotEmpty ?? false)
          rightConflict!.reason.trim(),
      };
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: root.id,
          message:
              'Conflict between ${left.value.name} ${left.value.version} and '
              '${right.value.name} ${right.value.version}'
              '${reasons.isEmpty ? '.' : ': ${reasons.join(' / ')}.'}',
        ),
      );
      for (final entry in [left, right]) {
        if (entry.key == root.id.toLowerCase()) {
          continue;
        }
        final installed = installedById[entry.key];
        if (installed != null && conflictingIds.add(entry.key)) {
          conflictingMods.add(installed);
        }
      }
    }
  }
}

ModConflict? _declaredConflict(ModManifest source, ModManifest target) {
  for (final conflict in source.conflicts) {
    if (conflict.id.toLowerCase() == target.id.toLowerCase() &&
        conflict.versionRange.allows(target.version)) {
      return conflict;
    }
  }
  return null;
}

bool _isSha256(String value) =>
    RegExp(r'^[0-9a-fA-F]{64}$').hasMatch(value.trim());
