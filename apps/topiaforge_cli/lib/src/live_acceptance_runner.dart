import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:path/path.dart' as p;
import 'package:launcher_data/launcher_data.dart';

import 'live_acceptance_evidence.dart';
import 'live_acceptance_log_reader.dart';
import 'live_acceptance_models.dart';
import 'live_acceptance_trust.dart';
import 'live_acceptance_session.dart';

part 'live_acceptance_runner_stages.dart';

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
    Future<int> Function(List<String>, AcceptanceIsolationContext)?
    isolatedCommandRunner,
    LiveAcceptanceSessionLauncher? sessionLauncher,
    AcceptanceIsolationContext Function(LiveAcceptanceOptions)?
    isolationAdmission,
    LiveAcceptanceDelay? delay,
    LiveAcceptanceClock? clock,
    LiveAcceptanceChallengeGenerator? challengeGenerator,
    this.pollInterval = const Duration(milliseconds: 500),
  }) : _commandRunner = commandRunner,
       _processRunner = processRunner,
       _isolatedCommandRunner = isolatedCommandRunner,
       _sessionLauncher = sessionLauncher ?? RepositoryAcceptanceSession.launch,
       _isolationAdmission = isolationAdmission,
       _delay = delay ?? Future<void>.delayed,
       _clock = clock ?? _utcNow,
       _challengeGenerator = challengeGenerator ?? _newChallenge;

  static const int _maximumSpecBytes = 2 * 1024 * 1024;
  static const int _maximumLastRunBytes = 16 * 1024 * 1024;

  final LiveAcceptanceCommandRunner _commandRunner;
  final LiveAcceptanceProcessRunner? _processRunner;
  final Future<int> Function(List<String>, AcceptanceIsolationContext)?
  _isolatedCommandRunner;
  final LiveAcceptanceSessionLauncher _sessionLauncher;
  final AcceptanceIsolationContext Function(LiveAcceptanceOptions)?
  _isolationAdmission;
  final LiveAcceptanceDelay _delay;
  final LiveAcceptanceClock _clock;
  final LiveAcceptanceChallengeGenerator _challengeGenerator;
  final Duration pollInterval;

  Future<LiveAcceptanceEvidence> run(LiveAcceptanceOptions options) async {
    final spec = _loadAndValidateInputs(options);
    final requiredCases = _resolveRequiredCases(options, spec);
    final AcceptanceIsolationContext isolation;
    try {
      isolation =
          _isolationAdmission?.call(options) ??
          AcceptanceIsolationContext.admit(
            recordPath: options.isolationRecordPath,
            sourceGameRoot: options.gameDirectory,
            outputRoot: options.outputDirectory,
          );
    } on Object catch (error) {
      throw LiveAcceptanceError(
        'TFACCEPT180',
        '$error',
        'Provide a reviewed isolated Windows QA layout and run inside that user/session or VM. No acceptance stage was started.',
      );
    }
    if (options.skipLaunch) {
      throw const LiveAcceptanceError(
        'TFACCEPT181',
        'Skipped launch cannot establish a new owned isolated acceptance run.',
        'Use an actual isolated launch; inspect historical evidence separately.',
      );
    }
    final challenge = _challengeGenerator();
    if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(challenge)) {
      throw const LiveAcceptanceError(
        'TFACCEPT109',
        'The live acceptance challenge generator returned an invalid value.',
        'Use the default cryptographically secure challenge generator.',
      );
    }

    isolation.verify();
    final output = Directory(isolation.outputRoot)..createSync(recursive: true);
    if (!options.skipRuntimeInstall) {
      await _runCliStage([
        'dev-install',
        '--game-dir',
        isolation.gameRoot,
      ], isolation);
    }

    final packagePath = await _resolvePackage(options, output, isolation);
    final expectedAcceptanceReceipt = readLiveAcceptancePackageReceipt(
      packagePath,
    );
    await _runCliStage(['check', 'package', packagePath], isolation);
    await _runCliStage([
      'install',
      packagePath,
      '--game-dir',
      isolation.gameRoot,
    ], isolation);

    final managerRoot = isolation.managerRoot;
    isolation.verify();
    final configDirectory = Directory(p.join(managerRoot, 'config'))
      ..createSync(recursive: true);
    final logsDirectory = p.join(managerRoot, 'logs');
    final managerLog = File(p.join(logsDirectory, 'manager.log'));
    final lastRunFile = File(p.join(logsDirectory, 'last-run.json'));
    _writeSchemaOneConfig(configDirectory, challenge);

    final logReader = AcceptanceIncrementalLogReader(managerLog);
    final startedAtUtc = _clock().toUtc();
    LiveAcceptancePackageReceipt? expectedJourneyReceipt;
    if (options.releaseJourneyEnabled) {
      await _runPackagedStage(options.devCliPath, [
        'dev',
        '--project',
        options.devProjectPath,
        '--game-dir',
        isolation.gameRoot,
        '--no-launch',
        '--no-tail',
      ], isolation);
      expectedJourneyReceipt = readGeneratedJourneyReceipt(options);
    }
    isolation.verify();
    final session = await _sessionLauncher(options, isolation, challenge);
    try {
      await session.confirm(options.timeout);
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

        final missing = requiredCases.any(
          (caseId) => !observed.contains(caseId),
        );
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

      await session.close();
      final evidence = buildLiveAcceptanceEvidence(
        isolationEvidence: session.isolationEvidence,
        isolatedGameDirectory: isolation.gameRoot,
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
      isolation.verify();
      requireAcceptanceUnlinkedPath(resultPath);
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
    } finally {
      await session.close();
    }
  }
}

bool _isRegularFile(File file) =>
    FileSystemEntity.typeSync(file.path, followLinks: false) ==
    FileSystemEntityType.file;

DateTime _utcNow() => DateTime.now().toUtc();

String _newChallenge() {
  final random = Random.secure();
  final buffer = StringBuffer();
  for (var index = 0; index < 32; index++) {
    buffer.write(random.nextInt(256).toRadixString(16).padLeft(2, '0'));
  }
  return buffer.toString();
}

Future<int> _runProcess(
  String executable,
  List<String> arguments,
  AcceptanceIsolationContext isolation,
) async {
  final process = await Process.start(
    executable,
    arguments,
    mode: ProcessStartMode.inheritStdio,
    environment: {'TOPIAFORGE_DATA_ROOT': isolation.launcherRoot},
  );
  return process.exitCode;
}
