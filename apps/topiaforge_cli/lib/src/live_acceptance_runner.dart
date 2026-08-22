import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:path/path.dart' as p;

import 'live_acceptance_evidence.dart';
import 'live_acceptance_models.dart';
import 'live_acceptance_trust.dart';

typedef LiveAcceptanceCommandRunner = Future<int> Function(List<String> args);
typedef LiveAcceptanceProcessRunner =
    Future<int> Function(String executable, List<String> args);
typedef LiveAcceptanceDelay = Future<void> Function(Duration duration);
typedef LiveAcceptanceClock = DateTime Function();
typedef LiveAcceptanceChallengeGenerator = String Function();

/// Runs the canonical instrumented acceptance journey and writes its evidence.
final class LiveAcceptanceRunner {
  LiveAcceptanceRunner({
    required LiveAcceptanceCommandRunner commandRunner,
    LiveAcceptanceProcessRunner? processRunner,
    LiveAcceptanceDelay? delay,
    LiveAcceptanceClock? clock,
    LiveAcceptanceChallengeGenerator? challengeGenerator,
    this.pollInterval = const Duration(milliseconds: 500),
  }) : _commandRunner = commandRunner,
       _processRunner = processRunner ?? _runProcess,
       _delay = delay ?? Future<void>.delayed,
       _clock = clock ?? _utcNow,
       _challengeGenerator = challengeGenerator ?? _newChallenge;

  static const int _maximumSpecBytes = 2 * 1024 * 1024;
  static const int _maximumLastRunBytes = 16 * 1024 * 1024;

  final LiveAcceptanceCommandRunner _commandRunner;
  final LiveAcceptanceProcessRunner _processRunner;
  final LiveAcceptanceDelay _delay;
  final LiveAcceptanceClock _clock;
  final LiveAcceptanceChallengeGenerator _challengeGenerator;
  final Duration pollInterval;

  Future<LiveAcceptanceEvidence> run(LiveAcceptanceOptions options) async {
    final spec = _loadAndValidateInputs(options);
    final requiredCases = _resolveRequiredCases(options, spec);
    final output = Directory(p.normalize(p.absolute(options.outputDirectory)))
      ..createSync(recursive: true);
    final challenge = _challengeGenerator();
    if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(challenge)) {
      throw const LiveAcceptanceError(
        'TFACCEPT109',
        'The live acceptance challenge generator returned an invalid value.',
        'Use the default cryptographically secure challenge generator.',
      );
    }

    if (!options.skipRuntimeInstall) {
      await _runCliStage(['dev-install', '--game-dir', options.gameDirectory]);
    }

    final packagePath = await _resolvePackage(options, output);
    final expectedAcceptanceReceipt = readLiveAcceptancePackageReceipt(
      packagePath,
    );
    await _runCliStage(['check', 'package', packagePath]);
    await _runCliStage([
      'install',
      packagePath,
      '--game-dir',
      options.gameDirectory,
    ]);

    final managerRoot = p.join(options.gameDirectory, 'BepInEx', 'TopiaForge');
    final configDirectory = Directory(p.join(managerRoot, 'config'))
      ..createSync(recursive: true);
    final logsDirectory = p.join(managerRoot, 'logs');
    final managerLog = File(p.join(logsDirectory, 'manager.log'));
    final lastRunFile = File(p.join(logsDirectory, 'last-run.json'));
    _writeSchemaOneConfig(configDirectory, challenge);

    final logReader = _IncrementalLogReader(managerLog);
    final startedAtUtc = _clock().toUtc();
    LiveAcceptancePackageReceipt? expectedJourneyReceipt;
    if (!options.skipLaunch) {
      if (options.releaseJourneyEnabled) {
        await _runPackagedStage(options.devCliPath, [
          'dev',
          '--project',
          options.devProjectPath,
          '--game-dir',
          options.gameDirectory,
          '--launch',
          '--no-tail',
        ]);
        expectedJourneyReceipt = readGeneratedJourneyReceipt(options);
      } else {
        await _runCliStage(['launch', '--game-dir', options.gameDirectory]);
      }
    }

    final observed = <String>{};
    final failures = <String>[];
    var markerObserved = !options.releaseJourneyEnabled;
    LiveAcceptanceLastRun? lastRun;
    // Building/installing the generated release journey can be substantial.
    // Preserve the pre-launch timestamp for stale last-run rejection, but give
    // the operator the full configured interaction window after launch returns.
    final deadline = _clock().toUtc().add(options.timeout);
    while (_clock().toUtc().isBefore(deadline)) {
      for (final line in await logReader.readNewLines()) {
        final structured = tryParseLiveAcceptanceManagerLine(line);
        if (structured == null) continue;
        if (options.releaseJourneyEnabled &&
            structured.level == 'INFO' &&
            structured.source == options.requiredLoadedPackageId &&
            structured.message == options.requiredLogMarker) {
          markerObserved = true;
        }
        if (structured.source != 'dev.topiaforge.sdk-acceptance') continue;
        final passed = RegExp(
          r'^TF-ACCEPT\|PASS\|([0-9a-f]{64})\|'
          r'([a-z0-9][a-z0-9._-]{0,127})\|([^|\r\n]*)$',
        ).firstMatch(structured.message);
        if (structured.level == 'INFO' &&
            passed != null &&
            passed.group(1) == challenge &&
            requiredCases.contains(passed.group(2))) {
          observed.add(passed.group(2)!);
          continue;
        }
        final failed = RegExp(
          r'^TF-ACCEPT\|FAIL\|([0-9a-f]{64})\|'
          r'([a-z0-9][a-z0-9._-]{0,127})\|([^|\r\n]+)$',
        ).firstMatch(structured.message);
        if (structured.level == 'ERROR' &&
            failed != null &&
            failed.group(1) == challenge &&
            requiredCases.contains(failed.group(2))) {
          failures.add('${failed.group(2)}: ${failed.group(3)}');
        }
      }

      final candidate = _tryReadLastRun(lastRunFile);
      if (candidate != null &&
          !candidate.completedAtUtc.isBefore(
            startedAtUtc.subtract(const Duration(seconds: 2)),
          )) {
        lastRun = candidate;
      }

      final missing = requiredCases.any((caseId) => !observed.contains(caseId));
      final journeyReady =
          !options.releaseJourneyEnabled ||
          (markerObserved &&
              lastRun?.package(options.requiredLoadedPackageId)?.valid ==
                  true &&
              lastRun?.package(options.requiredLoadedPackageId)?.status ==
                  'loaded' &&
              expectedJourneyReceipt != null &&
              lastRun
                      ?.package(options.requiredLoadedPackageId)
                      ?.matchesReceipt(expectedJourneyReceipt) ==
                  true);
      if (!missing && lastRun != null && journeyReady) break;
      await _delay(pollInterval);
    }

    final evidence = buildLiveAcceptanceEvidence(
      options: options,
      startedAtUtc: startedAtUtc,
      completedAtUtc: _clock().toUtc(),
      packagePath: packagePath,
      requiredCases: requiredCases,
      observedCases: observed,
      failures: failures,
      lastRun: lastRun,
      requiredLogMarkerObserved: markerObserved,
      acceptanceChallenge: challenge,
      expectedAcceptanceReceipt: expectedAcceptanceReceipt,
      expectedJourneyReceipt: expectedJourneyReceipt,
    );
    final resultPath = p.join(output.path, 'acceptance-result.json');
    File(resultPath).writeAsStringSync(evidence.encode(), flush: true);
    if (!evidence.succeeded) {
      final details =
          'missing=${evidence.missingCases.join(',')}; '
          'failures=${evidence.failures.join('; ')}; '
          'package=${evidence.acceptancePackageStatus}; '
          'packageReceipt=${evidence.acceptancePackageReceipt != null}; '
          'journeyPackage=${evidence.requiredLoadedPackageStatus}; '
          'journeyReceipt=${evidence.requiredLoadedPackageReceipt != null}; '
          'journeyMarker=${evidence.requiredLogMarkerObserved}';
      throw LiveAcceptanceError(
        'TFACCEPT170',
        'Live acceptance did not complete: $details',
        'Keep Robotopia focused, perform the requested device/UI/item '
            'interactions, inspect manager.log and last-run.json, then retry. '
            'Result: $resultPath',
      );
    }
    return evidence;
  }

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
    if (specFile.lengthSync() > _maximumSpecBytes) {
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
      ]);
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

  Future<void> _runCliStage(List<String> arguments) async {
    try {
      final code = await _commandRunner(arguments);
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
  ) async {
    try {
      if (await _processRunner(executable, arguments) == 0) return;
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
    File(
      p.join(configDirectory.path, 'dev.topiaforge.sdk-acceptance.json'),
    ).writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert(fixture)}\n',
      flush: true,
    );
  }

  LiveAcceptanceLastRun? _tryReadLastRun(File file) {
    try {
      if (!_isRegularFile(file) || file.lengthSync() > _maximumLastRunBytes) {
        return null;
      }
      return LiveAcceptanceLastRun.tryParse(file.readAsStringSync());
    } on FileSystemException {
      return null;
    }
  }

  static bool _isRegularFile(File file) =>
      FileSystemEntity.typeSync(file.path, followLinks: false) ==
      FileSystemEntityType.file;

  static DateTime _utcNow() => DateTime.now().toUtc();

  static String _newChallenge() {
    final random = Random.secure();
    final buffer = StringBuffer();
    for (var index = 0; index < 32; index++) {
      buffer.write(random.nextInt(256).toRadixString(16).padLeft(2, '0'));
    }
    return buffer.toString();
  }

  static Future<int> _runProcess(
    String executable,
    List<String> arguments,
  ) async {
    final process = await Process.start(
      executable,
      arguments,
      mode: ProcessStartMode.inheritStdio,
    );
    return process.exitCode;
  }
}

final class _IncrementalLogReader {
  _IncrementalLogReader(this.file)
    : _offset = file.existsSync() ? file.lengthSync() : 0;

  final File file;
  int _offset;
  String _pending = '';

  Future<List<String>> readNewLines() async {
    try {
      if (!file.existsSync()) return const [];
      final length = file.lengthSync();
      if (_offset > length) {
        _offset = 0;
        _pending = '';
      }
      if (_offset == length) return const [];
      final handle = await file.open();
      List<int> bytes;
      try {
        await handle.setPosition(_offset);
        bytes = await handle.read(length - _offset);
        _offset = await handle.position();
      } finally {
        await handle.close();
      }
      final combined = _pending + utf8.decode(bytes, allowMalformed: true);
      final lines = combined.split('\n');
      _pending = lines.removeLast();
      return lines
          .map(
            (line) =>
                line.endsWith('\r') ? line.substring(0, line.length - 1) : line,
          )
          .toList();
    } on FileSystemException {
      return const [];
    }
  }
}
