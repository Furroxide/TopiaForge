import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('GameCompatStatus gameVersion', () {
    test('round-trips a canonical optional version', () {
      final status = GameCompatStatus.fromJson({
        'status': 'ok',
        'gameVersion': '0.0.2227',
      });

      expect(status.gameVersion, '0.0.2227');
      expect(status.toJson()['gameVersion'], '0.0.2227');
    });

    test('migrates missing, empty, and invalid legacy values to null', () {
      for (final value in <Object?>[null, '', 'not-a-version']) {
        final status = GameCompatStatus.fromJson({
          'status': 'unknown',
          'gameVersion': ?value,
        });

        expect(status.gameVersion, isNull);
        expect(status.toJson(), isNot(contains('gameVersion')));
      }
    });
  });
}
