import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'release_candidate_fixture.dart';

void main() {
  test(
    'frozen policy cannot redirect game metadata outside its fixed contract path',
    () async {
      final fixture = await CandidateFixture.create(
        mutateContracts: (root) {
          final file = File(p.join(root.path, 'release/release-policy.json'));
          final policy = readObject(file);
          (policy['gameBuild'] as Map)['metadataFile'] =
              './.github/robotopia-game-build.json';
          writeObject(file, policy);
        },
      );
      addTearDown(fixture.dispose);
      await expectLater(fixture.qualify(), throwsStateError);
    },
  );
  test(
    'frozen catalog cannot add a payload without a component identity',
    () async {
      final fixture = await CandidateFixture.create(
        mutateContracts: (root) {
          final file = File(p.join(root.path, 'release/catalog.json'));
          final catalog = readObject(file);
          ((catalog['releases'] as List).single['artifacts'] as List).add(
            'unowned.zip',
          );
          writeObject(file, catalog);
        },
      );
      addTearDown(fixture.dispose);
      await expectLater(fixture.qualify(), throwsStateError);
    },
  );
  test(
    'frozen policy cannot broaden generated metadata into payload files',
    () async {
      final fixture = await CandidateFixture.create(
        mutateContracts: (root) {
          final file = File(p.join(root.path, 'release/release-policy.json'));
          final policy = readObject(file);
          ((policy['artifactPolicy'] as Map)['generatedMetadata'] as List).add(
            'unowned.zip',
          );
          writeObject(file, policy);
        },
      );
      addTearDown(fixture.dispose);
      fixture.asset('unowned.zip').writeAsStringSync('unexpected payload');
      await expectLater(fixture.qualify(), throwsStateError);
    },
  );
}
