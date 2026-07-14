part of 'dependency_planner.dart';

class _InstallDependencyPlan {
  const _InstallDependencyPlan({
    required this.actions,
    required this.issues,
    required this.unresolvedIds,
    required this.selectedManifests,
  });

  final List<PackageInstallAction> actions;
  final List<LauncherIssue> issues;
  final Set<String> unresolvedIds;
  final Map<String, ModManifest> selectedManifests;
}

class _DependencyChoice {
  const _DependencyChoice({required this.mod, required this.kind});

  final RegistryMod mod;
  final _DependencyChoiceKind kind;

  String get signature => '${mod.manifest.version}|${kind.name}';
  bool get needsAction => kind != _DependencyChoiceKind.satisfied;
}

enum _DependencyChoiceKind { satisfied, enableOnly, install }

_InstallDependencyPlan _resolveInstallDependencies(
  ModManifest root,
  Map<String, InstalledMod> installed,
  List<RegistryMod> available, {
  String? gameVersion,
  bool requireKnownGameVersion = false,
  String? loaderVersion,
  String? sdkVersion,
}) {
  final validAvailable = available
      .where(
        (mod) =>
            !mod.manifest.validate().any((issue) => issue.isBlocking) &&
            _supportsRuntime(
              mod.manifest,
              gameVersion: gameVersion,
              requireKnownGameVersion: requireKnownGameVersion,
              loaderVersion: loaderVersion,
              sdkVersion: sdkVersion,
            ),
      )
      .toList(growable: false);
  var knownConstraints = <String, List<VersionRange>>{};
  var choices = <String, _DependencyChoice>{};
  var requesters = <String, Set<String>>{};

  for (var pass = 0; pass < 50; pass++) {
    final nextConstraints = <String, List<VersionRange>>{};
    final nextChoices = <String, _DependencyChoice>{};
    final nextRequesters = <String, Set<String>>{};
    final queue = <(ModManifest, ModDependency)>[
      for (final dependency in root.dependencies) (root, dependency),
    ];
    final expanded = <String>{};

    for (var index = 0; index < queue.length; index++) {
      final (requester, dependency) = queue[index];
      final key = dependency.id.toLowerCase();
      nextRequesters.putIfAbsent(key, () => <String>{}).add(requester.name);
      final ranges = nextConstraints.putIfAbsent(key, () => <VersionRange>[]);
      if (!ranges.any(
        (range) => range.toString() == dependency.versionRange.toString(),
      )) {
        ranges.add(dependency.versionRange);
      }
      if (key == root.id.toLowerCase()) {
        continue;
      }

      final allRanges = <VersionRange>[
        ...knownConstraints[key] ?? const [],
        ...ranges,
      ];
      final choice = _chooseDependency(
        key,
        allRanges,
        installed,
        validAvailable,
        gameVersion: gameVersion,
        requireKnownGameVersion: requireKnownGameVersion,
        loaderVersion: loaderVersion,
        sdkVersion: sdkVersion,
      );
      if (choice == null) {
        nextChoices.remove(key);
        continue;
      }
      nextChoices[key] = choice;
      if (!expanded.add('$key|${choice.signature}')) {
        continue;
      }
      queue.addAll(
        choice.mod.manifest.dependencies.map(
          (nested) => (choice.mod.manifest, nested),
        ),
      );
    }

    final stable =
        _constraintSignature(knownConstraints) ==
            _constraintSignature(nextConstraints) &&
        _choiceSignature(choices) == _choiceSignature(nextChoices);
    knownConstraints = nextConstraints;
    choices = nextChoices;
    requesters = nextRequesters;
    if (stable) {
      break;
    }
  }

  final issues = <LauncherIssue>[];
  final unresolved = <String>{};
  for (final entry in knownConstraints.entries) {
    if (choices.containsKey(entry.key)) {
      continue;
    }
    unresolved.add(entry.key);
    final requested = entry.value.map((range) => range.toString()).join(' & ');
    final names = requesters[entry.key]?.join(', ') ?? root.name;
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: root.id,
        message: entry.key == root.id.toLowerCase()
            ? 'Dependency cycle detected while planning $names -> ${root.id}.'
            : 'No installed or source package satisfies ${entry.key} '
                  '$requested (required by $names).',
      ),
    );
  }

  final actions = <PackageInstallAction>[];
  final planned = <String>{};
  final visiting = <String>[root.id.toLowerCase()];
  for (final dependency in root.dependencies) {
    _appendDependencyAction(
      dependency.id.toLowerCase(),
      root.id,
      choices,
      planned,
      visiting,
      actions,
      issues,
    );
  }
  return _InstallDependencyPlan(
    actions: actions,
    issues: issues,
    unresolvedIds: unresolved,
    selectedManifests: {
      for (final entry in choices.entries) entry.key: entry.value.mod.manifest,
    },
  );
}

_DependencyChoice? _chooseDependency(
  String key,
  List<VersionRange> ranges,
  Map<String, InstalledMod> installed,
  List<RegistryMod> available, {
  required String? gameVersion,
  required bool requireKnownGameVersion,
  required String? loaderVersion,
  required String? sdkVersion,
}) {
  bool allows(String version) => ranges.every((range) => range.allows(version));
  final existing = installed[key];
  if (existing != null &&
      allows(existing.version) &&
      _isUsable(existing) &&
      _supportsRuntime(
        existing.manifest!,
        gameVersion: gameVersion,
        requireKnownGameVersion: requireKnownGameVersion,
        loaderVersion: loaderVersion,
        sdkVersion: sdkVersion,
      )) {
    return _DependencyChoice(
      mod: RegistryMod(manifest: existing.manifest!),
      kind: existing.enabled
          ? _DependencyChoiceKind.satisfied
          : _DependencyChoiceKind.enableOnly,
    );
  }

  final options =
      available
          .where(
            (mod) =>
                mod.manifest.id.toLowerCase() == key &&
                allows(mod.manifest.version),
          )
          .toList()
        ..sort(
          (a, b) => _compareDependencyVersions(
            b.manifest.version,
            a.manifest.version,
          ),
        );
  return options.isEmpty
      ? null
      : _DependencyChoice(
          mod: options.first,
          kind: _DependencyChoiceKind.install,
        );
}

void _appendDependencyAction(
  String key,
  String rootId,
  Map<String, _DependencyChoice> choices,
  Set<String> planned,
  List<String> visiting,
  List<PackageInstallAction> actions,
  List<LauncherIssue> issues,
) {
  final choice = choices[key];
  if (choice == null || planned.contains(key)) {
    return;
  }
  final cycleStart = visiting.indexOf(key);
  if (cycleStart >= 0) {
    final cycle = [...visiting.sublist(cycleStart), key].join(' -> ');
    if (!issues.any((issue) => issue.message.contains(cycle))) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: rootId,
          message: 'Dependency cycle detected while planning: $cycle.',
        ),
      );
    }
    return;
  }

  visiting.add(key);
  for (final dependency in choice.mod.manifest.dependencies) {
    _appendDependencyAction(
      dependency.id.toLowerCase(),
      rootId,
      choices,
      planned,
      visiting,
      actions,
      issues,
    );
  }
  visiting.removeLast();
  planned.add(key);
  if (!choice.needsAction) {
    return;
  }
  final enableOnly = choice.kind == _DependencyChoiceKind.enableOnly;
  actions.add(
    PackageInstallAction(
      modId: choice.mod.manifest.id,
      name: choice.mod.manifest.name,
      version: choice.mod.manifest.version,
      packageUrl: enableOnly ? '' : choice.mod.downloadUrl,
      packageSha256: enableOnly ? '' : choice.mod.packageSha256,
      sourceId: enableOnly ? '' : choice.mod.sourceId,
      sourceName: enableOnly ? '' : choice.mod.sourceName,
      enableOnly: enableOnly,
    ),
  );
}

bool _isUsable(InstalledMod mod) =>
    !mod.uninstallPending &&
    mod.manifest != null &&
    mod.errors.isEmpty &&
    !mod.manifest!.validate().any((issue) => issue.isBlocking);

bool _supportsRuntime(
  ModManifest manifest, {
  required String? gameVersion,
  required bool requireKnownGameVersion,
  required String? loaderVersion,
  required String? sdkVersion,
}) {
  return (manifest.gameVersionRange.isAny ||
          ((gameVersion == null || gameVersion.isEmpty)
              ? !requireKnownGameVersion
              : manifest.gameVersionRange.allows(gameVersion))) &&
      (loaderVersion == null ||
          manifest.loaderVersionRange.isAny ||
          manifest.loaderVersionRange.allows(loaderVersion)) &&
      (sdkVersion == null ||
          manifest.sdkVersionRange.isAny ||
          manifest.sdkVersionRange.allows(sdkVersion));
}

List<LauncherIssue> _runtimeCompatibilityIssues(
  ModManifest manifest, {
  required String? gameVersion,
  required bool requireKnownGameVersion,
  required String? loaderVersion,
  required String? sdkVersion,
}) {
  final issues = <LauncherIssue>[];
  if (!manifest.gameVersionRange.isAny) {
    if ((gameVersion == null || gameVersion.isEmpty) &&
        requireKnownGameVersion) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: manifest.id,
          message:
              '${manifest.name} requires a known Robotopia game build in '
              '${manifest.gameVersionRange}, but installed-build.json could '
              'not be verified.',
        ),
      );
    } else if (gameVersion != null &&
        gameVersion.isNotEmpty &&
        !manifest.gameVersionRange.allows(gameVersion)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: manifest.id,
          message:
              '${manifest.name} supports game ${manifest.gameVersionRange}, '
              'not $gameVersion.',
        ),
      );
    }
  }
  if (loaderVersion != null &&
      !manifest.loaderVersionRange.isAny &&
      !manifest.loaderVersionRange.allows(loaderVersion)) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message:
            '${manifest.name} supports loader ${manifest.loaderVersionRange}, '
            'not $loaderVersion.',
      ),
    );
  }
  if (sdkVersion != null &&
      !manifest.sdkVersionRange.isAny &&
      !manifest.sdkVersionRange.allows(sdkVersion)) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message:
            '${manifest.name} supports SDK ${manifest.sdkVersionRange}, not '
            '$sdkVersion.',
      ),
    );
  }
  return issues;
}

int _compareDependencyVersions(String left, String right) {
  final leftVersion = SemanticVersion.tryParse(left);
  final rightVersion = SemanticVersion.tryParse(right);
  if (leftVersion == null || rightVersion == null) {
    return left.compareTo(right);
  }
  return leftVersion.compareTo(rightVersion);
}

String _constraintSignature(Map<String, List<VersionRange>> constraints) {
  final entries = constraints.entries.toList()
    ..sort((a, b) => a.key.compareTo(b.key));
  return entries
      .map(
        (entry) =>
            '${entry.key}:${entry.value.map((range) => range.toString()).join(',')}',
      )
      .join('|');
}

String _choiceSignature(Map<String, _DependencyChoice> choices) {
  final entries = choices.entries.toList()
    ..sort((a, b) => a.key.compareTo(b.key));
  return entries
      .map((entry) => '${entry.key}:${entry.value.signature}')
      .join('|');
}
