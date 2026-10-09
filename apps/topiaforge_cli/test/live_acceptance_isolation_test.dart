import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_models.dart';
import 'package:topiaforge/src/live_acceptance_runner.dart';

import 'live_acceptance_test_fixture.dart';

void main() {
  for (final skipInstall in [false, true]) {
    for (final skipLaunch in [false, true]) {
      test(
        'missing isolation refuses effects (install=$skipInstall launch=$skipLaunch)',
        () async {
          final fixture = AcceptanceFixture();
          addTearDown(fixture.dispose);
          final calls = <List<String>>[];
          final runner = LiveAcceptanceRunner(
            commandRunner: (args) async {
              calls.add(args);
              if (args.first == 'launch') fixture.writePassingRun();
              return 0;
            },
          );
          Object? failure;
          try {
            await runner.run(
              LiveAcceptanceOptions(
                repositoryRoot: fixture.repository.path,
                gameDirectory: fixture.game.path,
                packagePath: fixture.package.path,
                outputDirectory: fixture.output.path,
                timeout: const Duration(milliseconds: 10),
                skipRuntimeInstall: skipInstall,
                skipLaunch: skipLaunch,
              ),
            );
          } on Object catch (error) {
            failure = error;
          }
          expect(
            calls,
            isEmpty,
            reason: 'No stage may run against an unadmitted layout.',
          );
          expect(
            Directory(p.join(fixture.game.path, 'BepInEx')).existsSync(),
            isFalse,
          );
          expect(fixture.output.existsSync(), isFalse);
          expect(
            failure,
            isA<LiveAcceptanceError>().having(
              (error) => error.code,
              'code',
              'TFACCEPT180',
            ),
          );
        },
      );
    }
  }
}
