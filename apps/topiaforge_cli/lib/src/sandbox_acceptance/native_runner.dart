import 'native_screen_oracle.dart';
import 'native_audio_oracle.dart';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import '../live_acceptance_models.dart';
import '../live_acceptance_session.dart';
import 'native_annex.dart';
import 'native_annex_verifier.dart';
import 'native_file_verifier.dart';
import 'native_io.dart';
import 'native_options.dart';
import 'native_run_support.dart';
import 'native_transcript_oracles.dart';
import 'sandbox_json.dart';
import 'sandbox_specification.dart';

Future<Map<String, Object?>> runSandboxNative(SandboxNativePaths paths) async {
  if (!Platform.isWindows) {
    throw StateError('Native Sandbox execution requires admitted Windows QA.');
  }
  if (paths.timeout.inSeconds < 30 || paths.timeout.inSeconds > 14400) {
    throw StateError('Native timeout must be 30 through 14400 seconds.');
  }
  final clock = Stopwatch()..start();
  void budget() {
    if (clock.elapsed >= paths.timeout) {
      throw StateError('Native overall run budget expired.');
    }
  }

  final provisioning = readNativeDocument(paths.isolationRecordPath);
  final context = AcceptanceIsolationContext.admit(
    recordPath: paths.isolationRecordPath,
    sourceGameRoot: sandboxText(provisioning['sourceGameRoot'], 'source game'),
    outputRoot: paths.outputRoot,
  );
  final excluded = sandboxText(
    provisioning['normalUserProfile'],
    'normal profile',
  );
  for (final path in [
    paths.repositoryRoot,
    paths.deviceProfilePath,
    paths.packagesPath,
    paths.brokerPath,
    paths.driverManifestPath,
    paths.specPath,
    if (paths.sourceWorkspacePath.isNotEmpty) paths.sourceWorkspacePath,
  ]) {
    requireAcceptanceUnlinkedPath(path);
    if (sameAcceptancePath(path, excluded) ||
        p.isWithin(excluded.toLowerCase(), path.toLowerCase())) {
      throw StateError(
        'Native QA inputs cannot use the ordinary user profile.',
      );
    }
  }
  final packages = SandboxNativePackageInventory.read(paths.packagesPath);
  final specBytes = readNativeFile(paths.specPath);
  final spec = SandboxSpecification.parse(specBytes);
  final driverBytes = readNativeFile(
    paths.driverManifestPath,
    maximum: 4 * 1024 * 1024,
  );
  final deviceBytes = readNativeFile(paths.deviceProfilePath);
  sandboxDocument(deviceBytes, 'native device profile');
  final brokerHash = nativeFileHash(paths.brokerPath);
  List<int> sourceSnapshot() => paths.sourceWorkspacePath.isEmpty
      ? captureSandboxSourceWorkspace(paths.repositoryRoot)
      : verifySandboxSourceSnapshot(
          paths.repositoryRoot,
          paths.sourceWorkspacePath,
        );
  final sourceBytes = sourceSnapshot();
  decodeNativeSourceWorkspace(sourceBytes, packages.sourceRevision);
  budget();
  final now = DateTime.now().toUtc();
  final random = Random.secure();
  String nonce(int bytes) => List.generate(
    bytes,
    (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
  ).join();
  final runId = 'sandbox-${now.microsecondsSinceEpoch}-${nonce(6)}';
  final challenge = nonce(32);
  final runRoot = nativeChild(context.outputRoot, 'sandbox/$runId');
  requireAcceptanceUnlinkedPath(p.dirname(runRoot));
  Directory(p.dirname(runRoot)).createSync(recursive: true);
  if (Directory(runRoot).existsSync()) {
    throw StateError('Native run identity already exists.');
  }
  Directory(runRoot).createSync();
  writeNativeBytes(nativeChild(runRoot, 'source-workspace.json'), sourceBytes);
  OwnedAcceptanceSession? session;
  SandboxFixtureConfigLease? config;
  Map<String, Object?>? runtime;
  var brokerPid = 0;
  var brokerExit = -1;
  var processStarted = false;
  final failures = <String>[];
  Map<String, Object?>? brokerResult;
  var configRestored = false;
  final started = DateTime.now().toUtc();
  try {
    final repository = LocalLauncherRepository(
      dataRoot: context.launcherRoot,
      repositoryRoot: paths.repositoryRoot,
      knownGamePath: context.gameRoot,
      acceptanceIsolation: context,
      acceptanceChallenge: challenge,
    );
    try {
      context.verify();
      final game = await repository.detectKnownInstall();
      if (game == null) throw StateError('Admitted QA game is unavailable.');
      final repaired = await repository.installOrRepairRuntime(game);
      if (!repaired.ok) {
        throw StateError('Staged QA runtime installation failed.');
      }
      budget();
      for (final package in packages.packages) {
        context.verify();
        await repository.installPackage(
          package.path,
          game,
          expectedSha256: package.receipt.sourceSha256,
        );
        budget();
      }
    } finally {
      await repository.dispose();
    }
    runtime = captureSandboxRuntimeFiles(paths.repositoryRoot, context);
    writeNativeDocument(nativeChild(runRoot, 'runtime-files.json'), runtime);
    config = SandboxFixtureConfigLease.install(context, challenge);
    budget();
    final options = LiveAcceptanceOptions(
      repositoryRoot: paths.repositoryRoot,
      gameDirectory: context.gameRoot,
      outputDirectory: context.outputRoot,
      isolationRecordPath: paths.isolationRecordPath,
      timeout: paths.timeout,
    );
    session = await RepositoryAcceptanceSession.launchTarget(
      options,
      context,
      challenge,
      targetId: 'io.github.furroxide.topiaforge.sandbox.creator.menu',
      enabledMods: packages.packages.map((package) => package.id).toSet(),
    );
    processStarted = true;
    await session.confirm(
      sandboxNativeRemainingBudget(paths.timeout, clock.elapsed),
    );
    budget();
    final managerSession = await waitForSandboxPackageReceipts(
      context,
      packages,
      session,
      started,
      sandboxNativeRemainingBudget(paths.timeout, clock.elapsed),
    );
    final isolation = session.nativeIsolationEvidence;
    final ack = sandboxObject(isolation['acknowledgement'], 'ack');
    final remaining = paths.timeout - clock.elapsed;
    if (remaining.inSeconds < 30) {
      throw StateError('No bounded broker execution budget remains.');
    }
    final request = {
      'schemaVersion': 1,
      'kind': 'sandbox-broker-request-v1',
      'runId': runId,
      'challenge': challenge,
      'managerSessionId': managerSession,
      'process': ack['process'],
      'identity': context.identity.toJson(),
      'gameRoot': context.gameRoot,
      'outputRoot': runRoot,
      'persistentDataRoot': context.persistentDataRoot,
      'isolationRecordPath': paths.isolationRecordPath,
      'isolationRecordSha256': context.recordSha256,
      'acknowledgementSha256': isolation['acknowledgementSha256'],
      'deviceProfilePath': paths.deviceProfilePath,
      'deviceProfileSha256': nativeHash(deviceBytes),
      'driverManifestPath': paths.driverManifestPath,
      'driverManifestSha256': nativeHash(driverBytes),
      'timeoutSeconds': remaining.inSeconds,
    };
    final requestPath = nativeChild(runRoot, 'broker-request.json');
    writeNativeDocument(requestPath, request);
    brokerExit = await runSandboxBroker(
      paths: _withTimeout(paths, remaining),
      requestPath: requestPath,
      runRoot: runRoot,
      onStarted: (pid) => brokerPid = pid,
    );
    final reported = readNativeDocument(
      nativeChild(runRoot, 'broker-result.json'),
    );
    _brokerResultShape(reported, runId, brokerPid);
    brokerResult = reported;
    if (brokerExit != 0) failures.add('Native broker returned a failure exit.');
  } on Object catch (error) {
    failures.add('Native execution failed: $error');
  } finally {
    if (session != null) {
      try {
        await session.close();
      } on Object {
        failures.add('Original owned game exit was not confirmed.');
      }
    }
    if (session == null || session.processExitConfirmed) {
      try {
        config?.restore();
        configRestored = true;
      } on Object {
        failures.add('Fixture config restoration was incomplete.');
      }
    }
  }
  final exitConfirmed =
      !processStarted || session?.processExitConfirmed == true;
  if (session?.isolationConfirmed != true ||
      brokerPid <= 0 ||
      brokerResult == null) {
    final failure = {
      'schemaVersion': 1,
      'kind': 'sandbox-native-run-failure-v1',
      'runId': runId,
      'failureCode': 'native-start-or-broker-unconfirmed',
      'message': failures.isEmpty
          ? 'Native broker output was not established.'
          : failures.join(' '),
      'processStarted': processStarted,
      'cleanup': {
        'inputReleased': brokerPid == 0,
        'fixtureReleased': brokerPid == 0 && exitConfirmed && configRestored,
        'originalProcessExitConfirmed': exitConfirmed,
      },
    };
    writeNativeDocument(nativeChild(runRoot, 'sandbox-failure.json'), failure);
    return {
      'runId': runId,
      'status': 'refused',
      'qualifiesRelease': false,
      'cleanupConfirmed': brokerPid == 0 && exitConfirmed && configRestored,
    };
  }
  try {
    if (nativeHash(sourceSnapshot()) != nativeHash(sourceBytes)) {
      failures.add('Source workspace changed during native execution.');
    }
  } on Object {
    failures.add('Source workspace could not be rechecked after execution.');
  }
  try {
    if (runtime == null ||
        !nativeSame(
          runtime,
          captureSandboxRuntimeFiles(paths.repositoryRoot, context),
        )) {
      failures.add('QA runtime bytes changed during native execution.');
    }
  } on Object {
    failures.add('QA runtime bytes could not be rechecked after execution.');
  }
  final artifacts = <Map<String, Object?>>[
    ...nativeRows(brokerResult['artifacts'], 254),
    for (final relative in ['source-workspace.json', 'runtime-files.json'])
      {
        'path': relative,
        'sha256': nativeFileHash(
          nativeChild(runRoot, relative),
          maximum: nativeSourceLimit,
        ),
        'length': File(nativeChild(runRoot, relative)).lengthSync(),
      },
  ];
  final raw = <String, Object?>{
    'schemaVersion': 1,
    'kind': 'sandbox-workbench-automation-v1',
    'scope': 'supplementary-native-development',
    'runId': runId,
    'sourceRevision': packages.sourceRevision,
    'sourceWorkspaceSha256': nativeHash(sourceBytes),
    'specSha256': spec.sha256Digest,
    'driverManifestSha256': nativeHash(driverBytes),
    'challenge': challenge,
    'startedAtUtc': started.toIso8601String(),
    'completedAtUtc': DateTime.now().toUtc().toIso8601String(),
    'deviceProfileSha256': nativeHash(deviceBytes),
    'isolation': session!.nativeIsolationEvidence,
    'packages': packages.receipts,
    'broker': {'binarySha256': brokerHash, 'processId': brokerPid},
    'transcript': brokerResult['transcript'],
    'artifacts': artifacts,
    'scenarioResults': <Map<String, Object?>>[],
    'cleanup': {
      'inputReleased': brokerResult['inputReleased'],
      'fixtureReleased':
          brokerResult['fixtureReleased'] == true && configRestored,
      'originalProcessExitConfirmed': exitConfirmed,
    },
    'failures': <dynamic>{
      ...failures,
      ...nativeReasons(brokerResult['failures']),
    }.toList(),
  };
  final transcriptPath =
      sandboxObject(brokerResult['transcript'], 'transcript')['path']!
          as String;
  final transcriptBytes = readNativeFile(
    nativeChild(runRoot, transcriptPath),
    maximum: nativeTranscriptLimit,
  );
  final provisional = SandboxNativeAnnex.parse(utf8.encode(jsonEncode(raw)));
  try {
    final audioMeasurements = <String, NativeAudioMeasurement>{};
    final screenMeasurements = <String, NativeScreenMeasurement>{};
    for (final artifact in artifacts) {
      final path = artifact['path']! as String;
      if (path.endsWith('.bmp')) {
        final bytes = readNativeFile(
          nativeChild(runRoot, path),
          maximum: 128 * 1024 * 1024,
        );
        if (nativeHash(bytes) != artifact['sha256'] ||
            bytes.length != artifact['length']) {
          throw StateError('Retained BMP bytes changed before evaluation.');
        }
        screenMeasurements[path] = measureNativeScreen(bytes);
      }
      if (path.endsWith('.wav')) {
        final bytes = readNativeFile(
          nativeChild(runRoot, path),
          maximum: 20 * 1024 * 1024,
        );
        if (nativeHash(bytes) != artifact['sha256'] ||
            bytes.length != artifact['length']) {
          throw StateError('Retained audio bytes changed before evaluation.');
        }
        audioMeasurements[path] = measureNativeAudio(bytes);
      }
    }
    final observed = evaluateSandboxNativeTranscript(
      annex: provisional,
      transcriptBytes: transcriptBytes,
      driverManifestBytes: driverBytes,
      audioMeasurements: audioMeasurements,
      screenMeasurements: screenMeasurements,
      audioEndpointId:
          sandboxObject(
                sandboxDocument(deviceBytes, 'device')['audio'],
                'audio',
              )['endpointId']!
              as String,
    );
    raw['scenarioResults'] = [
      for (final result in observed)
        if (result.status != SandboxNativeStatus.missing)
          {
            'scenarioId': result.scenarioId,
            'status': result.status.name,
            'reason': result.reason,
          },
    ];
  } on Object catch (error) {
    nativeReasons(raw['failures']);
    raw['failures'] = [
      ...(raw['failures']! as List),
      'Transcript verification refused: $error',
    ];
  }
  final annexPath = nativeChild(runRoot, 'sandbox-annex.json');
  writeNativeDocument(annexPath, raw);
  final result = await verifySandboxNativeFiles(_withAnnex(paths, annexPath));
  writeNativeDocument(
    nativeChild(runRoot, 'verification.json'),
    result.toJson(),
  );
  return result.toJson();
}

void _brokerResultShape(Map<String, Object?> result, String runId, int pid) {
  sandboxFields(result, {
    'schemaVersion',
    'kind',
    'runId',
    'processId',
    'transcript',
    'artifacts',
    'inputReleased',
    'fixtureReleased',
    'failures',
    'qualifiesRelease',
  }, 'native broker result');
  if (result['schemaVersion'] is! int ||
      result['schemaVersion'] != 1 ||
      result['kind'] != 'sandbox-broker-result-v1' ||
      result['runId'] != runId ||
      result['processId'] is! int ||
      result['processId'] != pid ||
      result['inputReleased'] is! bool ||
      result['fixtureReleased'] is! bool ||
      result['qualifiesRelease'] != false) {
    throw StateError('Native broker result identity or cleanup is malformed.');
  }
  nativeFile(sandboxObject(result['transcript'], 'transcript'));
  for (final artifact in nativeRows(result['artifacts'], 254)) {
    nativeFile(artifact, length: true);
  }
  nativeReasons(result['failures']);
}

SandboxNativePaths _withTimeout(SandboxNativePaths p, Duration timeout) =>
    SandboxNativePaths(
      repositoryRoot: p.repositoryRoot,
      isolationRecordPath: p.isolationRecordPath,
      deviceProfilePath: p.deviceProfilePath,
      packagesPath: p.packagesPath,
      brokerPath: p.brokerPath,
      driverManifestPath: p.driverManifestPath,
      specPath: p.specPath,
      outputRoot: p.outputRoot,
      sourceWorkspacePath: p.sourceWorkspacePath,
      timeout: timeout,
    );
SandboxNativePaths _withAnnex(SandboxNativePaths p, String annex) =>
    SandboxNativePaths(
      repositoryRoot: p.repositoryRoot,
      isolationRecordPath: p.isolationRecordPath,
      deviceProfilePath: p.deviceProfilePath,
      packagesPath: p.packagesPath,
      brokerPath: p.brokerPath,
      driverManifestPath: p.driverManifestPath,
      specPath: p.specPath,
      outputRoot: p.outputRoot,
      sourceWorkspacePath: p.sourceWorkspacePath,
      timeout: p.timeout,
      annexPath: annex,
    );
