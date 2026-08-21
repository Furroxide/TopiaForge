import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge/src/live_acceptance_models.dart';

final class AcceptanceFixture {
  AcceptanceFixture()
    : temp = Directory.systemTemp.createTempSync(
        'topiaforge-acceptance-test-',
      ) {
    repository = Directory(p.join(temp.path, 'repository'))..createSync();
    game = Directory(p.join(temp.path, 'game'))..createSync();
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
    gameDirectory: game.path,
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

  void writePassingRun({
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
              package,
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

List<int> acceptancePackageBytes(String id) {
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
    ..addFile(ArchiveFile.string('Mod.dll', 'managed-$id'));
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
