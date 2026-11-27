import 'package:test/test.dart';

import 'release_candidate_fixture.dart';

void main() {
  test('qualification rejects malformed embedded platform payloads', () async {
    final fixture = await CandidateFixture.create(validArchive: false);
    addTearDown(fixture.dispose);
    await expectLater(fixture.qualify(), throwsA(anything));
  });
  test('signed qualification requires its detached signature bytes', () async {
    final fixture = await CandidateFixture.create();
    addTearDown(fixture.dispose);
    fixture.asset('release-handoff-v1.json.p7s').deleteSync();
    await expectLater(fixture.qualify(), throwsA(anything));
  });
  test('qualification rejects replaced detached signature bytes', () async {
    final fixture = await CandidateFixture.create();
    addTearDown(fixture.dispose);
    fixture.asset('release-handoff-v1.json.p7s').writeAsStringSync('replaced');
    await expectLater(fixture.qualify(), throwsA(anything));
  });
}
