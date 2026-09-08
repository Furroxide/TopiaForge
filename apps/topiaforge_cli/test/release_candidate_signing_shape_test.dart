import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'release_candidate_fixture.dart';

void main() {
  Future<CandidateFixture> unsigned() => CandidateFixture.create(
    mutateContracts: (root) {
      final file = File(p.join(root.path, 'release/release-policy.json'));
      writeObject(
        file,
        readObject(file)
          ..['signingIdentities'] = {'windowsDistribution': 'unsigned'},
      );
    },
  );
  test(
    'explicit synthetic unsigned qualification omits CMS file and digest',
    () async {
      final fixture = await unsigned();
      addTearDown(fixture.dispose);
      expect(fixture.decision.containsKey('handoffSignatureSha256'), isFalse);
      expect(
        fixture.asset('release-handoff-v1.json.p7s').existsSync(),
        isFalse,
      );
      expect((await fixture.qualify()).isReady, isTrue);
    },
  );
  test('unsigned qualification rejects a stray CMS file', () async {
    final fixture = await unsigned();
    addTearDown(fixture.dispose);
    fixture
        .asset('release-handoff-v1.json.p7s')
        .writeAsStringSync('unexpected');
    await expectLater(fixture.qualify(), throwsStateError);
  });
  test(
    'unsigned qualification rejects even a null CMS digest placeholder',
    () async {
      final fixture = await unsigned();
      addTearDown(fixture.dispose);
      fixture.writeDecision(
        fixture.decision..['handoffSignatureSha256'] = null,
      );
      await expectLater(fixture.qualify(), throwsStateError);
    },
  );
  test('signed qualification requires the reviewed CMS digest', () async {
    final fixture = await CandidateFixture.create();
    addTearDown(fixture.dispose);
    fixture.writeDecision(fixture.decision..remove('handoffSignatureSha256'));
    await expectLater(fixture.qualify(), throwsStateError);
  });
}
