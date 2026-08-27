import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_models.dart';
import 'package:topiaforge/src/live_acceptance_runner.dart';

import 'live_acceptance_test_fixture.dart';

void main() {
  late AcceptanceFixture fixture;

  setUp(() => fixture = AcceptanceFixture());
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
      expect(
        (config['value'] as Map)['acceptanceChallenge'],
        matches(RegExp(r'^[0-9a-f]{64}$')),
      );
      expect(fixture.evidenceJson()['schemaVersion'], 2);
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
          fixture.writeJourneyPackage('example.release-journey');
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
              'dev.topiaforge.sdk-acceptance-0.1.0-rc.1.topiaforgemod',
            );
            File(packedPath)
              ..createSync(recursive: true)
              ..writeAsBytesSync(
                acceptancePackageBytes('dev.topiaforge.sdk-acceptance'),
              );
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

  test('interaction timeout starts after the launch stage returns', () async {
    var now = DateTime.now().toUtc();
    final runner = LiveAcceptanceRunner(
      commandRunner: (arguments) async {
        if (arguments.first == 'launch') {
          now = now.add(const Duration(minutes: 5));
          fixture.writePassingRun();
        }
        return 0;
      },
      clock: () => now,
      pollInterval: const Duration(milliseconds: 1),
    );

    final evidence = await runner.run(
      fixture.options(timeout: const Duration(milliseconds: 15)),
    );

    expect(evidence.succeeded, isTrue);
    expect(evidence.passedCases, ['case.one', 'case.two']);
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

  test(
    'ignores spoofed acceptance markers from another logger source',
    () async {
      final runner = LiveAcceptanceRunner(
        commandRunner: (arguments) async {
          if (arguments.first == 'launch') {
            fixture.writePassingRun(acceptanceLogSource: 'example.spoof');
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
      expect(fixture.evidenceJson()['passedCases'], isEmpty);
    },
  );

  test('ignores replayed markers carrying a different challenge', () async {
    final runner = LiveAcceptanceRunner(
      commandRunner: (arguments) async {
        if (arguments.first == 'launch') {
          fixture.writePassingRun(challenge: List.filled(64, '0').join());
        }
        return 0;
      },
      pollInterval: const Duration(milliseconds: 1),
    );

    await expectLater(
      runner.run(fixture.options(timeout: const Duration(milliseconds: 15))),
      throwsA(isA<LiveAcceptanceError>()),
    );
    expect(fixture.evidenceJson()['passedCases'], isEmpty);
  });

  test(
    'rejects last-run source and critical-file receipt mismatches',
    () async {
      final runner = LiveAcceptanceRunner(
        commandRunner: (arguments) async {
          if (arguments.first == 'launch') {
            fixture.writePassingRun(tamperAcceptanceReceipt: true);
          }
          return 0;
        },
        pollInterval: const Duration(milliseconds: 1),
      );

      await expectLater(
        runner.run(fixture.options(timeout: const Duration(milliseconds: 15))),
        throwsA(isA<LiveAcceptanceError>()),
      );
      expect(fixture.evidenceJson()['succeeded'], isFalse);
    },
  );

  test(
    'journey marker must be attributed to the exact generated package',
    () async {
      final cli = File(p.join(fixture.temp.path, 'release', 'topiaforge'))
        ..createSync(recursive: true)
        ..writeAsStringSync('fixture');
      final project = Directory(p.join(fixture.temp.path, 'project'))
        ..createSync();
      final runner = LiveAcceptanceRunner(
        commandRunner: (_) async => 0,
        processRunner: (_, _) async {
          fixture.writeJourneyPackage('example.release-journey');
          fixture.writePassingRun(
            marker: 'Unique journey loaded',
            journeyPackageId: 'example.release-journey',
            journeyMarkerSource: 'example.spoof',
          );
          return 0;
        },
        pollInterval: const Duration(milliseconds: 1),
      );

      await expectLater(
        runner.run(
          fixture.options(
            timeout: const Duration(milliseconds: 15),
            skipRuntimeInstall: true,
            devCliPath: cli.path,
            devProjectPath: project.path,
            requiredLoadedPackageId: 'example.release-journey',
            requiredLogMarker: 'Unique journey loaded',
          ),
        ),
        throwsA(isA<LiveAcceptanceError>()),
      );
      expect(fixture.evidenceJson()['requiredLogMarkerObserved'], isFalse);
    },
  );

  test(
    'rejects a stale last-run session even with current challenge logs',
    () async {
      final runner = LiveAcceptanceRunner(
        commandRunner: (arguments) async {
          if (arguments.first == 'launch') {
            fixture.writePassingRun(
              completedAtUtc: DateTime.now().toUtc().subtract(
                const Duration(minutes: 5),
              ),
            );
          }
          return 0;
        },
        pollInterval: const Duration(milliseconds: 1),
      );

      await expectLater(
        runner.run(fixture.options(timeout: const Duration(milliseconds: 15))),
        throwsA(isA<LiveAcceptanceError>()),
      );
      expect(fixture.evidenceJson()['lastRunSessionId'], isEmpty);
    },
  );

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
