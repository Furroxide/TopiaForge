part of 'topiaforge.dart';

/// `topiaforge check ...` — validation for projects, packages, and registry
/// entries (split out of topiaforge.dart for the file-size cap).
extension _TopiaForgeCheckCommands on _TopiaForgeCli {
  static const _checkPackageUsage =
      'Usage: topiaforge check package <path> [--sha256 hex] '
      '[--entry registry/<id>.json] [--resolve] [--project path]';

  Future<int> _check(List<String> args) async {
    return switch (args.firstOrNull) {
      'project' => _checkProject(
        _option(args, '--project') ?? args.skip(1).firstOrNull,
      ),
      'package' => _checkPackage(args.skip(1).toList()),
      _ => throw UsageError('Usage: topiaforge check project|package [path]'),
    };
  }

  Future<int> _checkProject(String? path) async {
    final workspace = await developerRepository.loadDeveloperWorkspace(
      projectPath: path,
    );
    if (!workspace.hasProject) {
      stdout.writeln('No TopiaForge developer project found.');
      return 1;
    }
    stdout.writeln('${workspace.project!.name} (${workspace.project!.id})');
    stdout.writeln('Dependencies: ${workspace.project!.dependencies.length}');
    stdout.writeln('Lock: ${workspace.lock == null ? 'missing' : 'present'}');
    _printIssues(workspace.issues);
    return workspace.hasBlockingIssues ? 1 : 0;
  }

  Future<int> _checkPackage(List<String> args) async {
    String? path;
    for (var index = 0; index < args.length; index++) {
      final arg = args[index];
      if (arg == '--resolve') {
        continue;
      }
      if (arg.startsWith('--')) {
        index++; // Skip the flag's value.
        continue;
      }
      path = arg;
      break;
    }
    if (path == null) {
      throw UsageError(_checkPackageUsage);
    }

    final ModManifest manifest;
    ModPackageSummary? package;
    if (FileSystemEntity.isDirectorySync(path)) {
      final file = File(p.join(path, 'topiaforge.mod.json'));
      if (!file.existsSync()) {
        stderr.writeln('topiaforge.mod.json was not found in $path.');
        return 1;
      }
      manifest = ModManifest.fromJson(
        readBoundedJsonObjectSync(file, maxBytes: CliFileLimits.manifest),
      );
    } else {
      final file = File(path);
      if (!file.existsSync()) {
        stderr.writeln('Package file does not exist: $path');
        return 1;
      }
      package = readModPackage(
        readBoundedRegularFileSync(file, maxBytes: CliFileLimits.package),
      );
      manifest = package.manifest;
    }

    stdout.writeln('${manifest.name} ${manifest.version} (${manifest.id})');
    if (package != null) {
      final sizeMb = (package.byteLength / (1024 * 1024)).toStringAsFixed(1);
      stdout.writeln('sha256=${package.sha256Hex} ($sizeMb MB)');
    }
    final issues = manifest.validate();
    _printIssues(issues);
    var failed = issues.any((issue) => issue.isBlocking);

    final expectedSha = _option(args, '--sha256');
    if (expectedSha != null) {
      if (package == null) {
        throw UsageError('--sha256 only applies to a packed .topiaforgemod.');
      }
      if (package.sha256Hex != expectedSha.trim().toLowerCase()) {
        stdout.writeln(
          'error: sha256 mismatch — expected ${expectedSha.trim().toLowerCase()}, '
          'got ${package.sha256Hex}.',
        );
        failed = true;
      } else {
        stdout.writeln('sha256 matches.');
      }
    }

    final entryPath = _option(args, '--entry');
    if (entryPath != null) {
      final entryIssues = _entryConsistencyIssues(entryPath, manifest, package);
      _printIssues(entryIssues);
      failed = failed || entryIssues.any((issue) => issue.isBlocking);
    }

    if (args.contains('--resolve')) {
      failed = await _printResolvePlan(args, manifest) || failed;
    }
    return failed ? 1 : 0;
  }

  /// Cross-checks a registry entry file against the package on disk — the
  /// primitive registry CI and modders both use before opening a PR.
  List<LauncherIssue> _entryConsistencyIssues(
    String entryPath,
    ModManifest manifest,
    ModPackageSummary? package,
  ) {
    final file = File(entryPath);
    if (!file.existsSync()) {
      return [
        LauncherIssue(
          severity: IssueSeverity.error,
          message: 'Entry file does not exist: $entryPath',
        ),
      ];
    }
    final RegistryEntryFile entry;
    try {
      entry = RegistryEntryFile.fromJson(
        readBoundedJsonObjectSync(file, maxBytes: CliFileLimits.registryEntry),
      );
    } on Object {
      return const [
        LauncherIssue(
          severity: IssueSeverity.error,
          message: 'Entry file is not a valid JSON object.',
        ),
      ];
    }

    final issues = entry.validate();
    if (entry.id.toLowerCase() != manifest.id.toLowerCase()) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: entry.id,
          message:
              'Entry id "${entry.id}" does not match the package '
              '("${manifest.id}").',
        ),
      );
      return issues;
    }
    final version = entry.versions
        .where((item) => item.version.trim() == manifest.version.trim())
        .firstOrNull;
    if (version == null) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: '${manifest.id}@${manifest.version}',
          message:
              'The entry has no version ${manifest.version} — run '
              '`topiaforge registry add-entry` against this package.',
        ),
      );
      return issues;
    }
    if (package != null && version.packageSha256 != package.sha256Hex) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: '${manifest.id}@${manifest.version}',
          message:
              'Entry packageSha256 does not match this package '
              '(${version.packageSha256} vs ${package.sha256Hex}). '
              'Re-run `topiaforge registry add-entry` against the exact file '
              'you host.',
        ),
      );
    }
    return issues;
  }

  /// Dry-run dependency resolution against the configured package sources.
  /// Returns true when the plan has blocking issues.
  Future<bool> _printResolvePlan(
    List<String> args,
    ModManifest manifest,
  ) async {
    final available = await developerRepository.loadConfiguredRegistryMods(
      projectPath: _option(args, '--project'),
    );
    final plan = const DependencyPlanner().previewInstall(
      manifest,
      const <InstalledMod>[],
      availableMods: available,
      loaderVersion: TopiaForgeRuntimeVersions.loaderVersion,
      sdkVersion: TopiaForgeRuntimeVersions.sdkVersion,
    );
    stdout.writeln('');
    stdout.writeln('Dependency plan (${available.length} mod(s) available):');
    for (final action in plan.installActions) {
      stdout.writeln(
        '  would install ${action.name} ${action.version}'
        '${action.sourceName.isEmpty ? '' : ' from ${action.sourceName}'}',
      );
    }
    if (plan.installActions.isEmpty) {
      stdout.writeln('  (nothing to install)');
    }
    _printIssues(plan.issues);
    return plan.hasBlockingIssues;
  }
}
