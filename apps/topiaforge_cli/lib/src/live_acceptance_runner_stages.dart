part of 'live_acceptance_runner.dart';

extension _AcceptanceStages on LiveAcceptanceRunner {
  LiveAcceptanceSpec _loadAndValidateInputs(LiveAcceptanceOptions options) {
    final specFile = File(
      p.join(options.repositoryRoot, 'tests', 'live-game-acceptance.json'),
    );
    if (!_isRegularFile(specFile)) {
      throw const LiveAcceptanceError(
        'TFACCEPT100',
        'The canonical live acceptance specification is missing.',
        'Run from a complete TopiaForge source checkout.',
      );
    }
    if (options.gameDirectory.trim().isEmpty) {
      throw const LiveAcceptanceError(
        'TFACCEPT101',
        'Robotopia game directory was not supplied.',
        'Set ROBOTOPIA_GAME_DIR or pass --game-dir.',
      );
    }
    if (FileSystemEntity.typeSync(options.gameDirectory, followLinks: true) !=
        FileSystemEntityType.directory) {
      throw LiveAcceptanceError(
        'TFACCEPT102',
        'Robotopia game directory does not exist: ${options.gameDirectory}',
        'Select the installed build-2409 game directory.',
      );
    }
    _validateReleaseJourney(options);
    if (specFile.lengthSync() > LiveAcceptanceRunner._maximumSpecBytes) {
      throw const LiveAcceptanceError(
        'TFACCEPT103',
        'The acceptance specification exceeds its size limit.',
        'Update the harness and specification together.',
      );
    }
    return LiveAcceptanceSpec.decode(specFile.readAsStringSync());
  }

  void _validateReleaseJourney(LiveAcceptanceOptions options) {
    final values = [
      options.devCliPath,
      options.devProjectPath,
      options.requiredLoadedPackageId,
      options.requiredLogMarker,
    ];
    final count = values.where((value) => value.trim().isNotEmpty).length;
    if (count != 0 && count != values.length) {
      throw const LiveAcceptanceError(
        'TFACCEPT105',
        'The release journey is only partially configured.',
        'Supply --dev-cli, --dev-project, --required-loaded-package, and '
            '--required-log-marker together.',
      );
    }
    if (count == 0) return;
    if (options.skipLaunch) {
      throw const LiveAcceptanceError(
        'TFACCEPT106',
        'The release journey cannot prove a load marker when launch is skipped.',
        'Remove --skip-launch or omit all release-journey arguments.',
      );
    }
    if (!_isRegularFile(File(options.devCliPath))) {
      throw LiveAcceptanceError(
        'TFACCEPT107',
        'Packaged CLI does not exist: ${options.devCliPath}',
        'Extract or build the candidate developer payload and pass its CLI '
            'executable.',
      );
    }
    if (FileSystemEntity.typeSync(options.devProjectPath, followLinks: true) !=
        FileSystemEntityType.directory) {
      throw LiveAcceptanceError(
        'TFACCEPT108',
        'Release-generated mod project does not exist: '
            '${options.devProjectPath}',
        'Create it with the packaged CLI `new mod` command outside the '
            'extraction directory.',
      );
    }
  }

  List<String> _resolveRequiredCases(
    LiveAcceptanceOptions options,
    LiveAcceptanceSpec spec,
  ) {
    final requiredCases = options.requireAll || options.requiredCases.isEmpty
        ? spec.caseIds
        : options.requiredCases;
    for (final caseId in requiredCases) {
      if (!spec.caseIds.contains(caseId)) {
        throw LiveAcceptanceError(
          'TFACCEPT104',
          "Unknown required acceptance case '$caseId'.",
          'Use an id from tests/live-game-acceptance.json.',
        );
      }
    }
    return List.unmodifiable({...requiredCases});
  }

  Future<String> _resolvePackage(
    LiveAcceptanceOptions options,
    Directory output,
    AcceptanceIsolationContext isolation,
  ) async {
    var packagePath = options.packagePath;
    if (packagePath.trim().isEmpty) {
      final project = p.join(
        options.repositoryRoot,
        'tests',
        'TopiaForge.SdkAcceptanceMod',
      );
      await _runCliStage([
        'pack',
        '--project',
        project,
        '--output',
        output.path,
        '--configuration',
        'Release',
      ], isolation);
      final candidates =
          output
              .listSync(followLinks: false)
              .whereType<File>()
              .where(
                (file) =>
                    p
                        .basename(file.path)
                        .startsWith('dev.topiaforge.sdk-acceptance-') &&
                    file.path.endsWith('.topiaforgemod') &&
                    _isRegularFile(file),
              )
              .toList()
            ..sort(
              (left, right) =>
                  p.basename(left.path).compareTo(p.basename(right.path)),
            );
      packagePath = candidates.isEmpty ? '' : candidates.last.path;
    }
    if (packagePath.trim().isNotEmpty) {
      packagePath = p.normalize(p.absolute(packagePath));
    }
    if (packagePath.isEmpty || !_isRegularFile(File(packagePath))) {
      throw LiveAcceptanceError(
        'TFACCEPT120',
        'Acceptance package does not exist: $packagePath',
        'Build or provide the SDK acceptance package.',
      );
    }
    return packagePath;
  }

  Future<void> _runCliStage(
    List<String> arguments,
    AcceptanceIsolationContext isolation,
  ) async {
    try {
      isolation.verify();
      final code =
          await (_isolatedCommandRunner?.call(arguments, isolation) ??
              _commandRunner(arguments));
      if (code == 0) return;
    } on LiveAcceptanceError {
      rethrow;
    } on Object catch (error) {
      throw LiveAcceptanceError(
        'TFACCEPT110',
        'CLI stage failed: ${arguments.join(' ')} ($error)',
        'Read the CLI output, repair the detected install, and retry.',
      );
    }
    throw LiveAcceptanceError(
      'TFACCEPT110',
      'CLI stage failed: ${arguments.join(' ')}',
      'Read the CLI output, repair the detected install, and retry.',
    );
  }

  Future<void> _runPackagedStage(
    String executable,
    List<String> arguments,
    AcceptanceIsolationContext isolation,
  ) async {
    try {
      isolation.verify();
      if (await (_processRunner?.call(executable, arguments) ??
              _runProcess(executable, arguments, isolation)) ==
          0) {
        return;
      }
    } on Object catch (error) {
      throw LiveAcceptanceError(
        'TFACCEPT111',
        'Packaged CLI development stage failed: ${arguments.join(' ')} '
            '($error)',
        'Inspect the stable TFDEV diagnostic, repair the release scaffold or '
            'game install, and retry.',
      );
    }
    throw LiveAcceptanceError(
      'TFACCEPT111',
      'Packaged CLI development stage failed: ${arguments.join(' ')}',
      'Inspect the stable TFDEV diagnostic, repair the release scaffold or '
          'game install, and retry.',
    );
  }

  void _writeSchemaOneConfig(Directory configDirectory, String challenge) {
    final fixture = {
      'schemaVersion': 1,
      'value': {
        'acceptanceChallenge': challenge,
        'migratedFromSchema1': false,
        'highContrast': true,
        'uiScale': 1.15,
        'reducedMotion': true,
        'motionIntensity': 0.0,
      },
    };
    final path = p.join(
      configDirectory.path,
      'dev.topiaforge.sdk-acceptance.json',
    );
    requireAcceptanceUnlinkedPath(path);
    File(path).writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert(fixture)}\n',
      flush: true,
    );
  }

  LiveAcceptanceLastRun? _tryReadLastRun(File file) {
    try {
      if (!_isRegularFile(file) ||
          file.lengthSync() > LiveAcceptanceRunner._maximumLastRunBytes) {
        return null;
      }
      return LiveAcceptanceLastRun.tryParse(file.readAsStringSync());
    } on FileSystemException {
      return null;
    }
  }
}
