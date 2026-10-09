import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge/src/live_acceptance_models.dart';
import 'package:topiaforge/src/live_acceptance_runner.dart';
import 'package:topiaforge/src/live_acceptance_session.dart';
import 'package:launcher_data/launcher_data.dart';

final class AcceptanceFixture {
  AcceptanceFixture({Directory? temporaryRoot})
    : temp = Directory(
        (temporaryRoot ?? Directory.systemTemp)
            .createTempSync('topiaforge-acceptance-test-')
            .resolveSymbolicLinksSync(),
      ) {
    repository = Directory(p.join(temp.path, 'repository'))..createSync();
    game = Directory(p.join(temp.path, 'game'))..createSync();
    sourceGame = Directory(p.join(temp.path, 'source-game'))..createSync();
    output = Directory(p.join(temp.path, 'evidence'));
    package = File(p.join(temp.path, 'acceptance.topiaforgemod'))
      ..writeAsBytesSync(
        acceptancePackageBytes('dev.topiaforge.sdk-acceptance'),
      );
    final tests = Directory(p.join(repository.path, 'tests'))..createSync();
    File(p.join(tests.path, 'live-game-acceptance.json')).writeAsStringSync(
      jsonEncode({
        'schemaVersion': 1,
        'cases': [
          {'id': 'case.one'},
          {'id': 'case.two'},
        ],
      }),
    );
  }

  final Directory temp;
  late final Directory repository;
  late final Directory game;
  late final Directory sourceGame;
  late final Directory output;
  late final File package;

  LiveAcceptanceOptions options({
    String? packagePath,
    List<String> requiredCases = const [],
    Duration timeout = const Duration(seconds: 1),
    bool skipRuntimeInstall = false,
    String devCliPath = '',
    String devProjectPath = '',
    String requiredLoadedPackageId = '',
    String requiredLogMarker = '',
  }) => LiveAcceptanceOptions(
    repositoryRoot: repository.path,
    gameDirectory: sourceGame.path,
    packagePath: packagePath ?? package.path,
    outputDirectory: output.path,
    requiredCases: requiredCases,
    timeout: timeout,
    skipRuntimeInstall: skipRuntimeInstall,
    devCliPath: devCliPath,
    devProjectPath: devProjectPath,
    requiredLoadedPackageId: requiredLoadedPackageId,
    requiredLogMarker: requiredLogMarker,
  );

  AcceptanceIsolationContext admitIsolation(LiveAcceptanceOptions options) {
    final qa = p.join(temp.path, 'qa-profile');
    final low = p.join(qa, 'AppData', 'LocalLow');
    final identity = WindowsAcceptanceIdentity(
      userSid: 'S-1-5-21-42',
      logonId: '0000000000000042',
      sessionId: 2,
      userProfile: qa,
      localAppDataLow: low,
    );
    final record = File(p.join(temp.path, 'isolation.json'));
    if (!record.existsSync()) {
      record.writeAsStringSync(
        jsonEncode({
          'schemaVersion': 1,
          'kind': 'windows-user',
          'sourceGameRoot': sourceGame.path,
          'gameRoot': game.path,
          'launcherRoot': p.join(temp.path, 'launcher'),
          'outputRoot': output.path,
          'persistentDataRoot': p.join(low, 'Vendor', 'Game'),
          'userSid': identity.userSid,
          'userProfile': qa,
          'localAppDataLow': low,
          'normalUserSid': 'S-1-5-21-1',
          'normalUserProfile': p.join(temp.path, 'normal-profile'),
          'reviewerEvidence':
              'Synthetic test fixture; no real identity or game.',
        }),
      );
    }
    return AcceptanceIsolationContext.admit(
      recordPath: record.path,
      sourceGameRoot: options.gameDirectory,
      outputRoot: options.outputDirectory,
      identityReader: () => identity,
    );
  }

  LiveAcceptanceRunner runner({
    required LiveAcceptanceCommandRunner commandRunner,
    LiveAcceptanceProcessRunner? processRunner,
    LiveAcceptanceDelay? delay,
    LiveAcceptanceClock? clock,
    LiveAcceptanceChallengeGenerator? challengeGenerator,
    LiveAcceptanceSessionLauncher? sessionLauncher,
    Duration pollInterval = const Duration(milliseconds: 500),
  }) => LiveAcceptanceRunner(
    commandRunner: commandRunner,
    processRunner: processRunner,
    delay: delay,
    clock: clock,
    challengeGenerator: challengeGenerator,
    pollInterval: pollInterval,
    isolationAdmission: admitIsolation,
    sessionLauncher:
        sessionLauncher ??
        (options, context, challenge) async {
          if (!options.releaseJourneyEnabled) {
            final code = await commandRunner([
              'launch',
              '--game-dir',
              game.path,
              '--target',
              'dev.topiaforge.sdk-acceptance.menu',
            ]);
            if (code != 0) throw StateError('Synthetic launch failed.');
          }
          return FixtureAcceptanceSession();
        },
  );

  void writePassingRun({
    File? acceptancePackage,
    List<String> cases = const ['case.one', 'case.two'],
    String marker = '',
    String journeyPackageId = '',
    String acceptanceLogSource = 'dev.topiaforge.sdk-acceptance',
    String journeyMarkerSource = '',
    String challenge = '',
    bool tamperAcceptanceReceipt = false,
    DateTime? completedAtUtc,
  }) {
    final logs = Directory(p.join(game.path, 'BepInEx', 'TopiaForge', 'logs'))
      ..createSync(recursive: true);
    final activeChallenge = challenge.isNotEmpty
        ? challenge
        : ((configJson()['value'] as Map)['acceptanceChallenge'] as String);
    final timestamp = DateTime.now().toUtc().toIso8601String();
    final lines = [
      if (marker.isNotEmpty)
        '$timestamp [INFO] '
            '[${journeyMarkerSource.isEmpty ? journeyPackageId : journeyMarkerSource}] '
            '$marker',
      for (final caseId in cases)
        '$timestamp [INFO] [$acceptanceLogSource] '
            'TF-ACCEPT|PASS|$activeChallenge|$caseId|ok',
    ];
    File(
      p.join(logs.path, 'manager.log'),
    ).writeAsStringSync('${lines.join('\n')}\n');
    File(p.join(logs.path, 'last-run.json')).writeAsStringSync(
      jsonEncode({
        'schemaVersion': 1,
        'completedAtUtc': (completedAtUtc ?? DateTime.now().toUtc())
            .toIso8601String(),
        'sessionId': 'session-1',
        'rootError': '',
        'packages': [
          {
            'id': 'dev.topiaforge.sdk-acceptance',
            'valid': true,
            'status': 'loaded',
            ..._receiptJson(
              acceptancePackage ?? package,
              tamperCriticalFile: tamperAcceptanceReceipt,
            ),
          },
          if (journeyPackageId.isNotEmpty)
            {
              'id': journeyPackageId,
              'valid': true,
              'status': 'loaded',
              ..._receiptJson(_journeyPackage(journeyPackageId)),
            },
        ],
      }),
    );
  }

  File writeJourneyPackage(String id) {
    final file = _journeyPackage(id);
    file.parent.createSync(recursive: true);
    file.writeAsBytesSync(acceptancePackageBytes(id));
    return file;
  }

  File _journeyPackage(String id) => File(
    p.join(
      temp.path,
      'project',
      'bin',
      'TopiaForgeDev',
      'Release',
      '$id-1.0.0.topiaforgemod',
    ),
  );

  Map<String, Object?> configJson() =>
      jsonDecode(
            File(
              p.join(
                game.path,
                'BepInEx',
                'TopiaForge',
                'config',
                'dev.topiaforge.sdk-acceptance.json',
              ),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;

  Map<String, Object?> evidenceJson() =>
      jsonDecode(
            File(
              p.join(output.path, 'acceptance-result.json'),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;

  void dispose() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  }
}

List<int> acceptancePackageBytes(String id, {String? assemblyContent}) {
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode({
          'schemaVersion': 1,
          'name': id,
          'version': '1.0.0',
          'entryAssembly': 'Mod.dll',
        }),
      ),
    )
    ..addFile(ArchiveFile.string('Mod.dll', assemblyContent ?? 'managed-$id'));
  return ZipEncoder().encode(archive);
}

Map<String, Object?> _receiptJson(
  File package, {
  bool tamperCriticalFile = false,
}) {
  final bytes = package.readAsBytesSync();
  final archive = ZipDecoder().decodeBytes(bytes);
  final entries = {
    for (final entry in archive.files.where((entry) => entry.isFile))
      entry.name: entry,
  };
  final paths = ['Mod.dll', 'topiaforge.mod.json'];
  return {
    'sourceSha256': sha256.convert(bytes).toString(),
    'criticalFiles': [
      for (final path in paths)
        {
          'path': path,
          'sha256': tamperCriticalFile && path == 'Mod.dll'
              ? List.filled(64, '0').join()
              : sha256.convert(entries[path]!.readBytes()!).toString(),
        },
    ],
  };
}

final class FixtureAcceptanceSession implements LiveAcceptanceSession {
  bool confirmed = false;
  bool closed = false;
  @override
  Future<void> confirm(Duration timeout) async {
    confirmed = true;
  }

  @override
  Future<void> close() async {
    closed = true;
  }

  @override
  Map<String, Object?> get isolationEvidence {
    if (!confirmed || !closed) throw StateError('Unconfirmed fixture session.');
    return {
      'kind': 'windows-user',
      'provisioningRecordSha256': '0' * 64,
      'acknowledgementSha256': '1' * 64,
      'acknowledgement': {'fixture': true},
      'processExitConfirmed': true,
    };
  }
}
