import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';

void main() {
  final fixture = readMultiplayerFixture('progress-v2/forward-compat.json');
  for (final item in asObjects(fixture['valid'])) {
    test('${item['id']} normalizes to safe canonical bytes', () {
      final value = LaunchTransport.readOutcome(item['text']! as String);
      expect(LaunchTransport.writeOutcome(value), item['expected']);
      expect(value.multiplayer!.detail, LaunchOutcomeMultiplayer.otherDetail);
      expect(
        LaunchTransport.writeOutcome(
          LaunchTransport.readOutcome(item['expected']! as String),
        ),
        item['expected'],
      );
    });
  }
  for (final item in asObjects(fixture['invalid'])) {
    test('${item['id']} stays invalid without repeating private input', () {
      try {
        LaunchTransport.readOutcome(item['text']! as String);
        fail('Accepted invalid outcome.');
      } on FormatException catch (error) {
        expect(error.source, isNull);
        for (final marker in [
          '203.0.113.77',
          'secret.example.org',
          'fixture-secret',
          'future-private',
        ]) {
          expect(error.toString(), isNot(contains(marker)));
        }
      }
    });
  }
}
