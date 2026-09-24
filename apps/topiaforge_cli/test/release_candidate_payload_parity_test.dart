import 'package:test/test.dart';

import 'release_candidate_fixture.dart';

void main() {
  test(
    'detached acceptance cannot bless a mod differing from the tested archive',
    () async {
      final fixture = await CandidateFixture.create();
      try {
        expect((await fixture.qualify()).isReady, isTrue);
        final decision = fixture.decision;
        final payloads = (decision['payloads'] as List).cast<Map>();
        final mod = payloads.firstWhere(
          (entry) => (entry['name'] as String).endsWith('.topiaforgemod'),
        );
        final file = fixture.asset(mod['name'] as String);
        file.writeAsStringSync(
          'different mod bytes under the same catalog ID and version',
        );
        mod['sha256'] = fileDigest(file);
        mod['size'] = file.lengthSync();
        fixture.writeDecision(decision);
        fixture.writeAcceptance(fixture.acceptance..['payloads'] = payloads);
        // Both detached files now honestly name the replacement file, but the
        // sealed archive and its game/authoring handoff exercised other bytes.
        await expectLater(fixture.qualify(), throwsStateError);
      } finally {
        fixture.dispose();
      }
    },
  );
}
