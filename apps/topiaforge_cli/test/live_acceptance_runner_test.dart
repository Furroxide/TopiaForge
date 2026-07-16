import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_models.dart';
import 'package:topiaforge/src/live_acceptance_runner.dart';

void main() {
  late _AcceptanceFixture fixture;

  setUp(() => fixture = _AcceptanceFixture());
  tearDown(() => fixture.dispose());

  test(
    'runs every canonical case and writes schema-one migration input',
    () async {
      final commands = <List<String>>[];
      final runner = LiveAcceptanceRunner(
        commandRunner: (arguments) async {
          commands.add(arguments);
          if (arguments.first == 'launch') fixture.writePassingRun();
          return 0;
        },
        pollInterval: const Duration(milliseconds: 1),
      );

      final evidence = await runner.run(fixture.options());

      expect(evidence.succeeded, isTrue);
      expect(evidence.requiredCases, ['case.one', 'case.two']);
      expect(evidence.passedCases, ['case.one', 'case.two']);
      expect(
        commands,
        containsAll([
          ['dev-install', '--game-dir', fixture.game.path],
          ['check', 'package', fixture.package.path],
          ['install', fixture.package.path, '--game-dir', fixture.game.path],
          ['launch', '--game-dir', fixture.game.path],
        ]),
      );
      final config = fixture.configJson();
      expect(config['schemaVersion'], 1);
      expect((config['value'] as Map)['migratedFromSchema1'], isFalse);
      expect((config['value'] as Map)['highContrast'], isTrue);
      expect(fixture.evidenceJson()['succeeded'], isTrue);
    },
  );

  test(
    'release journey invokes packaged CLI and proves marker and package',
    () async {
      final packagedArguments = <String>[];
      final cli = File(p.join(fixture.temp.path, 'release', 'topiaforge'))
        ..createSync(recursive: true)
        ..writeAsStringSync('fixture');
      final project = Directory(p.join(fixture.temp.path, 'project'))
        ..createSync();
      final runner = LiveAcceptanceRunner(
        commandRunner: (_) async => 0,
        processRunner: (executable, arguments) async {
          expect(executable, cli.path);
          packagedArguments.addAll(arguments);
          fixture.writePassingRun(
            marker: 'Unique journey loaded',
            journeyPackageId: 'example.release-journey',
          );
          return 0;
        },
        pollInterval: const Duration(milliseconds: 1),
      );

      final evidence = await runner.run(
        fixture.options(
          skipRuntimeInstall: true,
          devCliPath: cli.path,
          devProjectPath: project.path,
          requiredLoadedPackageId: 'example.release-journey',
          requiredLogMarker: 'Unique journey loaded',
        ),
      );

      expect(evidence.succeeded, isTrue);
      expect(evidence.requiredLogMarkerObserved, isTrue);
      expect(evidence.requiredLoadedPackageStatus, 'loaded');
      expect(
        packagedArguments,
        containsAllInOrder(['dev', '--project', project.path]),
      );
      expect(fixture.evidenceJson()['releaseJourneyAuthoringCommandCount'], 2);
    },
  );

  test(
    'packs and selects the canonical acceptance package when omitted',
    () async {
      late String packedPath;
      final commands = <List<String>>[];
      final runner = LiveAcceptanceRunner(
        commandRunner: (arguments) async {
          commands.add(arguments);
          if (arguments.first == 'pack') {
            packedPath = p.join(
              fixture.output.path,
              'dev.topiaforge.sdk-acceptance-1.0.0.topiaforgemod',
            );
            File(packedPath)
              ..createSync(recursive: true)
              ..writeAsStringSync('packed');
          } else if (arguments.first == 'launch') {
            fixture.writePassingRun();
          }
          return 0;
        },
        pollInterval: const Duration(milliseconds: 1),
      );

      final evidence = await runner.run(fixture.options(packagePath: ''));

      expect(evidence.packagePath, packedPath);
      expect(
        commands.singleWhere((arguments) => arguments.first == 'pack'),
        containsAllInOrder(['--configuration', 'Release']),
      );
    },
  );

  test('unknown requested case fails before any CLI stage', () async {
    var commandCalled = false;
    final runner = LiveAcceptanceRunner(
      commandRunner: (_) async {
        commandCalled = true;
        return 0;
      },
    );

    await expectLater(
      runner.run(fixture.options(requiredCases: ['not.canonical'])),
      throwsA(
        isA<LiveAcceptanceError>().having(
          (error) => error.code,
          'code',
          'TFACCEPT104',
        ),
      ),
    );
    expect(commandCalled, isFalse);
  });

  test('partial result is retained before TFACCEPT170 is reported', () async {
    final runner = LiveAcceptanceRunner(
      commandRunner: (arguments) async {
        if (arguments.first == 'launch') {
          fixture.writePassingRun(cases: ['case.one']);
        }
        return 0;
      },
      pollInterval: const Duration(milliseconds: 1),
    );

    await expectLater(
      runner.run(fixture.options(timeout: const Duration(milliseconds: 15))),
      throwsA(
        isA<LiveAcceptanceError>().having(
          (error) => error.code,
          'code',
          'TFACCEPT170',
        ),
      ),
    );
    final evidence = fixture.evidenceJson();
    expect(evidence['succeeded'], isFalse);
    expect(evidence['passedCases'], ['case.one']);
    expect(evidence['missingCases'], ['case.two']);
  });

  test('CLI stage failures keep the stable TFACCEPT110 diagnostic', () async {
    final runner = LiveAcceptanceRunner(commandRunner: (_) async => 9);

    await expectLater(
      runner.run(fixture.options()),
      throwsA(
        isA<LiveAcceptanceError>()
            .having((error) => error.code, 'code', 'TFACCEPT110')
            .having(
              (error) => error.toString(),
              'message',
              contains('Remediation:'),
            ),
      ),
    );
  });

  test('spec parser rejects duplicate and unbounded case collections', () {
    expect(
      () => LiveAcceptanceSpec.fromJson({
        'schemaVersion': 1,
        'cases': [
          {'id': 'duplicate'},
          {'id': 'duplicate'},
        ],
      }),
      throwsA(isA<LiveAcceptanceError>()),
    );
    expect(
      () => LiveAcceptanceSpec.fromJson({
        'schemaVersion': 1,
        'cases': List.generate(513, (index) => {'id': 'case.$index'}),
      }),
      throwsA(isA<LiveAcceptanceError>()),
    );
  });
}

final class _AcceptanceFixture {
  _AcceptanceFixture()
    : temp = Directory.systemTemp.createTempSync(
        'topiaforge-acceptance-test-',
      ) {
    repository = Directory(p.join(temp.path, 'repository'))..createSync();
    game = Directory(p.join(temp.path, 'game'))..createSync();
    output = Directory(p.join(temp.path, 'evidence'));
    package = File(p.join(temp.path, 'acceptance.topiaforgemod'))
      ..writeAsStringSync('package');
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
  }) {
    final logs = Directory(p.join(game.path, 'BepInEx', 'TopiaForge', 'logs'))
      ..createSync(recursive: true);
    final lines = [
      if (marker.isNotEmpty) marker,
      for (final caseId in cases) 'TF-ACCEPT|PASS|$caseId|ok',
    ];
    File(
      p.join(logs.path, 'manager.log'),
    ).writeAsStringSync('${lines.join('\n')}\n');
    File(p.join(logs.path, 'last-run.json')).writeAsStringSync(
      jsonEncode({
        'completedAtUtc': DateTime.now().toUtc().toIso8601String(),
        'sessionId': 'session-1',
        'rootError': '',
        'packages': [
          {
            'id': 'dev.topiaforge.sdk-acceptance',
            'valid': true,
            'status': 'loaded',
          },
          if (journeyPackageId.isNotEmpty)
            {'id': journeyPackageId, 'valid': true, 'status': 'loaded'},
        ],
      }),
    );
  }

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
