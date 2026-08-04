import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:path/path.dart' as p;

import 'creator_acceptance_evidence.dart';
import 'creator_acceptance_models.dart';
import 'creator_persistence_probe.dart';
import 'live_acceptance_models.dart';
import 'live_acceptance_trust.dart';

typedef CreatorCommandRunner = Future<int> Function(List<String> args);
typedef CreatorDelay = Future<void> Function(Duration duration);
typedef CreatorClock = DateTime Function();
typedef CreatorChallengeGenerator = String Function();

/// Drives one interactive Creator workbench acceptance run and writes evidence.
///
/// This mirrors the live acceptance runner: a one-run 64-hex challenge is
/// written into the CreatorTools config, the recorder echoes that challenge
/// in every marker it emits from a real observed workbench transition, and the
/// run is bound to the manager's exact session and package receipt.
final class CreatorAcceptanceRunner {
  CreatorAcceptanceRunner({
    required CreatorCommandRunner commandRunner,
    CreatorDelay? delay,
    CreatorClock? clock,
    CreatorChallengeGenerator? challengeGenerator,
    CreatorPersistenceProbe probe = const CreatorPersistenceProbe(),
    this.pollInterval = const Duration(milliseconds: 500),
  }) : _commandRunner = commandRunner,
       _delay = delay ?? Future<void>.delayed,
       _clock = clock ?? _utcNow,
       _challengeGenerator = challengeGenerator ?? _newChallenge,
       _probe = probe;

  static const int _maximumSpecBytes = 2 * 1024 * 1024;
  static const int _maximumLastRunBytes = 16 * 1024 * 1024;

  final CreatorCommandRunner _commandRunner;
  final CreatorDelay _delay;
  final CreatorClock _clock;
  final CreatorChallengeGenerator _challengeGenerator;
  final CreatorPersistenceProbe _probe;
  final Duration pollInterval;

  Future<CreatorAcceptanceEvidence> run(
    CreatorAcceptanceOptions options,
    String creatorPackagePath,
  ) async {
    final spec = _loadAndValidateInputs(options);
    final requiredCases = _resolveRequiredCases(options, spec);
    final output = Directory(p.normalize(p.absolute(options.outputDirectory)))
      ..createSync(recursive: true);
    final challenge = _challengeGenerator();
    if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(challenge)) {
      throw const CreatorAcceptanceError(
        'TFCREATOR109',
        'The Creator acceptance challenge generator returned an invalid value.',
        'Use the default cryptographically secure challenge generator.',
      );
    }

    if (!options.skipRuntimeInstall) {
      await _runCliStage(['dev-install', '--game-dir', options.gameDirectory]);
    }
    final packagePath = p.normalize(p.absolute(creatorPackagePath));
    if (FileSystemEntity.typeSync(packagePath, followLinks: false) !=
        FileSystemEntityType.file) {
      throw CreatorAcceptanceError(
        'TFCREATOR120',
        'The CreatorTools package does not exist: $packagePath',
        'Build the CreatorTools package and pass --creator-package.',
      );
    }
    final expectedReceipt = readLiveAcceptancePackageReceipt(packagePath);
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
    _writeChallengeConfig(configDirectory, challenge);

    final before = _probe.capture();
    final logReader = CreatorLogReader(managerLog);
    final startedAtUtc = _clock().toUtc();
    if (!options.skipLaunch) {
      await _runCliStage(['launch', '--game-dir', options.gameDirectory]);
    }

    final observed = <String>{};
    final failures = <String>[];
    var lifecycleCycles = 0;
    LiveAcceptanceLastRun? lastRun;
    final deadline = _clock().toUtc().add(options.timeout);
    while (_clock().toUtc().isBefore(deadline)) {
      for (final line in await logReader.readNewLines()) {
        final structured = tryParseLiveAcceptanceManagerLine(line);
        if (structured == null) continue;
        if (structured.source != CreatorAcceptanceOptions.creatorModId) {
          continue;
        }
        final cycle = tryParseCreatorCycle(structured.message);
        if (cycle != null && cycle.challenge == challenge) {
          lifecycleCycles = max(lifecycleCycles, cycle.cycle);
          continue;
        }
        final marker = tryParseCreatorMarker(structured.message);
        if (marker == null ||
            marker.challenge != challenge ||
            !requiredCases.contains(marker.caseId)) {
          continue;
        }
        if (marker.passed && structured.level == 'INFO') {
          observed.add(marker.caseId);
        } else if (!marker.passed && structured.level == 'ERROR') {
          failures.add('${marker.caseId}: ${marker.detail}');
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
      if (!missing &&
          lastRun != null &&
          lifecycleCycles >= spec.minimumLifecycleCycles) {
        break;
      }
      await _delay(pollInterval);
    }

    final after = _probe.capture();
    final persistence = CreatorPersistenceObservation(
      layout: _probe.describeLayout(),
      saveBefore: before.save,
      saveAfter: after.save,
      checkpointBefore: before.checkpoint,
      checkpointAfter: after.checkpoint,
    );

    final evidence = buildCreatorAcceptanceEvidence(
      options: options,
      spec: spec,
      startedAtUtc: startedAtUtc,
      completedAtUtc: _clock().toUtc(),
      requiredCases: requiredCases,
      observedCases: observed,
      failures: failures,
      lastRun: lastRun,
      acceptanceChallenge: challenge,
      lifecycleCycles: lifecycleCycles,
      persistence: persistence,
      expectedCreatorReceipt: expectedReceipt,
    );
    final resultPath = p.join(output.path, 'creator-acceptance-result.json');
    File(resultPath).writeAsStringSync(evidence.encode(), flush: true);
    // Retain the exact pre-images the digests were taken over so the retained
    // bundle is byte-verifiable rather than digest-only.
    final stateDirectory = Directory(p.join(output.path, 'state'))
      ..createSync(recursive: true);
    for (final entry in <String, List<int>>{
      'save-before.bin': before.saveDocumentBytes,
      'save-after.bin': after.saveDocumentBytes,
      'checkpoint-before.bin': before.checkpointDocumentBytes,
      'checkpoint-after.bin': after.checkpointDocumentBytes,
    }.entries) {
      File(
        p.join(stateDirectory.path, entry.key),
      ).writeAsBytesSync(entry.value, flush: true);
    }
    if (!evidence.succeeded) {
      final details =
          'missing=${evidence.missingCases.join(',')}; '
          'failures=${evidence.failures.join('; ')}; '
          'cycles=${evidence.lifecycleCycles}/'
          '${evidence.minimumLifecycleCycles}; '
          'package=${evidence.creatorPackageStatus}; '
          'packageReceipt=${evidence.creatorPackageReceipt != null}; '
          'saveUnchanged=${persistence.saveUnchanged}; '
          'checkpointUnchanged=${persistence.checkpointUnchanged}';
      throw CreatorAcceptanceError(
        'TFCREATOR170',
        'Creator acceptance did not complete: $details',
        'Keep Robotopia focused, perform every requested Creator workbench '
            'interaction, inspect manager.log and last-run.json, then retry. '
            'Result: $resultPath',
      );
    }
    return evidence;
  }

  CreatorAcceptanceSpec _loadAndValidateInputs(
    CreatorAcceptanceOptions options,
  ) {
    final specFile = File(
      p.join(options.repositoryRoot, 'tests', 'live-game-acceptance.json'),
    );
    if (FileSystemEntity.typeSync(specFile.path, followLinks: false) !=
        FileSystemEntityType.file) {
      throw const CreatorAcceptanceError(
        'TFCREATOR100',
        'The canonical live acceptance specification is missing.',
        'Run from a complete TopiaForge source checkout.',
      );
    }
    if (options.gameDirectory.trim().isEmpty) {
      throw const CreatorAcceptanceError(
        'TFCREATOR101',
        'Robotopia game directory was not supplied.',
        'Set ROBOTOPIA_GAME_DIR or pass --game-dir.',
      );
    }
    if (FileSystemEntity.typeSync(options.gameDirectory, followLinks: true) !=
        FileSystemEntityType.directory) {
      throw CreatorAcceptanceError(
        'TFCREATOR102',
        'Robotopia game directory does not exist: ${options.gameDirectory}',
        'Select the installed build-2309 game directory.',
      );
    }
    if (specFile.lengthSync() > _maximumSpecBytes) {
      throw const CreatorAcceptanceError(
        'TFCREATOR103',
        'The acceptance specification exceeds its size limit.',
        'Update the harness and specification together.',
      );
    }
    return CreatorAcceptanceSpec.decode(specFile.readAsStringSync());
  }

  List<String> _resolveRequiredCases(
    CreatorAcceptanceOptions options,
    CreatorAcceptanceSpec spec,
  ) {
    final requiredCases = options.requireAll || options.requiredCases.isEmpty
        ? spec.caseIds
        : options.requiredCases;
    for (final caseId in requiredCases) {
      if (!spec.caseIds.contains(caseId)) {
        throw CreatorAcceptanceError(
          'TFCREATOR104',
          "Unknown required Creator acceptance case '$caseId'.",
          'Use an id from the creatorAcceptance section of '
              'tests/live-game-acceptance.json.',
        );
      }
    }
    // Sort deterministically. The release producer and validator compare this
    // list against the lexicographically sorted inventory with an order
    // sensitive Compare-Object, and the inventory is not stored in sorted
    // order, so emitting spec order would have the producer reject real
    // evidence. passedCases is already sorted for the same reason.
    return List.unmodifiable({...requiredCases}.toList()..sort());
  }

  Future<void> _runCliStage(List<String> arguments) async {
    try {
      if (await _commandRunner(arguments) == 0) return;
    } on CreatorAcceptanceError {
      rethrow;
    } on Object catch (error) {
      throw CreatorAcceptanceError(
        'TFCREATOR110',
        'CLI stage failed: ${arguments.join(' ')} ($error)',
        'Read the CLI output, repair the detected install, and retry.',
      );
    }
    throw CreatorAcceptanceError(
      'TFCREATOR110',
      'CLI stage failed: ${arguments.join(' ')}',
      'Read the CLI output, repair the detected install, and retry.',
    );
  }

  /// Writes the one-run challenge into the CreatorTools config document.
  ///
  /// The recorder refuses to emit any marker unless this value is a 64-hex
  /// string, so evidence cannot be produced by a run the operator did not
  /// provision.
  void _writeChallengeConfig(Directory configDirectory, String challenge) {
    final path = p.join(
      configDirectory.path,
      CreatorAcceptanceOptions.creatorConfigFileName,
    );
    final existing = File(path);
    final document = <String, Object?>{
      'schemaVersion': 1,
      'value': <String, Object?>{},
    };
    if (existing.existsSync()) {
      try {
        final decoded = jsonDecode(existing.readAsStringSync());
        if (decoded is Map && decoded['value'] is Map) {
          document['value'] = Map<String, Object?>.from(
            decoded['value'] as Map,
          );
        }
      } on Object {
        // A malformed prior document is replaced rather than trusted.
      }
    }
    (document['value']! as Map<String, Object?>)['acceptanceChallenge'] =
        challenge;
    (document['value']! as Map<String, Object?>)['enabled'] = true;
    File(path).writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert(document)}\n',
      flush: true,
    );
  }

  LiveAcceptanceLastRun? _tryReadLastRun(File file) {
    try {
      if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
              FileSystemEntityType.file ||
          file.lengthSync() > _maximumLastRunBytes) {
        return null;
      }
      return LiveAcceptanceLastRun.tryParse(file.readAsStringSync());
    } on FileSystemException {
      return null;
    }
  }

  static DateTime _utcNow() => DateTime.now().toUtc();

  static String _newChallenge() {
    final random = Random.secure();
    final buffer = StringBuffer();
    for (var index = 0; index < 32; index++) {
      buffer.write(random.nextInt(256).toRadixString(16).padLeft(2, '0'));
    }
    return buffer.toString();
  }
}

/// Incremental reader over the manager log, shared in shape with the live path.
final class CreatorLogReader {
  CreatorLogReader(this.file)
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
